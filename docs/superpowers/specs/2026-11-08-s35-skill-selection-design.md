# Client-directed skill selection (S35)

Date: 2026-11-08. Status: implemented by the plan `docs/superpowers/plans/2026-11-08-s35-skill-selection.md`.
Gap: S35 in `docs/2026-11-07-a2a-gap-analysis.md`.

## 1. Goal

A client that knows which skill of a multi-skill agent it wants can say so. An executor can read the choice. An agent can opt in to refuse a skill it does not have. Plain A2A v1.0 peers see no change.

## 2. What upstream says

Epic A2A #1989 (open, `v1.1-candidate`, read 2026-11-08) wants an optional skill hint on send, defined behaviour for an unknown skill, and a strictly additive change. Proposal #1812 (closed into the epic) puts `skill_id` on `SendMessageConfiguration`: omitted or empty means nothing changes, a known id is an advisory hint, an unknown id is an invalid-params style error. Nothing is in the proto, so a field is not available. This SDK uses the extension route the gap analysis names: an extension URI plus `metadata["skillId"]` on the message.

## 3. Decisions (all my call)

**D1. Model lives in `A2A.Protocol`, namespace `A2A.Skills`.** Client and server both need it, and it is pure data. `SkillSelection` holds the constants and `CreateExtension`; `SkillSelectionMessageExtensions` reads and writes the message. No new package: four small members do not justify one.

**D2. Key and URI are constants.** `SkillSelection.MetadataKey` = `skillId`. `SkillSelection.ExtensionUri` = `https://github.com/roman-ambinder/a2a/extensions/skill-selection/v1`, the same community form as the actor-chain URI, and free to change until something is published. The SDK does not add the extension to cards by itself, so existing cards and their signatures do not change.

**D3. A value is a non-empty string.** Empty is the same as absent (#1812). `Message.GetRequestedSkillId()` returns null for an absent, empty or non-string value and never throws. Ids are compared ordinally, with no trimming.

**D4. The opt-in lives on the card.** `SkillSelection.CreateExtension(rejectUnknownSkills: true)` writes `params.rejectUnknownSkills = true` on the extension entry. `SkillSelection.RejectsUnknownSkills(card)` reads it. Why the card and not an `A2AServerOptions` switch: per-tenant cards then get per-agent behaviour for free, a client can see the policy before it calls, and it uses the existing `AgentExtension.Params` seam. No new option type.

**D5. No rejection without both sides agreeing.** The server refuses only when the caller activated the extension (S31: requested in `A2A-Extensions` and declared on the card) and the agent opted in (D4). Otherwise `skillId` might be someone's private metadata: it is ignored, with no error. Without the opt-in, a malformed value under an activated extension is also ignored: this differs from the actor chain's D4 on purpose, because the brief is that rejection needs the agent's opt-in.

**D6. Refusals.** With both sides agreed, a non-string value and a skill not on the card are `InvalidParams`, before the agent runs and with nothing stored, on SendMessage and SendStreamingMessage on all bindings. Metadata `extension` names the URI, as `ExtensionSupportRequired` does. The message does not repeat the caller's value and does not list the card's skills.

**D7. Extended cards count.** A skill may be only on the extended card. On a miss against the public card, if the card declares an extended card, the caller is authenticated, and the caller is authorized for GetExtendedAgentCard (the host's `A2AAuthorizationHandler`), `IAgentCardProvider.GetExtendedAsync` is consulted. A denial gives the normal refusal. An unauthenticated caller is refused, so an anonymous caller cannot probe the extended card. The provider runs only on a miss under opt-in, so the hot path does not pay for it. A provider failure propagates.

**D8. Validation order.** `SkillSelectionValidator` runs in `PrepareAsync`, after `ActorChainValidator` and the media type check. It is the only place that needs the card: a message without the key costs one dictionary lookup. With per-tenant cards `RequestCards.ResolveAsync` fills `ActivatedExtensions`, so the validator resolves the card before it reads the activated set. A tenant with no agent resolves to null and the validator returns, leaving the existing refusal to fire.

**D9. Executor read.** `AgentRequestContext.RequestedSkillId` is the value only when the extension is activated, else null, so an agent that did not declare the extension never mistakes private metadata for a selection. An agent that wants the raw value uses `context.Message.GetRequestedSkillId()`. It is a hint, not an authorization decision: without the opt-in it may name anything. The docs say so.

**D10. Per-message, not per-task.** A follow-up carries its own `skillId` or none. No echo to `Task.metadata`: nothing else needs it and it would be new data on the wire. A multi-turn executor can read the first message from `context.Task.History`.

**D11. Client helper is one method on `A2AClient`.** `SelectSkill(message, skillId, options)` sets the metadata, adds the URI to `A2ARequestOptions.Extensions` once, and returns the options to pass, with the message, to a send method that takes a message or a request (the `string text` overload builds its own message). One method covers text, message and request overloads and streaming, and leaves the send path alone. `options` is changed and returned, as `UseBearerToken` does; a null gives new options; a read-only `Extensions` list is replaced by a copy.

**D12. Client check is fail-fast and only when conclusive.** With `ValidateCapabilities` on and a known card, a skill not on it is `A2AException` `InvalidParams` before anything is sent. If the card declares an extended card and the client has not loaded it (`RefreshWithExtendedAgentCardAsync`), the public list is incomplete, so the check is skipped and the server decides. The helper does not require the card to declare the extension: a plain peer ignores the header and the key, and the skill id is still valid intent.

## 4. Non-goals

- A `SendMessageConfiguration.skillId` field: not in the proto.
- Routing to a specific executor per skill. Executors read the id and branch; `AddAgentExecutor` stays one per agent.
- Structured skill arguments or deterministic invocation (#2178).
- Authorization by skill.

## 5. Wire compatibility

No new field, header or error for a request without the key. A plain v1.0 peer ignores the `A2A-Extensions` entry and the metadata key. A plain client that uses `skillId` for something else is unaffected (D5). Cards change only if the host lists the extension. Parity tests and `interop/run.sh` stay as they are.
