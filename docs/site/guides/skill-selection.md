# Skill selection

An agent can offer several skills. With skill selection, a client names the one it wants, and the agent can read the choice. It is an optional extension (A2A epic #1989), so plain v1.0 peers ignore it. Read this if your agent has more than one skill, or if you call such an agent.

The skill-selection API is marked `[Experimental]` (`A2AEXP001`). To use it, add `<NoWarn>A2AEXP001</NoWarn>` to your project. See [Versioning](versioning.md#a2aexp001).

## How it works

The client puts the skill id in `metadata["skillId"]` of the message. It also asks for the extension `SkillSelection.ExtensionUri` in the `A2A-Extensions` header. Nothing is added to the proto, so no field changes. A peer that does not know the extension ignores both.

Selection is per message. A follow-up message carries its own `skillId`, or none.

## Agent side

Declare the extension on the Agent Card, and read the choice in the executor:

[!code-csharp[](../../../samples/Snippets/SkillSelection.cs#Card)]

[!code-csharp[](../../../samples/Snippets/SkillSelection.cs#Executor)]

- `SkillSelection.CreateExtension()` makes the card entry. It is never required.
- `AgentRequestContext.RequestedSkillId` is the chosen id. It is null when the caller did not activate the extension, or sent no usable value. An empty string, a non-string value and text that is not valid UTF-16 all read as null.
- To read the raw value without the activation check, use `Message.GetRequestedSkillId()`.

The extension counts as activated only when the client asked for it and the card declares it. If your card does not declare it, `RequestedSkillId` is always null.

### Refuse unknown skills

By default, an unknown id is passed to your executor as is. Set `rejectUnknownSkills: true` to make the server refuse it before your agent runs. The flag is stored in the extension `params` on the card, so a client can read the policy first. Per-tenant cards can differ.

The server refuses only when both sides agree: the caller activated the extension and the card sets the flag. Otherwise `skillId` might be someone's private metadata, and it is ignored without an error.

With both sides agreed, the call fails with `InvalidParams` on `SendMessage` and `SendStreamingMessage`, on every binding. Nothing is stored. The error metadata names the extension URI. The text does not repeat the id and does not list your skills. The cases are:

- the value is not a string, or is text that is not valid UTF-16
- the id is not a skill of this agent

An empty string reads as "no selection" and is accepted.

### What counts as a known skill

The skills on the public card count. A skill that is only on the extended card counts too, but only for a caller that is authenticated and allowed to call `GetExtendedAgentCard` (your `A2AAuthorizationHandler` decides). Anyone else gets the normal refusal, so an anonymous caller cannot probe the extended card. The extended card provider runs only when the public card has no match.

## Client side

Call `A2AClient.SelectSkill` and pass both the message and the returned options to the send method:

[!code-csharp[](../../../samples/Snippets/SkillSelection.cs#Client)]

`SelectSkill` sets `metadata["skillId"]` on the message and adds the extension URI to the call's extensions. It changes the options you pass and returns them. With null, it makes new options. It works with any send method that takes a message or a request, streaming included. The `string text` overload builds its own message, so it does not fit.

If you skip the returned options, the server never sees the extension and ignores the selection.

When `ValidateCapabilities` is on and the client knows the card, a skill that is not on the card fails at once with `InvalidParams`, and nothing is sent. The check is skipped when the card declares an extended card that the client has not loaded with `RefreshWithExtendedAgentCardAsync`, because the public list is then incomplete. The server decides in that case.

`SelectSkill` does not require the card to declare the extension. A plain peer ignores the header and the key.

## A hint, not authorization

The id comes from the caller. Treat it as untrusted text. Without `rejectUnknownSkills`, it can name anything. Do not use it to decide who may do what. To allow one caller a skill and deny another, use [skill authorization](skill-authorization.md). For other rules, see [authorization](auth-and-ownership.md).

## What can go wrong

- `RequestedSkillId` is always null. Your card does not declare `SkillSelection.CreateExtension()`, or the client did not pass the options from `SelectSkill`.
- A skill is refused though it exists. It is only on the extended card, and the caller is anonymous or not allowed to call `GetExtendedAgentCard`.
- An unknown id reaches your executor. `rejectUnknownSkills` is off. Branch on known ids and handle the rest.
- `SelectSkill` throws `InvalidParams` before sending. The loaded card does not list the skill. Check the id, or load the extended card first.
- `SelectSkill` throws `ArgumentException`. The skill id is null or empty.

## See also

- [Spec decisions](spec-decisions.md)
- [Skill authorization](skill-authorization.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Getting started](getting-started.md)
