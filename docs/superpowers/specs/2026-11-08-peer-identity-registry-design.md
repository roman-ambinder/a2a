# Peer identity and client agent registry (gaps S4, S5)

Date: 2026-11-08. Status: decided (routine decisions recorded here, marked "my call").
Builds on P0 (`2026-11-07-p0-security-correctness-design.md`) and the design principles. Nothing here relaxes verification, pinning or `CardTrustPolicy`.

## 1. Problem

- S4: the SDK has no identity primitive. `ResolvedAgentCard.Identity` is a bare tuple with no way to mark how strong it is, and no way to plug in DID or SPIFFE.
- S5: `AgentCard.name` is display text anyone can set. A host that routes by name can be spoofed by a second agent with the same or a lookalike name.

## 2. Scope

In: `PeerIdentity` of `(card origin, signing kid)`; `IPeerIdentityResolver` seam; client-side `AgentRegistry` keyed by identity, rejecting duplicate names and warning on lookalikes; `ResolvedAgentCard.Identity` becomes `PeerIdentity`.

Out: registry discovery over the network (S36); a DID or SPIFFE implementation (seam only); a revocation protocol; durable registry storage; DI registration.

To revoke a key, remove it from `CardTrustPolicy` and call `AgentRegistry.Remove`. This works because `A2ACardResolver` re-checks a cached card against the live policy on the fresh-cache and 304 paths (`RawJson` is parsed again). A result whose status or `kid` changed, or which the policy now rejects, is dropped and the card is fetched again, so the normal path decides (my call: the 304 path otherwise extends a stale verification with no limit). Cost: one signature check per cached resolve, and a JWKS outage now fails a cached resolve the same way it fails a fresh one.

## 3. Types

All new public types live in `A2A.Client.Peers` (folder `src/A2A.Client/Peers`). In `A2A.Client` because identity is derived during client card resolution, and `A2A.Protocol` stays pure data (my call).

### 3.1 PeerIdentity

Sealed record: `Origin` (normalized with `CardTrustPolicy.OriginOf`, http or https only), `KeyId` (`null` unless a trusted key verified the card), `ExternalId` (optional DID or SPIFFE ID, `init`), `Assurance` (computed). The card's `name` is never part of it. `Origin` and `KeyId` have no setter, so `with` can only add `ExternalId`.

`PeerIdentityAssurance`: `OriginOnly = 0` (no key and no external proof: anyone who controls the origin, or reaches the client through a compromised route on plain HTTP, is this peer), `Verified = 1` (`KeyId` or `ExternalId` is set).

An unsigned card and an `Unverified` card that policy let through are both `OriginOnly`. A `kid` from an unverified signature never enters the identity.

`ToString` is for logs and marks the weak case: `https://a (origin only)`, `https://a#k1`, and with an external id `https://a#k1 did:web:a`. The `kid` goes through `TextSanitizer.ForMessage`.

### 3.2 IPeerIdentityResolver

`ValueTask<PeerIdentity> ResolveAsync(PeerIdentityContext context, CancellationToken cancellationToken = default)`. `PeerIdentityContext` carries `Card`, `Source`, `Verification` and `Baseline` (the `(origin, kid)` identity). The default, `CardPeerIdentityResolver.Instance`, returns `Baseline`. Set through `A2ACardResolverOptions.IdentityResolver`.

Rule: **a resolver may add, never change.** It runs only after signature and pin checks pass, so it cannot rescue a rejected card. The result must have the baseline's `Origin` and `KeyId`; otherwise resolution fails with `InvalidOperationException`, because that is a host bug, not a peer fault. A plug-in returns `context.Baseline with { ExternalId = "did:web:..." }` after checking its own proof. An `ExternalId` lifts assurance to `Verified`, so a resolver sets it only after a real check. Exceptions from a resolver propagate and fail resolution; nothing is cached.

### 3.3 AgentRegistry

Sealed, thread-safe, in memory, usable without DI. One entry per identity.

`Register(ResolvedAgentCard)` returns `AgentRegistration` or throws `AgentRegistrationException` with a `Reason`. A rejected call leaves the registry unchanged. Also `TryGet(PeerIdentity, out)`, `TryGetByName(string, out)`, `Remove(PeerIdentity)`, `Count`, `Agents` (snapshot).

`TryGetByName` is safe only because names are unique here. Hosts should still check `Identity` before sending credentials.

| Case | Result |
|---|---|
| New identity, new source, free name | Added |
| Same identity, same source | Refreshed; a name change is checked like a new name |
| Same source, new identity, assurance not lower (key rotation, or unsigned to signed) | Entry replaced, `PreviousIdentity` set, logged |
| Same source, lower assurance (signed agent now served unsigned) | `IdentityDowngrade` |
| Same identity, different source | `IdentityInUse` |
| Name key held by another entry | `DuplicateName`; first registered wins |
| Name empty after sanitizing | `InvalidName` |
| New entry beyond `MaxAgents` (default 10,000) | `RegistryFull` |

**Why rotation is allowed.** The registry key is `(origin, kid)`, so a rotated key is a new identity. Treating it as a duplicate name would break every host at each rotation. The source URL is unchanged and the new key passed `CardTrustPolicy`, so this is the same peer.

**Why `IdentityInUse`.** Two agents on one origin with the same key (or no key), such as tenants at `/{tenant}/.well-known/agent-card.json` that share the host key, have the same identity. The registry cannot tell them apart, so it refuses the second rather than letting one tenant replace another. Fix: per-tenant signing keys (`TenantCardProvider.GetSigningKeyAsync`), which give distinct kids.

### 3.4 Name comparison

Internal `AgentNameNormalizer`. All inputs go through `TextSanitizer.Sanitize(name, MaxNameLength)` first (default 256), then invalid surrogates and Unicode noncharacters (U+FDD0 to U+FDEF, and U+xFFFE/U+xFFFF of every plane) become U+FFFD because `string.Normalize` can throw on them, then invisible characters the sanitizer keeps are dropped, then fullwidth ASCII folds to ASCII. If `Normalize` still throws, the name counts as empty (`InvalidName`), so a hostile name cannot raise anything but `AgentRegistrationException`.

Dropped invisibles: `UnicodeCategory.Format`, U+034F, U+115F, U+1160, U+17B4, U+17B5, U+180B to U+180F, U+2800, U+3164, U+FE00 to U+FE0F, U+FFA0, U+E0100 to U+E01EF. This is in the normalizer, not `TextSanitizer`, so display text does not change.

- **Name key** (duplicates): NFKC, invariant lower case, whitespace collapsed, trimmed. `Agent`, `AGENT`, `Agent​`, `Ａgent` are one name. Truncation can make two long names collide; that fails closed.
- **Skeleton** (lookalikes): NFKD, drop combining marks, map confusables to Latin, lower case, keep letters and digits only, then `rn`→`m` and `vv`→`w`. `Pаypal` (Cyrillic а), `Paypa1`, `pay-pal` and `Café`/`Cafe` share a skeleton with their Latin forms.
- **Mixed script**: one token holds letters from two of Latin, Greek, Cyrillic. Flagged even with no collision. Combining marks do not split a token.

The confusable table is curated (Cyrillic, Greek, Latin lookalikes), not all of UTS #39 (my call: no new dependency, `A2A.Client` may reference only Logging.Abstractions). No edit distance: `Agent 1` and `Agent 2` must not warn (my call).

Limit: with `InvariantGlobalization`, .NET's `Normalize` does nothing, so compatibility forms other than fullwidth ASCII are not folded. Documented on `AgentRegistry`.

### 3.5 Warnings

Lookalikes warn and do not reject (per S5). `AgentRegistration.LookalikeOf` lists up to 16 other identities (capped so stored lists stay small under a flood), `MixedScriptName` flags the name. When the entry is new or its name key changed, so periodic re-registration does not spam, a lookalike or mixed-script name is logged at Warning. Only a lookalike collision is counted, on `a2a.registry.name_lookalike` (meter `A2A`, tag `origin`). A host that wants to fail closed removes the entry. The name is logged through `TextSanitizer.ForMessage`.

## 4. Breaking change

`ResolvedAgentCard.Identity` changes from a tuple to `PeerIdentity`. Nothing is on NuGet (principles: renames are fine until first publish). The property defaults from `Source` and `Verification`, so a hand-built `ResolvedAgentCard` is correct.

## 5. Tests

Second `IPeerIdentityResolver` in tests (principles anti-goal: no untested seams). End-to-end through `A2ACardResolver` with signed cards, including rotation. Package and namespace rules stay green.
