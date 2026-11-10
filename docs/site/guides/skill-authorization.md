# Skill authorization

An agent can offer several skills. With skill authorization, the server decides which caller may use which skill. It checks before your agent runs, the same way on JSON-RPC, REST and gRPC. Read this if some of your skills are not for everyone.

It is off by default. With nothing turned on, every request behaves as before.

## How a request names its skill

The client puts the skill id in `metadata["skillId"]` of the message. This is the same key as [skill selection](skill-selection.md), so `A2AClient.SelectSkill` works for both.

A2A v1.0 has no standard field for this. The key is an SDK choice, and it may move if the upstream epic #1989 lands. Only the metadata of the message counts, not the metadata of the request.

When a mode is on, the server reads the key always. It does not need the `A2A-Extensions` header or an extension entry on the card. A check that a caller could skip by leaving out a header would be no check.

## Turn it on

Write a handler, then pick a mode:

[!code-csharp[](../../../samples/Snippets/SkillAuthorization.cs#Handler)]

[!code-csharp[](../../../samples/Snippets/SkillAuthorization.cs#Register)]

A runnable version, with a skill that one caller may use and another may not, is in [samples/TenantAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/TenantAgent/README.md).

`A2AAuthorizationHandler.AuthorizeSkillAsync` is the one place you decide. It is a virtual method on the handler you already use for [authorization](auth-and-ownership.md#extra-rules-a2aauthorizationhandler). The default returns `false`. If you turn a mode on and forget to override it, every skill is denied. That is loud and safe.

The handler only gets ids that are on the agent's card. It never sees text the caller invented.

## The modes

| Mode | Message names no skill | Message names a skill |
| --- | --- | --- |
| `Off` (default) | runs | runs. The handler is not asked. |
| `WhenNamed` | runs | checked |
| `Required` | refused | checked |

The mode is per host. To decide per tenant, read `call.Tenant` in your handler.

The check covers `SendMessage` and `SendStreamingMessage`. It runs in the authorization step, before any filter that runs after authorization, before an idempotent replay and before anything is stored. A retry is checked again, so a caller that lost access cannot replay a message.

### What WhenNamed relies on

In `WhenNamed`, a message that names no skill reaches your agent. If your agent picks the skill itself, the server cannot know which one. So your agent must ask, with `AgentRequestContext.IsSkillAllowedAsync`, before it uses a skill:

[!code-csharp[](../../../samples/Snippets/SkillAuthorization.cs#Executor)]

`IsSkillAllowedAsync` makes the same decision as the server: the skill must be on the card, and the handler must allow it. It works in any mode. It returns false for an unknown skill, and when the context was built by hand. A null or empty id throws `ArgumentException`.

`AuthorizedSkillId` is the id the server checked for this message. It is null when the mode is `Off` or no skill was named. Branch on `AuthorizedSkillId`, not on `RequestedSkillId`. `RequestedSkillId` is only what the caller wrote.

Use `Required` if you do not want to rely on the agent.

## What the caller sees

| Case | Error |
| --- | --- |
| The skill is denied by the handler | `PermissionDenied`, or `TaskNotFound` when the message names a task |
| The skill is not on the card | the same error, with the same text |
| `Required`, and no skill | `InvalidParams` |
| The value is not a string, or is unreadable text (any mode) | `InvalidParams` |
| The message is missing (any mode but `Off`) | `InvalidParams` |
| The tenant has no agent | the usual "no agent" refusal |

A hidden skill and a missing skill give the identical error. So a caller cannot find out which skills exist. For this reason the check runs before the `rejectUnknownSkills` check of skill selection.

The `InvalidParams` errors carry the skill-selection extension URI in the error metadata, so a client knows what to fix.

If your handler or a card provider throws, the call fails. Nothing is allowed by accident.

## Follow-up messages

A follow-up is checked like any other message. In `Required` mode, it must name the skill again. The skill is not stored on the task. So a grant cannot outlive its revocation.

## Open streams

A `SendStreamingMessage` stream that was authorized for a skill asks the handler again every `StreamReauthInterval` (see [limits](limits-and-rate-limiting.md)). If the answer is no, the stream ends, like any failed re-authorization. The task keeps running.

## The extended card

Set `FilterExtendedCard` to list only the skills the handler allows on the extended card. It works in any mode.

- Your provider's card is never changed. When a skill is removed, the caller gets a copy.
- The copy has no `Signatures`, because they no longer match.
- When nothing is removed, the caller gets the provider's card as it is.
- If the handler throws, the call fails. It never shows the full card instead.

The public card is not filtered. It is shared with everyone and may be signed. Put private skills on the extended card only.

## What is not checked

- The public card. See above.
- `GetTask`, `CancelTask`, `SubscribeToTask`, push config operations and a `Last-Event-ID` resume. They act on the caller's own task and start no skill. Owner checks and `AuthorizeAsync` still apply.
- Work that the server starts with no incoming message, such as a [continuation](proactive-continuation.md).
- A message that names no skill in `WhenNamed` mode. Your agent must ask.

Nothing changes on the wire. A request without the key, under `Off`, is as before.

## What can go wrong

- Every skill is denied. You turned a mode on and did not override `AuthorizeSkillAsync`. The default denies.
- A skill is denied though the handler allows it. The id is not on the card the caller sees, or only on the extended card and the caller may not call `GetExtendedAgentCard`.
- A caller runs a skill in `WhenNamed` without a check. The message named no skill, and your agent did not call `IsSkillAllowedAsync`. Use `Required`, or ask.
- `Required` refuses a follow-up. It did not name the skill again.
- A client gets `InvalidParams` for a skill it named. The value is not a string, or the skill is empty in `Required` mode.
- The extended card still shows a skill. `FilterExtendedCard` is off, or the handler allows it.
- A skill is refused because of its spelling. Ids must match exactly, including case.

## See also

- [Skill selection](skill-selection.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Spec decisions](spec-decisions.md)
