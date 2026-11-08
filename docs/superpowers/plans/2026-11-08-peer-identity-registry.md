# Peer identity and agent registry Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the client a peer identity of `(card origin, signing kid)`, a resolver seam for DID/SPIFFE, and a registry keyed by identity that rejects duplicate names and warns on lookalikes (gaps S4, S5).

**Architecture:** New `A2A.Client.Peers` namespace. `A2ACardResolver` builds the identity after signature and pin checks and passes it through `IPeerIdentityResolver`. `AgentRegistry` is a thread-safe in-memory index over `ResolvedAgentCard`.

**Tech Stack:** C# (net8.0, net10.0), xUnit, PublicApiAnalyzers.

**Spec:** `docs/superpowers/specs/2026-11-08-peer-identity-registry-design.md`. Read it before starting a task.

## Global Constraints

- Work only in `src/A2A.Client`, `tests/A2A.Tests`, `README.md`. Do not touch transports or server code.
- Do not change the logic of `AgentCardVerifier`, `CardTrustPolicy` or `CheckPinAsync`.
- `A2A.Client` may reference only `Microsoft.Extensions.Logging.Abstractions`. No new packages.
- Every public member has an XML `<summary>`. Every public member is listed in `src/A2A.Client/PublicAPI.Unshipped.txt` (the build fails with RS0016/RS0017 otherwise; keep the file sorted).
- Public `CancellationToken` parameters are named `cancellationToken`.
- Namespace matches folder: `src/A2A.Client/Peers/*.cs` is `A2A.Client.Peers`.
- Peer strings in logs and exception messages go through `TextSanitizer.ForMessage`.
- Comments and docs in plain, short English. Comments only where the reason is not obvious.
- Build: `dotnet build A2A.slnx -c Release` must end with 0 warnings. Filter tests with `dotnet test tests/A2A.Tests -f net10.0 --filter "FullyQualifiedName~<Name>"`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. A card name with a lone surrogate (`"a\uD800b"`, legal in JSON) must not throw from `Normalize`.
2. A rejected `Register` must leave every index unchanged; a later lookup returns the old entry.
3. Two tenants on one origin with a shared key must not replace each other (`IdentityInUse`).
4. A signed agent later served unsigned must not lose its binding (`IdentityDowngrade`).
5. Re-registering the same lookalike must not log or count again.

Each has a named test below.

---

### Task 1: PeerIdentity, resolver seam, ResolvedAgentCard

**Files:**
- Create: `src/A2A.Client/Peers/PeerIdentity.cs` (`PeerIdentity`, `PeerIdentityAssurance`)
- Create: `src/A2A.Client/Peers/IPeerIdentityResolver.cs` (`IPeerIdentityResolver`, `PeerIdentityContext`, `CardPeerIdentityResolver`)
- Modify: `src/A2A.Client/A2ACardResolver.cs`, `src/A2A.Client/PublicAPI.Unshipped.txt`
- Modify: `tests/A2A.Tests/CardTrustTests.cs`, `IntegrationTests.cs`, `MultiAgentTests.cs` (the `Assert.Equal((origin, kid), x.Identity)` lines become `Assert.Equal(new PeerIdentity(origin, kid), x.Identity)`)
- Test: `tests/A2A.Tests/PeerIdentityTests.cs`

**Interfaces — Produces:**

```csharp
public enum PeerIdentityAssurance { OriginOnly = 0, Verified = 1 }

public sealed record PeerIdentity
{
    public PeerIdentity(string origin, string? keyId = null);   // origin: absolute http/https, else ArgumentException; keyId: empty => ArgumentException
    public string Origin { get; }      // CardTrustPolicy.OriginOf of the input
    public string? KeyId { get; }
    public string? ExternalId { get; init; } // empty or whitespace => ArgumentException
    public PeerIdentityAssurance Assurance { get; } // Verified when KeyId or ExternalId is set
    public override string ToString();
    internal static PeerIdentity FromCard(Uri source, AgentCardVerificationResult verification); // KeyId only when Status == Verified
}

public sealed record PeerIdentityContext(AgentCard Card, Uri Source, AgentCardVerificationResult Verification, PeerIdentity Baseline);

public interface IPeerIdentityResolver
{
    ValueTask<PeerIdentity> ResolveAsync(PeerIdentityContext context, CancellationToken cancellationToken = default);
}

public sealed class CardPeerIdentityResolver : IPeerIdentityResolver
{
    public static CardPeerIdentityResolver Instance { get; }
}
```

`A2ACardResolverOptions.IdentityResolver` (`IPeerIdentityResolver`, default `CardPeerIdentityResolver.Instance`; null-checked in the `A2ACardResolver` constructor like `PinStore`).

`ResolvedAgentCard.Identity` becomes `public PeerIdentity Identity { get; init; } = PeerIdentity.FromCard(Source, Verification);`. Its summary says `name` is display only and that `Identity.Assurance` says how strong it is.

- [ ] **Step 1: Write failing tests in `PeerIdentityTests.cs`**

Use `CardTrustTests.CardServer` (internal, reusable) and the sample card and signing helpers from `SigningTests`/`CardTrustTests` (copy the small `NewKey`/`Set` helpers).

- `Origin_is_normalized`: `new PeerIdentity("https://Agent.Example:443/")` equals `new PeerIdentity("https://agent.example")`.
- `Non_http_origin_is_rejected`: `"ftp://a"`, `"not a url"`, `"file:///x"` throw `ArgumentException`.
- `Empty_key_id_and_external_id_are_rejected`.
- `Assurance_marks_origin_only_as_weaker`: no kid gives `OriginOnly`; kid gives `Verified`; `new PeerIdentity(o) { ExternalId = "did:web:a" }` gives `Verified`.
- `ToString_marks_origin_only`: contains `"origin only"` for no kid; equals `"https://a#k1"` with kid; `"https://a#k1 did:web:a"` with both.
- `Equality_includes_key_and_external_id`.
- `Unsigned_card_has_origin_only_identity` via resolver: `KeyId` null, `OriginOnly`.
- `Verified_card_identity_has_kid` via resolver: `KeyId == "k1"`, `Verified`.
- `Unverified_kid_never_enters_identity`: card signed with an untrusted key, `RequireSignature = false`, status `Unverified`, `Identity.KeyId` null even though `Verification.KeyId` is set.
- `Hand_built_card_gets_identity_from_verification`: `new ResolvedAgentCard(card, "{}", uri, verified)`.
- `Custom_resolver_adds_external_id`: test class `DidResolver` returns `context.Baseline with { ExternalId = "did:web:agent.example" }`; result has it; `Assurance` is `Verified` on an unsigned card.
- `Custom_resolver_cannot_change_origin_or_key`: returns `new PeerIdentity("https://evil.example")` for one test and `new PeerIdentity(origin, "other")` for another; `ResolveAsync` throws `InvalidOperationException`; a later resolve with default-cache (`DefaultCacheDuration = TimeSpan.FromMinutes(5)`) also calls the resolver again (nothing cached).
- `Resolver_is_not_called_for_a_rejected_card`: a counting resolver; card rejected by `RequireSignature = true` (unsigned) and by `PinPolicy.Strict` change; count stays 0 for the rejected call.
- `Resolver_exception_fails_resolution`: resolver throws `new InvalidOperationException("boom")`; the same exception propagates from `ResolveAsync`.
- `Resolver_gets_the_timeout_token`: resolver asserts `cancellationToken.CanBeCanceled`.

- [ ] **Step 2: Run** `dotnet test tests/A2A.Tests -f net10.0 --filter "FullyQualifiedName~PeerIdentityTests"`. Expected: compile failure.

- [ ] **Step 3: Implement.** In `ResolveAsync`, after `CheckPinAsync`:

```csharp
var baseline = PeerIdentity.FromCard(uri, verification);
var identity = await _options.IdentityResolver.ResolveAsync(new PeerIdentityContext(card, uri, verification, baseline), cancellationToken).ConfigureAwait(false);
if (identity is null || identity.Origin != baseline.Origin || identity.KeyId != baseline.KeyId)
    throw new InvalidOperationException($"{_options.IdentityResolver.GetType().Name} must keep the card origin and signing key of the baseline identity; it may only add ExternalId.");
```

Set `Identity = identity` in the result. Compute the pin's `keyId` from the same rule (keep the existing local; do not change pin behaviour). Update the `ResolvedAgentCard` summary. Update the `Identity` lines in existing tests. Update `PublicAPI.Unshipped.txt` (drop the tuple line).

- [ ] **Step 4: Run** the filter above, then `dotnet test tests/A2A.Tests -f net10.0 --filter "FullyQualifiedName~CardTrustTests|FullyQualifiedName~MultiAgentTests|FullyQualifiedName~IntegrationTests|FullyQualifiedName~PackageRulesTests|FullyQualifiedName~NamespaceRulesTests"`. Expected: PASS.

- [ ] **Step 5: Commit** `feat(client): add PeerIdentity and IPeerIdentityResolver`.

---

### Task 2: Agent name normalizer

**Files:**
- Create: `src/A2A.Client/Peers/AgentNameNormalizer.cs` (internal static)
- Test: `tests/A2A.Tests/AgentNameNormalizerTests.cs`

**Interfaces — Produces:**

```csharp
internal static class AgentNameNormalizer
{
    public static string NameKey(string? name, int maxLength);   // "" when nothing remains
    public static string Skeleton(string? name, int maxLength);  // "" when no letters or digits remain
    public static bool IsMixedScript(string? name, int maxLength);
}
```

- [ ] **Step 1: Write failing tests** (use `[Theory]` where natural; `const int Max = 256`).

`NameKey` — same key: `("Agent","AGENT")`, `("Agent","Agent​")`, `("Agent","‮Agent")`, `("Agent","Ａgent")` (fullwidth), `("Travel Agent","  travel \t agent\n")`, `("ﬁle","file")` (ligature). Different key: `("Agent","Аgent")` (Cyrillic А), `("Café","Cafe")`, `("Agent 1","Agent 2")`. Empty: `null`, `""`, `"​​"`, `"  \t"`.

`Skeleton` — same: `("Paypal","Pаypal")` (Cyrillic а), `("paypal","paypa1")`, `("PAYPAL","PAYPAI")`, `("pay-pal","pay pal")`, `("pay_pal","PayPal")`, `("Cafe","Café")`, `("modern","modem")`, `("wide","vvide")`, `("Agent O","Agent 0")`, `("Scope","Ѕсоре")` (Cyrillic). Different: `("Agent 1","Agent 2")`, `("Payments","Pay")`, `("Scheduler","Planner")`. Empty: `"---"`, `"🤖"`.

`IsMixedScript` — true: `"Pаypal"` (Latin+Cyrillic), `"Αgent"` (Greek Α+Latin). False: `"Paypal"`, `"Агент"`, `"Travel Агент"` (scripts differ by token only), `"AI翻訳"`, `"Agent 1"`, `""`.

Safety: `Lone_surrogate_does_not_throw` for `"a\uD800b"` and `"\uDC00"` on all three. `Long_name_is_capped`: 1,000,000 `a` chars returns `NameKey` of length ≤ 256 quickly. `Turkish_culture_does_not_change_keys`: set `CultureInfo.CurrentCulture` to `tr-TR`, `NameKey("TITLE") == "title"`; restore in `finally`.

- [ ] **Step 2: Run** with filter `AgentNameNormalizerTests`. Expected: compile failure.

- [ ] **Step 3: Implement.** Follow spec §3.4. Shared `Prepare`: `TextSanitizer.Sanitize(name ?? "", maxLength)`, rebuild through `EnumerateRunes()` (invalid UTF-16 becomes U+FFFD), fold U+FF01–U+FF5E by −0xFEE0. Use `Rune.ToLowerInvariant`, `Rune.IsLetterOrDigit`, `Rune.GetUnicodeCategory`; drop `NonSpacingMark` and `EnclosingMark` only. Skeleton maps **before** lower-casing because `Η` and `η` differ. Mixed-script tokens split on non letter/digit; Latin = ASCII letters, U+00C0–U+024F, U+1E00–U+1EFF; Greek = U+0370–U+03FF, U+1F00–U+1FFF; Cyrillic = U+0400–U+052F, U+1C80–U+1C8F, U+2DE0–U+2DFF, U+A640–U+A69F.

Confusable map (input → output), exact:

```
Cyrillic: А а→a  В в→b  Е е→e  Ѕ ѕ→s  І→l  і→i  Ј ј→j  К к→k  М м→m  Н н→h  О о→o  Р р→p  С с→c  Т т→t  У у→y  Х х→x  Һ һ→h  ԁ→d  ԛ→q  ԝ→w  Ь ь→b  Ѵ ѵ→v
Greek:    Α α→a  Β β→b  Ε ε→e  Ζ ζ→z  Η→h  η→n  Ι→l  ι→i  Κ κ→k  Μ→m  μ→u  Ν→n  ν→v  Ο ο→o  Ρ ρ→p  Τ τ→t  Υ→y  υ→u  Χ χ→x  ω→w  γ→y  ς→σ
Latin:    I→l  1→l  |→l  0→o  ı→i  ɑ→a  ɡ→g  ƒ→f  ł→l  đ→d  ø Ø→o
```

Write non-ASCII table entries as `\uXXXX` escapes with the visible character in a comment, so a confusable cannot hide in source.

- [ ] **Step 4: Run** with filter `AgentNameNormalizerTests`. Expected: PASS.

- [ ] **Step 5: Commit** `feat(client): add agent name normalization for duplicate and lookalike checks`.

---

### Task 3: AgentRegistry and docs

**Files:**
- Create: `src/A2A.Client/Peers/AgentRegistry.cs` (`AgentRegistry`, `AgentRegistryOptions`, `AgentRegistration`)
- Create: `src/A2A.Client/Peers/AgentRegistrationException.cs` (`AgentRegistrationException`, `AgentRegistrationRejection`)
- Modify: `src/A2A.Client/ClientLog.cs`, `src/A2A.Client/PublicAPI.Unshipped.txt`, `README.md` (Security section, bullet after "Card pinning and untrusted text"), `src/A2A.Client/README.md` (one line)
- Test: `tests/A2A.Tests/AgentRegistryTests.cs`

**Interfaces — Consumes:** Task 1 types, Task 2 `AgentNameNormalizer`. **Produces:**

```csharp
public sealed class AgentRegistryOptions
{
    public int MaxAgents { get; set; } = 10_000;
    public int MaxNameLength { get; set; } = 256;
}

public sealed record AgentRegistration(PeerIdentity Identity, UntrustedText Name, ResolvedAgentCard Card)
{
    public IReadOnlyList<PeerIdentity> LookalikeOf { get; init; } = [];   // at most 16
    public bool MixedScriptName { get; init; }
    public PeerIdentity? PreviousIdentity { get; init; }
}

public enum AgentRegistrationRejection { DuplicateName, InvalidName, IdentityInUse, IdentityDowngrade, RegistryFull }

public sealed class AgentRegistrationException(AgentRegistrationRejection reason, PeerIdentity identity, PeerIdentity? conflictingIdentity, string message)
    : A2AException(A2AErrorCode.InvalidAgentResponse, message)
{
    public AgentRegistrationRejection Reason { get; }
    public PeerIdentity Identity { get; }
    public PeerIdentity? ConflictingIdentity { get; }
}

public sealed class AgentRegistry
{
    public AgentRegistry(AgentRegistryOptions? options = null, ILogger? logger = null);  // MaxAgents, MaxNameLength >= 1
    public int Count { get; }
    public IReadOnlyList<AgentRegistration> Agents { get; }  // snapshot
    public AgentRegistration Register(ResolvedAgentCard resolved);
    public bool TryGet(PeerIdentity identity, [NotNullWhen(true)] out AgentRegistration? registration);
    public bool TryGetByName(string name, [NotNullWhen(true)] out AgentRegistration? registration);
    public bool Remove(PeerIdentity identity);
}
```

`ClientLog`, ids 150-152 (update the summary list: 100-149 `A2ACardResolver`, 150-199 `AgentRegistry`): `AgentNameLookalike` (Warning: name, identity, other count), `AgentMixedScriptName` (Warning: name, identity), `AgentIdentityChanged` (Information: source, old identity, new identity).

- [ ] **Step 1: Write failing tests.** Helper: `Card(name, source, kid?)` builds `new ResolvedAgentCard(new AgentCard{...}, "{}", new Uri(source), new AgentCardVerificationResult(kid is null ? Unsigned : Verified, kid, origin, null))`.

- `Registers_and_finds_by_identity_and_name`.
- `Name_is_not_part_of_identity`: lookup by `new PeerIdentity(origin, kid)` works whatever the name.
- `Duplicate_name_from_another_identity_is_rejected`: reason `DuplicateName`, `ConflictingIdentity` is the first; `TryGetByName` still returns the first.
- `Duplicate_detection_uses_the_name_key`: `"Agent"` vs `"AGENT​"` rejected.
- `Empty_name_is_rejected`: `"​"` gives `InvalidName`.
- `Lookalike_name_is_registered_with_a_warning`: `"Paypal"` then `"Pаypal"` (Cyrillic а): second has `LookalikeOf == [first]`, `MixedScriptName == true`; `Count == 2`; a warning is logged; counter `a2a.registry.name_lookalike` with the origin tag is 1.
- `Numbered_names_do_not_warn`: `"Agent 1"`, `"Agent 2"`, empty `LookalikeOf`.
- `Mixed_script_warns_without_a_collision`.
- `Reregistering_does_not_warn_again` (Review Focus 5): same card twice; log and counter fire once; both results still report `LookalikeOf`.
- `Reregistering_refreshes_the_card`: same identity, new `Card` instance, `TryGet` returns the new one, `Count` 1, `PreviousIdentity` null.
- `Rename_releases_the_old_name`: same identity renamed; old name is free for another agent; new name found.
- `Rename_to_a_taken_name_is_rejected_and_keeps_the_entry` (Review Focus 2): old entry and old name still resolve.
- `Key_rotation_replaces_the_entry`: same source, `k1` then `k2`: `PreviousIdentity` is k1; `TryGet(k1)` false; `TryGetByName` gives k2; `Count` 1; name not reported as duplicate or lookalike.
- `Unsigned_to_signed_upgrade_replaces_the_entry`.
- `Signed_to_unsigned_downgrade_is_rejected` (Review Focus 4): reason `IdentityDowngrade`; entry unchanged.
- `Shared_identity_at_another_source_is_rejected` (Review Focus 3): `https://h/a/.well-known/agent-card.json` and `https://h/b/.well-known/agent-card.json`, same kid, different names: `IdentityInUse`. Same with no kid.
- `Per_tenant_keys_are_separate_agents`: same origin, different sources and kids: both registered.
- `Registry_full_is_rejected_for_new_entries_only`: `MaxAgents = 1`; second identity `RegistryFull`; re-register of the first works; after `Remove`, second works.
- `Lookalike_list_is_capped`: 20 names with one skeleton and different name keys: `"agent"` with one of `-`, `_`, `.`, `:`, a space inserted at each of positions 1 to 4; last result has `LookalikeOf.Count == 16`.
- `Remove_frees_identity_name_and_source`.
- `Lone_surrogate_name_registers` (Review Focus 1).
- `Concurrent_same_name_registrations_have_one_winner`: 64 tasks, distinct identities, same name, exactly one success and 63 `DuplicateName`, `Count` 1.
- `Log_output_sanitizes_the_name`: name with `\n` and a bidi control; captured log text has neither.
- `End_to_end_rotation_through_the_resolver`: `A2ACardResolver` with `TrustedKeys[origin]` holding `k1` and `k2` and a mutable card server; resolve and register `k1` card, switch to `k2` card, resolve (`DefaultCacheDuration = TimeSpan.Zero`) and register; `PreviousIdentity` is k1.
- `End_to_end_forged_name_at_another_origin_is_rejected`: two origins, verified, same name: `DuplicateName`.

- [ ] **Step 2: Run** with filter `AgentRegistryTests`. Expected: compile failure.

- [ ] **Step 3: Implement** with one `lock` and four maps of a private `Entry(AgentRegistration Registration, string NameKey, string Skeleton)`: by identity, by `Uri` source, by name key, by skeleton (`List<Entry>`; skip empty skeletons). Compute keys before taking the lock. Decide in this order, throwing before any mutation:

```
sameId = byIdentity[identity]
sameSource = bySource[source]
if sameId != null and sameId.Source != source  -> IdentityInUse(sameId.Identity)
if sameSource != null and sameSource.Identity != identity and identity.Assurance < sameSource.Assurance -> IdentityDowngrade(sameSource.Identity)
replaced = sameSource
holder = byName[nameKey]
if holder != null and holder is not replaced  -> DuplicateName(holder.Identity)
if replaced == null and Count >= MaxAgents    -> RegistryFull
lookalikes = bySkeleton[skeleton] minus replaced, first 16, identities
unindex(replaced); index(new)
```

Warn (log, counter) only when `replaced is null || replaced.Skeleton != skeleton`, and outside the lock. Set `PreviousIdentity` and log `AgentIdentityChanged` when `replaced.Identity != identity`. `Name = new UntrustedText(card.Name, MaxNameLength)`. `Card.Name` may be null from a hand-built card: treat as empty. Exception messages use sanitized names. Class summary states the `InvariantGlobalization` limit and the shared-key rule.

README bullet: identity is `PeerIdentity` (`Origin`, `KeyId`, `Assurance`; `OriginOnly` is weaker), `IPeerIdentityResolver` via `A2ACardResolverOptions.IdentityResolver` (may add `ExternalId`, never change origin or key), `AgentRegistry` rules (duplicates rejected, lookalikes warned via `LookalikeOf`, `MixedScriptName`, log, `a2a.registry.name_lookalike`; rotation replaces; downgrade and shared-identity rejected; per-tenant keys needed on shared origins), plus a 5-line usage sample. Update the existing "identity is `(origin, kid)`" wording.

- [ ] **Step 4: Run** filter `AgentRegistryTests`, then `dotnet build A2A.slnx -c Release` (0 warnings) and `dotnet test tests/A2A.Tests -f net10.0`. Expected: PASS.

- [ ] **Step 5: Commit** `feat(client): add AgentRegistry keyed by peer identity`.
