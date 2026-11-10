# Typed skills

Agents often parse raw message parts by hand. With typed skills, you declare a skill with .NET input and output types. The SDK puts JSON Schema on the Agent Card, refuses bad input before your agent runs, runs your handler with the checked input, and gives clients a typed call. Read this if your agent has skills with structured input, or if you call such an agent.

The typed-skills API is marked `[Experimental]` (`A2AEXP001`), like skill selection. To use it, add `<NoWarn>A2AEXP001</NoWarn>` to your project. See [Versioning](versioning.md#a2aexp001).

```shell
dotnet add package RomanAmbinder.A2A.Server.TypedSkills   # host typed skills
dotnet add package RomanAmbinder.A2A.Client               # call them
```

## Agent side

First, the types and a source-generated JSON context. Every type comes as a `JsonTypeInfo<T>` from the context, so nothing uses reflection and the code is safe for AOT and trimming:

[!code-csharp[](../../../samples/Snippets/TypedSkills.cs#Types)]

Mark required properties with `required` (or `[JsonRequired]`). The schema lists them, and the server refuses input that misses one.

Then register the skills:

[!code-csharp[](../../../samples/Snippets/TypedSkills.cs#Server)]

- `Map` adds one skill. The id is the `AgentSkill.Id` clients use. A duplicate id throws.
- The handler gets the checked `Input`, the `Request` (caller, tenant, message), the `Services` of the execution, and `ReportProgressAsync`. It does not get a `TaskUpdater`: the SDK starts the task, stores the result and completes it, so a handler cannot break that order.
- `AddTypedSkills` adds the skill to the Agent Card if it is not there. It keeps the text you wrote for an existing skill. It also lists the skill-selection extension and the typed-skills extension on the card. It does not matter whether `AddAgentCard` comes before or after.
- A card that has signatures makes `AddTypedSkills` throw at startup, because changing the card would break them. Add the skills to the card before you sign it.
- `UseFallback<T>` keeps a plain `IAgentExecutor` for every other request. Use it instead of `AddAgentExecutor`, which would replace the typed router.

## The card

The card gets one extension entry, `https://github.com/roman-ambinder/a2a/extensions/typed-skills/v1`. It is never required, so plain peers ignore it. Its params hold a JSON Schema for the input and the output of each skill:

```json
{ "skills": { "book": { "inputSchema": { "type": "object", "required": ["city", "nights"] }, "outputSchema": { } } } }
```

The schemas come from your types, once, when you call `Map`. A schema that is too deep to send on the card fails at startup. Rules that a type cannot say (a minimum, a pattern) are not in the schema. Use a validator for them.

A client reads the schemas with `TypedSkillExtension.TryGetSchemas`:

[!code-csharp[](../../../samples/Snippets/TypedSkills.cs#Schema)]

The schemas are on the public card, so anyone who can read the card sees the field names of your input and output types. Do not put secrets in names or descriptions.

The card comes from a peer, so the schema is data. Do not follow references in it.

## The wire shape

- **Input:** exactly one data part in the message. Text and file parts are ignored. The media type of the part is not checked.
- **Output:** a task with one artifact, named after the skill id, holding one data part. The task then completes. It is always a task, so `GetTask` and resubscribe work, and the result outlives the call.
- The skill's `inputModes` and `outputModes` on the card get `application/json`. This is what lets a data part through the media type check, without changing the defaults of your card.
- The client names the skill with skill selection: `metadata["skillId"]` and the `A2A-Extensions` header. See [Skill selection](skill-selection.md).

## Which request goes where

| The request | Names a typed skill | Result |
| --- | --- | --- |
| Names a typed skill, has no task id | yes | your handler |
| Names a typed skill, has a task id | yes | `InvalidParams`: typed skills run once |
| Names another skill | no | the fallback, or `InvalidParams` without one |
| Names nothing (no `skillId` in the metadata at all), no task id, one typed skill, no fallback | n/a | that skill |
| Names nothing, any other case | n/a | the fallback, or `InvalidParams` without one |

Only a skill id the caller activated counts (see [Skill selection](skill-selection.md)). A `skillId` in the metadata, without the extension, does not route. It does count as a name for the "names nothing" row, because a security check may read the key without the header: a `skillId` that is not the one skill is refused, and one that equals it runs that skill. The refusal text does not repeat the id and does not list your skills.

`CancelTask` goes to the fallback unless the first message of the task names a typed skill. Without a fallback, or for a typed task, the default cancel runs: the task is canceled and the handler's token fires.

## Bad input is refused

A send that goes to a typed skill is checked before anything is stored and before your agent runs. The check is an operation filter, so it runs on every binding, for `SendMessage` and `SendStreamingMessage`. The steps, in order:

1. The message has exactly one data part.
2. Every top-level property marked `required` is present.
3. The data deserializes to your input type.
4. The result is not null.
5. Your `Validate` delegate, if any, returns no errors.

[!code-csharp[](../../../samples/Snippets/TypedSkills.cs#Validate)]

A failure is `InvalidParams`. Nothing is stored and the handler does not run. The error metadata names the extension URI. The error carries a `google.rpc.BadRequest` entry in its details:

```json
{ "@type": "type.googleapis.com/google.rpc.BadRequest", "fieldViolations": [{ "field": "$.nights", "description": "Nights must be between 1 and 30." }] }
```

The `field` is a JSON path into the data, or `message.parts` for a problem with the message itself. The SDK never echoes a value the caller sent, and never uses text of a .NET exception, because that names .NET types. Keep your validator texts the same way.

The check runs again when the handler starts, so a validator runs twice. Keep it pure and fast. At most 20 problems are reported, each clipped to 200 characters.

## Failing from the handler

[!code-csharp[](../../../samples/Snippets/TypedSkills.cs#Fail)]

`TypedSkillFailedException` fails the task with your text as the status message. The caller may see it, so write it for them. Any other exception follows the normal path: the task fails, and the caller gets generic text.

## Client side

[!code-csharp[](../../../samples/Snippets/TypedSkills.cs#Client)]

`CallSkillAsync` uses `SelectSkill` and `SendMessageAsync`, and `WaitForTaskAsync` if the task is not finished. It returns the data part of the last artifact that has one. It also reads a direct message reply with a data part, so it works with agents from other SDKs.

- A task that ends in any state but `completed` throws `TypedSkillCallException`, with the `Task` and its `State`.
- A result that does not parse, or is missing, throws `A2AException` with `InvalidAgentResponse`.
- A refused input throws `A2AException` with `InvalidParams`. Read `Details` for the fields.
- When `ValidateCapabilities` is on and the client knows the card, a skill that is not on the card fails before anything is sent. The same rule as `SelectSkill`.

## Limits

- One set of typed skills per host. A host with an `AgentExecutorResolver` (one agent per tenant) is not covered.
- No multi-turn typed skills and no typed streaming. Use a plain `IAgentExecutor`, for example as the fallback.
- Skills that exist only on the extended card are not covered.
- The schema holds only what the type says. Use `Validate` for ranges and patterns.
- The id is a hint from the caller, not authorization. Use [authorization](auth-and-ownership.md) to decide who may call a skill.

## What can go wrong

- `AddTypedSkills` throws at startup about signatures. The card is signed. Add the skills before signing.
- Every call is refused with "Name a skill". The client did not pass the options from `SelectSkill`, or the card lost the skill-selection extension. `CallSkillAsync` does this for you.
- A plain message never reaches your old executor. You used `AddAgentExecutor` after `AddTypedSkills`, which replaced the router, or you did not call `UseFallback`.
- A required property is not enforced. It is not marked `required` or `[JsonRequired]`.
- A refusal says `message.parts`. The message has no data part, or has more than one.

## See also

- [Skill selection](skill-selection.md)
- [Versioning](versioning.md)
- [Spec decisions](spec-decisions.md)
