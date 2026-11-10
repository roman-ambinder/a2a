# Peer identity and the agent registry

A client that talks to many agents needs to know who each one is. This guide is for hosts that route calls between agents, keep a list of known agents, or send credentials to them. It shows what the SDK treats as identity, and how the registry stops one agent from posing as another.

## The card name is not identity

The `name` in an Agent Card is display text. Any agent can set it to anything. A host that routes by name can be fooled by a second agent with the same name, or one that only looks the same (`Paypal` with a Cyrillic `а`, or `Paypa1`).

The SDK identifies a peer by where its card came from and which key signed it. The name is never part of that.

## PeerIdentity

<xref:A2A.Client.ResolvedAgentCard> has an `Identity` property of type <xref:A2A.Client.Peers.PeerIdentity>.

[!code-csharp[](../../../samples/ClientFeatures/Program.cs#Read)]

| Property | Meaning |
|---|---|
| `Origin` | The origin of the card URL: `scheme://host[:port]`, lower case. |
| `KeyId` | The `kid` of the trusted key that verified the card. `null` otherwise. |
| `ExternalId` | An optional DID or SPIFFE id, added by an `IPeerIdentityResolver`. |
| `Assurance` | How strong the identity is. |

`KeyId` is set only when the card was `Verified` against a key you trust (see [agent card signing](agent-card-signing.md)). An unsigned card, or an unverified one that your policy let through, has no `KeyId`. A `kid` from an unverified signature never enters the identity.

There are two assurance levels:

- `OriginOnly`: no key and no external id. Anyone who controls the origin is this peer. So is anyone who reaches the client through a compromised route on plain HTTP.
- `Verified`: `KeyId` or `ExternalId` is set.

`ToString()` is made for logs and marks the weak case: `https://a (origin only)`, `https://a#k1`, or `https://a#k1 did:web:a`.

A runnable version, which reads the identity of an unsigned card, is in [samples/ClientFeatures](https://github.com/roman-ambinder/a2a/blob/main/samples/ClientFeatures/README.md).

A `PeerIdentity` is a record. `Origin` and `KeyId` have no setter, so a `with` expression can only add `ExternalId`. A hand-built `ResolvedAgentCard` gets its identity from its source and verification result.

## Add your own proof: IPeerIdentityResolver

Set <xref:A2A.Client.Peers.IPeerIdentityResolver> in `A2ACardResolverOptions.IdentityResolver` to add a DID or SPIFFE id. The default resolver returns the baseline (origin and key) unchanged.

[!code-csharp[](../../../samples/Snippets/PeerIdentity.cs#Resolver)]

[!code-csharp[](../../../samples/Snippets/PeerIdentity.cs#UseResolver)]

A resolver may add. It may not change.

- It runs only after the signature and pin checks pass. It cannot rescue a card that was rejected.
- It gets a `PeerIdentityContext` with the card, its source, the verification result and the baseline identity.
- Its result must keep the baseline's `Origin` and `KeyId`. If it does not, resolution fails with `InvalidOperationException`. That is a bug in the host, not a fault of the peer.
- `ExternalId` lifts the assurance to `Verified`. Set it only after a real check.
- An exception from the resolver fails the resolution, and nothing is cached.

## AgentRegistry

<xref:A2A.Client.Peers.AgentRegistry> is an in-memory, thread-safe list of peers, keyed by `PeerIdentity`. It does not fetch anything. You resolve a card, then register it. You can use it without DI.

[!code-csharp[](../../../samples/Snippets/AgentCardSigning.cs#Registry)]

`Register` returns an `AgentRegistration`:

- `Identity` and `Resolved` (the card).
- `Name`, the card name as `UntrustedText`, for display.
- `LookalikeOf`: up to 16 other identities whose names look like this one.
- `MixedScriptName`: the name mixes Latin, Greek or Cyrillic letters in one word.
- `PreviousIdentity`: set only on the call that replaced an older identity at the same card URL.

The registry also has `TryGet(identity)`, `TryGetByName(name)`, `Remove(identity)`, `Count` and `Agents` (a snapshot). `AgentRegistryOptions` sets `MaxAgents` (default 10,000) and `MaxNameLength` (default 256, the part of a name that is compared).

`TryGetByName` is safe only because names are unique in the registry. Check the identity before you send credentials:

[!code-csharp[](../../../samples/Snippets/PeerIdentity.cs#ByName)]

### What Register decides

| Case | Result |
|---|---|
| New identity, new card URL, free name | Added. |
| Same identity, same card URL | Refreshed. A changed name is checked like a new name. |
| Same card URL, new identity, assurance not lower | Replaced. `PreviousIdentity` is set and event 152 is logged. |
| Same card URL, new identity, assurance lower | Refused: `IdentityDowngrade`. |
| Same identity, another card URL | Refused: `IdentityInUse`. |
| Name already held by another entry | Refused: `DuplicateName`. The first one registered wins. |
| Name empty after cleaning | Refused: `InvalidName`. |
| New entry beyond `MaxAgents` | Refused: `RegistryFull`. |

A refused card throws <xref:A2A.Client.Peers.AgentRegistrationException> and leaves the registry unchanged. The exception has `Reason`, `Identity` (the refused card) and `ConflictingIdentity` (the registered peer behind the refusal, if any). It is an `A2AException` with `InvalidAgentResponse`. Its message holds only cleaned text from the peer.

[!code-csharp[](../../../samples/Snippets/PeerIdentity.cs#Rejections)]

**Rotation.** A new signing key is a new identity, because the identity includes the key. If the registry treated that as a new agent, it would refuse the name at every rotation. So a new identity at the same card URL replaces the old entry, as long as its assurance is not lower. The new key already passed your trust policy.

**Downgrade.** A signed agent that is now served unsigned is refused. At one card URL, the assurance can stay or go up, never down.

**One identity, two URLs.** Two agents on one origin that share a key, or have none, have the same identity. Tenants at `/{tenant}/.well-known/agent-card.json` are the usual case. The registry cannot tell them apart, so it refuses the second one rather than let one tenant replace another. Give each tenant its own signing key, which gives a distinct `kid`. See [agent card signing](agent-card-signing.md) and [multi-tenancy](multi-tenancy.md).

### How names are compared

Names come from peers, so they are cleaned and cut to `MaxNameLength` first.

- **Duplicates.** Two names are the same when they match after Unicode NFKC normalization, lower casing, white space collapse, removal of invisible characters and folding of fullwidth letters. `Agent`, `AGENT` and `Ａgent` are one name. Long names are cut before comparing, so two long names with the same start can collide. That fails closed.
- **Lookalikes.** The registry reduces each name to a skeleton: no accents, look-alike Cyrillic, Greek and Latin letters mapped to one Latin letter, only letters and digits kept, then `rn` becomes `m` and `vv` becomes `w`. `Paypal`, `Pаypal` (Cyrillic а), `Paypa1` and `pay-pal` share a skeleton. A lookalike is registered with a warning. It is not refused. `Agent 1` and `Agent 2` do not warn: there is no edit distance.
- **Mixed script.** A word with letters from two of Latin, Greek and Cyrillic is flagged, even if nothing collides.

The table of look-alike letters is a curated list, not all of Unicode UTS #39. With `InvariantGlobalization`, .NET's `Normalize` does nothing. Then only fullwidth ASCII is folded.

### Warnings, logs and the counter

A warning is raised when an entry is new or its name changed. A refresh of an unchanged name stays quiet.

| Event id | Level | When |
|---|---|---|
| 150 | Warning | The name looks like the names of other registered agents. |
| 151 | Warning | The name mixes Latin, Greek or Cyrillic letters in one word. |
| 152 | Information | The identity at a card URL changed (rotation or upgrade). |

The counter `a2a.registry.name_lookalike` (meter `A2A`, tag `origin`) counts lookalike collisions only. A mixed-script name with no collision is logged, not counted. Pass an `ILogger` to the `AgentRegistry` constructor to get the log lines. See [observability](observability.md#metrics).

To fail closed, check `LookalikeOf` and `MixedScriptName` after `Register` and call `Remove`, as the snippet above shows.

### Revoking a key

Remove the key from `CardTrustPolicy` and call `AgentRegistry.Remove`. The resolver checks a cached card against the current policy on every resolve, so the card is fetched again and the policy decides.

## What can go wrong

- **`DuplicateName` for an agent you trust.** Another peer registered the name first. Remove the first entry, or have one of the agents rename itself.
- **`IdentityInUse` for the second tenant on a host.** The tenants share a key (or have none). Give each tenant its own signing key.
- **`IdentityDowngrade` after an agent changed.** The agent was signed and now is served without a verified signature. Look at the key setup on both sides. Call `Remove` first only if you accept the weaker identity.
- **`InvalidOperationException` while resolving a card.** Your `IPeerIdentityResolver` returned an identity with another `Origin` or `KeyId`. Return `context.Baseline with { ExternalId = ... }`.
- **No warning for a name that looks wrong.** Lookalike detection covers a curated set of letters and works only against names already in the registry. A lookalike of an agent you never registered is not found.
- **The registry is empty after a restart.** It is in memory. There is no durable store and no revocation protocol.

## See also

- [Agent card signing](agent-card-signing.md)
- [Client credentials](client-credentials.md)
- [Multi-tenancy](multi-tenancy.md)
- [Observability](observability.md)
- [Spec decisions](spec-decisions.md)
