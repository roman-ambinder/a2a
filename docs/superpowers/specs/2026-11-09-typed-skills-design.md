# Typed skills

Date: 2026-11-09. Status: design. Builds on S35 (`2026-11-08-s35-skill-selection-design.md`).

## 1. Goal

Agents parse raw parts by hand. With typed skills, an agent declares a skill with .NET input and output types. The SDK puts JSON Schema on the card, refuses bad input with a proper A2A error before the agent runs, routes to the handler, and gives clients a typed call. AOT-safe: every type comes as a `JsonTypeInfo<T>` from a `System.Text.Json` source-generated context. Works alongside a plain `IAgentExecutor`.

## 2. Decisions (all my call; the user waived questions)

**D1. How a request names a skill: S35, nothing new.** `metadata["skillId"]` plus the activated `SkillSelection.ExtensionUri`, read with `AgentRequestContext.RequestedSkillId`. No new key, so per-skill authorization and typed skills agree on one name.

**D2. New package `A2A.Server.TypedSkills` (net8/net10) for the server side.** `JsonSchemaExporter` needs `System.Text.Json` 9+. `net10` has it. `net8` needs a package, and `PackageRulesTests` keeps `A2A.Server` on abstractions only. A package also keeps the core's public API unchanged. `System.Text.Json` is referenced only for `net8.0`, with `VersionOverride="10.0.13"`. No changes to `A2A.Server`: only public seams are used.

**D3. Schema lives in an extension entry on the card.** `AgentSkill` has no schema field and the proto is not ours to change. Entry: URI `https://github.com/roman-ambinder/a2a/extensions/typed-skills/v1`, `required: false`, `params.skills = { "<id>": { "inputSchema": {...}, "outputSchema": {...} } }`. Plain peers ignore it. Constants and a reader, `TypedSkillExtension.TryGetSchemas(card, id, out input, out output)`, sit in `A2A.Protocol` (`A2A.Skills`) so that clients can read schemas too. Generated with `typeInfo.GetJsonSchemaAsNode(new() { TreatNullObliviousAsNonNullable = true })` once, when the set is built. A schema that would be too deep for `A2AJson.MaxJsonDepth` once on the card fails at startup.

**D4. Wire shape of a call.** Input: exactly one `DataPart` in the message (text and file parts are ignored; media type not checked). Output: a task with one artifact, named after the skill id, holding one `DataPart`, then `completed`. Always a task, so `GetTask` and resubscribe work and the result outlives the call. `ApplyTo` sets `InputModes` and `OutputModes` to `application/json` on the skill, which also passes the server's media type check without touching card defaults.

**D5. Validation errors are refusals, before anything is stored.** An `AfterAuthorization` operation filter handles SendMessage and SendStreamingMessage on every binding and throws `InvalidParams`. It runs before `PrepareAsync`, so it must tolerate a null message (pass it on: the core refuses). The router parses again, because filter state does not reach the executor; request limits bound the cost. Validators run twice, so they must be pure and fast.

**D6. What is checked, in order.** (1) one data part; (2) top-level `IsRequired` properties present, via `JsonTypeInfo.Properties`; (3) `Deserialize` with the given type info; (4) non-null result; (5) the optional per-skill `Validate` delegate. Generic schema validation at runtime is left out: the schema comes from the type, so deserialization enforces it. Details go in a `google.rpc.BadRequest` entry in `A2AException.Details` (`fieldViolations: [{ field, description }]`), `Metadata["extension"]` names the typed-skills URI. Never echo values, and never use `JsonException.Message` (it names .NET types): use `JsonException.Path` and a fixed text.

**D7. Routing table.**

| Request | Typed skill matches | Result |
|---|---|---|
| Names a typed skill, no `taskId` | yes | handler |
| Names a typed skill, with `taskId` | yes | `InvalidParams`: typed skills are one-shot |
| Names another skill | no | fallback, else `InvalidParams` |
| Names nothing, no `taskId`, one typed skill, no fallback | n/a | that skill |
| Names nothing, any other case | n/a | fallback, else `InvalidParams` |

Only `RequestedSkillId` counts (S35 D5), so a private `skillId` under a non-activated extension does not route. The refusal text does not repeat the id or list skills.

**D8. Handlers do not see `TaskUpdater`.** `TypedSkillContext<TInput>` has `Input`, `Request` (the `AgentRequestContext`), `Services` (the execution scope) and `ReportProgressAsync(text, ct)`. The router owns start, artifact and completion, so a handler cannot break the sequence. To fail with a message the client may see, throw `TypedSkillFailedException(message)`: the task fails with that text. Any other exception follows the engine's normal path and generic text.

**D9. Cancel.** `CancelAsync` goes to the fallback unless the first history message names a typed skill. No fallback: the default cancel.

**D10. Registration.** `A2AServerBuilder.AddTypedSkills(Action<TypedSkillSet>)` registers the router as the executor, the filter, and a `PostConfigure<A2AServerOptions>` that calls `set.ApplyTo(options.AgentCard)`. It does not matter whether `AddAgentCard` comes first. `set.Map<TInput,TOutput>(id, inputType, outputType, handler, configure?)`, `set.UseFallback<T>()`. `Map` rejects a duplicate id. `ApplyTo` is idempotent: it adds a missing `AgentSkill`, keeps the fields the author set on an existing one, and adds the S35 extension (without `rejectUnknownSkills`) and the typed-skills extension when missing. A card that already has signatures throws at startup, because changing it would break them.

**D11. Client.** `A2AClient.CallSkillAsync<TInput,TOutput>(skillId, input, inputType, outputType, options?, ct)`, in `A2A.Client`, all targets. It uses `SelectSkill` and `SendMessageAsync`, and `WaitForTaskAsync` if the task is not yet terminal. It reads the last artifact with a data part, or a direct message reply with one (for other implementations). A result that does not parse is `A2AException` `InvalidAgentResponse`. A task that ends in anything but `completed` throws `TypedSkillCallException` with `Task` and `State`.

**D12. Experimental, reuse `A2AEXP001`.** Same open epic (#1989, and #2178 for structured arguments), and no new diagnostic id to collide with other sessions.

## 3. Non-goals

- Per-tenant typed skills in `AddTypedSkills`. One set per host. A host with `AgentExecutorResolver` is not covered.
- Multi-turn typed skills and typed streaming: use a plain `IAgentExecutor` (as the fallback).
- Skills that exist only on the extended card.
- Constraints beyond the type in the schema (min, max, pattern). `Validate` enforces them at runtime; they are not advertised.
- Authorization: the per-skill authorization work uses the same skill id (D1).

## 4. Wire compatibility

No new field, header or error for a request that does not reach a typed skill. Cards change only when a host calls `AddTypedSkills`. Plain v1.0 peers ignore both extensions, and with a single typed skill and no fallback they can still call it.
