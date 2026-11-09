# Per-skill authorization

Date: 2026-11-09. Builds on skill selection (S35, `2026-11-08-s35-skill-selection-design.md`), which left authorization by skill as a non-goal.

## 1. Goal

A host can allow a caller one skill and deny another. The server enforces it before the agent runs, the same way on JSON-RPC, REST and gRPC. The extended card can list only the caller's skills. An agent that picks the skill itself can ask for the same decision.

## 2. Decisions

**D1. A request names its skill with `Message.metadata["skillId"]`.** A2A v1.0 has no field for this, and upstream epic #1989 is still open. S35 already defined this key, so a client uses `A2AClient.SelectSkill` for both selection and authorization. Only the message's metadata counts, not `SendMessageRequest.metadata`.

**D2. Enforcement does not need the `A2A-Extensions` header or a card entry.** S35 D5 ignores the key without both, because it might be someone's private metadata. That reasoning does not fit here: the host has turned on a security feature, and a check a caller can skip by leaving out a header is no check. With a mode on, the key always has this meaning.

**D3. One seam: `A2AAuthorizationHandler.AuthorizeSkillAsync(call, skillId, ct)`.** A new virtual on the existing handler, not a second service. A host keeps its authorization in one class, and adding a virtual breaks nobody. **The default returns false.** Turning on a mode without overriding it denies everything, which is loud and safe. An allow-by-default here would look enforced and do nothing.

**D4. `A2AServerOptions.SkillAuthorization` holds `Mode` and `FilterExtendedCard`.** Host config, not the card (unlike S35 D4): a security rule must not depend on what a card says. Modes:

| Mode | No skill named | Skill named |
| --- | --- | --- |
| `Off` (default) | runs | runs; the handler's skill method is not called |
| `WhenNamed` | runs; the agent routes and must ask (D8) | checked |
| `Required` | refused | checked |

The mode is per host. A handler can still decide per tenant from `call.Tenant`.

**D5. The check is part of the authorization step.** It lives behind `AuthorizationFilter.RequireAsync`, after the operation check, for `SendMessage` and `SendStreamingMessage` only. So it runs on every binding, before any `AfterAuthorization` filter, before idempotency replay and before anything is stored. An idempotent retry is checked again, so a revoked caller cannot replay.

**D6. Outcomes.**

- Skill not on the card, or denied by the handler: the handler-denial path, with no difference between the two: `PermissionDenied`, or `TaskNotFound` when the message names a task (the rule `A2AAuthorizationHandler` already documents). The caller cannot tell a hidden skill from a missing one, which is why this must run before `SkillSelectionValidator`.
- `Required` with no skill (absent or empty): `InvalidParams`, metadata `extension` = the URI. It reveals nothing, and tells the client what to fix.
- Non-string or unreadable value, in either mode: `InvalidParams`, same metadata. S35 ignores these; a security mode refuses what it cannot read.
- Null message with a mode on: `InvalidParams`.
- Tenant with no agent: the existing no-agent refusal (`RequestCards.RequireAgentAsync`).
- A throw from the handler or a card provider propagates. Nothing is allowed by accident.

**D7. A skill is known if it is on the tenant's public card, or on the extended card under S35 D7's rules** (authenticated, and authorized for `GetExtendedAgentCard`). Share that lookup with `SkillSelectionValidator`. The known check runs first, so the handler only ever sees real skill ids, never attacker text. Ids compare ordinally.

**D8. Agents read and ask through `AgentRequestContext`.**

- `AuthorizedSkillId`: the id the server checked on this message. Null with mode `Off` or no skill named. Unlike `RequestedSkillId` it is an authorization result, and it is the one to branch on. It reads an internal value on `ServerCallContext` set when the check passes, so a later change to the message cannot alter it.
- `IsSkillAllowedAsync(skillId, ct)`: same decision as D6 (known, then handler), in any mode. False for an unknown skill. False when no authorizer is attached (a context built by hand). Null or empty id: `ArgumentException`.

**D9. Open streams.** Every `StreamReauthInterval`, a `SendStreamingMessage` stream with an `AuthorizedSkillId` is asked again through `IsSkillAllowedAsync`'s logic. A false ends it like any failed re-authorization.

**D10. Extended card.** With `FilterExtendedCard` true, `GetExtendedAgentCard` keeps only skills the handler allows. It works in any mode. The provider's card is never changed: when a skill is removed, the result is a copy through the wire format, with `Signatures` cleared because they no longer match. When nothing is removed, the provider's card is returned. A throw fails the call instead of showing an unfiltered card. Filtering happens only in that operation, so D7 sees the full card.

**D11. Follow-ups are checked per message,** matching S35 D10. In `Required` mode a follow-up must name its skill again. No skill is stored on the task: no new data on the wire, and no way for an old grant to outlive a revocation.

## 3. Non-goals

- Filtering the public card. It is shared and signed. Put private skills on the extended card only.
- A skill check on `GetTask`, `CancelTask`, `SubscribeToTask`, push config operations or a `Last-Event-ID` resume. They act on the caller's own task and start no skill. Owner checks and `AuthorizeAsync` still apply.
- Server-started work with no incoming message (continuations).
- A wire change. A request without the key under `Off` is unchanged.

## 4. Tests

Theory over JSON-RPC and REST, and a gRPC test file: denied, allowed, unknown (same error as denied), `Required` with none, malformed, streaming, follow-up, retry after revoke, no header needed, handler not called for unknown ids, handler throw fails closed, default handler denies, filtered card, signatures cleared, provider card unchanged, per-tenant cards, stream re-authorization (short interval, handler signals; `LimitStreamAsync` uses `Stopwatch`, as in `LimitsTests`), `IsSkillAllowedAsync` and `AuthorizedSkillId`.
