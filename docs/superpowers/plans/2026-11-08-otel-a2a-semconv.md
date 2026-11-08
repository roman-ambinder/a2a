# OpenTelemetry A2A Semantic Conventions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make client and server operation spans follow the OpenTelemetry A2A semantic conventions (gap D31).

**Architecture:** Names and shared tag helpers stay in `A2ATelemetry` (`A2A.Protocol`). `ClientTelemetry`, `TelemetryFilter`, `ServerTelemetry` and `RequestCards` call them. Metric names do not change.

**Tech Stack:** .NET 8/10, `System.Diagnostics.ActivitySource`, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-08-otel-a2a-semconv-design.md` (D-numbers below refer to it).

## Global Constraints

- `dotnet build A2A.slnx -c Release` gives 0 warnings. Warnings are errors, XML docs included.
- Every public API change goes in `src/A2A.Protocol/PublicAPI.Unshipped.txt`. A removed or changed line from `PublicAPI.Shipped.txt` gets a `*REMOVED*` line in Unshipped (do not edit Shipped). Constant lines include the value, so a changed value is one `*REMOVED*` line plus one new line.
- No `InternalsVisibleTo` between shipped packages. `A2A.Client` and `A2A.Server` do not reference each other.
- No new tag runs unless the span is sampled (`activity is { IsAllDataRequested: true }`). The no-listener paths stay as they are: `Source.HasListeners()` returns early.
- Tags hold ids, states, versions and card fields only. Never message text, parts or metadata.
- Do not rename any `A2ATelemetry.Metrics` value. Do not touch retry, store, skill-selection or required-field code.
- Do not dispatch subagents. Commit on the current branch only. Never push.

## Review Focus

- A sampled-out or propagation-only span (`IsAllDataRequested` false) gets no new tags and does not throw.
- A message with text and metadata: no tag value on any span or span event contains that text.
- A per-tenant host: each span carries its own tenant's agent name, not the host card's.
- A card with an empty description or version: that tag is absent, not an empty string.
- A stream with one distinct state keeps `a2a.task.state`; with two distinct states it has none, and a third state does not bring it back.

---

### Task 1: Rename to the convention

Applies D1-D5, D10, D11. Client, server, public API and existing tests change together so the build stays green.

**Files:**
- Modify: `src/A2A.Protocol/Diagnostics/A2ATelemetry.cs`, `src/A2A.Protocol/PublicAPI.Unshipped.txt`
- Modify: `src/A2A.Client/ClientTelemetry.cs`
- Modify: `src/A2A.Server/Telemetry/ServerTelemetry.cs`, `src/A2A.Server/Telemetry/TelemetryFilter.cs`, `src/A2A.Server/Execution/Execution.cs:69-70`, `src/A2A.Server/Execution/TaskExecutionEngine.cs:542`
- Test: `tests/A2A.Tests/Telemetry/TelemetryTests.cs`, `tests/A2A.Tests/TracingTests.cs`, `tests/A2A.Tests/Operations/OperationTableTests.cs`, `tests/A2A.Grpc.Tests/GrpcTelemetryTests.cs`, and any other test that fails to compile or pins an old name

**Interfaces:**
- Produces: `public static string A2ATelemetry.TaskStateValue(TaskState state)`; `public static string A2ATelemetry.ErrorTypeOf(Exception error)`; constant values `Tags.Operation = "a2a.method.name"`, `Tags.ContextId = "gen_ai.conversation.id"`. `SpanNames.Operation`, `SpanNames.OperationPrefix` and `Tags.RpcSystem` no longer exist.

- [ ] **Step 1: Write the failing tests in `tests/A2A.Tests/Telemetry/TelemetryTests.cs`**

```csharp
[Theory]
[InlineData(TaskState.Completed, "TASK_STATE_COMPLETED")]
[InlineData(TaskState.InputRequired, "TASK_STATE_INPUT_REQUIRED")]
public void Task_state_value_is_the_proto_name(TaskState state, string expected) =>
    Assert.Equal(expected, A2ATelemetry.TaskStateValue(state));

[Fact]
public void Error_type_is_the_a2a_reason()
{
    Assert.Equal("TASK_NOT_FOUND", A2ATelemetry.ErrorTypeOf(A2AException.TaskNotFound("t")));
    Assert.Equal(typeof(InvalidOperationException).FullName, A2ATelemetry.ErrorTypeOf(new InvalidOperationException()));
}
```

Add a test that a `GetTask` for a missing task yields one server span named `GetTask` with `a2a.method.name` = `GetTask`, `error.type` = `TASK_NOT_FOUND`, `a2a.error.code` = `TaskNotFound`, no `rpc.method` and no `rpc.system` tag; and that `a2a.server.requests` carries `a2a.method.name` and `error.type` = `TASK_NOT_FOUND`.

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test tests/A2A.Tests -f net10.0 --filter "FullyQualifiedName~TelemetryTests"`
Expected: build error, `TaskStateValue` and `ErrorTypeOf` not defined.

- [ ] **Step 3: Change `A2ATelemetry`**

- `Tags.Operation = "a2a.method.name"`; `Tags.ContextId = "gen_ai.conversation.id"`; delete `Tags.RpcSystem` and its `SetTag` in `SetOperationTags`; delete `SpanNames.OperationPrefix` and `SpanNames.Operation`. Update XML docs (`SpanNames` summary: operation spans are named after the method).
- `TaskStateValue(TaskState state)`: `ProtoEnumConverter<TaskState>.ToProtoName(state)`.
- `ErrorTypeOf(Exception error)`: for `A2AException e`, `e.ReasonOverride ?? A2AErrors.Reason(e.Code)`; otherwise `FullName ?? Name`. `RecordError` uses it. `a2a.error.code` stays `e.Code.ToString()`.
- `SetResult` and `AddStreamEvent` use `TaskStateValue`.

- [ ] **Step 4: Change the callers**

- `ClientTelemetry.Start`: span name is `operation`.
- `TelemetryFilter`: span name is `operation.Name`. Delete `SpanNames`, `SpanName`, `CreateSpanNames`.
- `ServerTelemetry.ErrorType`: delete it and call `A2ATelemetry.ErrorTypeOf` at its use in `TelemetryFilter.Count`.
- `TaskExecutionEngine.cs:542`: `A2ATelemetry.TaskStateValue(recorded)`.
- `Execution.EndActivity`: compare with `A2ATelemetry.TaskStateValue(TaskState.Failed)`.
- Update `<see cref>` and `<c>a2a.{Operation}</c>` mentions in `ServerTelemetry`, `TelemetryFilter`, `A2AClient`.

- [ ] **Step 5: Update `PublicAPI.Unshipped.txt`**

`*REMOVED*` lines for `SpanNames.OperationPrefix`, `SpanNames.Operation`, `Tags.RpcSystem` and the old values of `Tags.Operation`, `Tags.ContextId`; new lines for the new values, `TaskStateValue` and `ErrorTypeOf`.

- [ ] **Step 6: Update existing tests to the new names**

Replace `a2a.{Operation}` span-name literals with the bare method, `A2ATelemetry.SpanNames.Operation(x)` with `x`, `rpc.method` with `a2a.method.name`, `nameof(TaskState.X)` tag expectations with the proto name, and `error.type` expectations on A2A errors with the reason. Leave `a2a.agent.execute` and `a2a.push.deliver` alone.

- [ ] **Step 7: Run and confirm**

Run: `dotnet build A2A.slnx -c Release && dotnet test tests/A2A.Tests -f net10.0 && dotnet test tests/A2A.Grpc.Tests -f net10.0`
Expected: 0 warnings, all pass.

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "feat(telemetry): name spans and tags per the OTel A2A conventions"
```

### Task 2: Add the missing attributes

Applies D6-D9, D15.

**Files:**
- Modify: `src/A2A.Protocol/Diagnostics/A2ATelemetry.cs`, `src/A2A.Protocol/PublicAPI.Unshipped.txt`
- Modify: `src/A2A.Client/ClientTelemetry.cs`, `src/A2A.Client/A2AClient.cs`
- Modify: `src/A2A.Server/Telemetry/ServerTelemetry.cs`, `src/A2A.Server/Telemetry/TelemetryFilter.cs`, `src/A2A.Server/Agents/RequestCards.cs`
- Test: `tests/A2A.Tests/Telemetry/TelemetryTests.cs`, `tests/A2A.Tests/TracingTests.cs`

**Interfaces:**
- Consumes: Task 1's `TaskStateValue`.
- Produces, on `A2ATelemetry`: `Tags.MessageId = "a2a.message.id"`, `Tags.ReferenceTaskIds = "a2a.message.reference_task_ids"`, `Tags.ProtocolVersion = "a2a.protocol.version"`, `Tags.AgentName = "gen_ai.agent.name"`, `Tags.AgentDescription = "gen_ai.agent.description"`, `Tags.AgentVersion = "gen_ai.agent.version"`; `SetOperationTags(Activity? activity, string operation, string? binding, string? tenant, string? taskId = null, string? contextId = null, string? protocolVersion = null)`; `SetMessage(Activity? activity, Message? message)`; `SetAgent(Activity? activity, AgentCard? card)`; `SetStreamResult(Activity? activity, StreamResponse evt)`.
- Produces, internal: `ServerTelemetry.SetAgent(AgentCard? card)`.

- [ ] **Step 1: Write the failing tests**

In `TelemetryTests.cs`, against a plain sampled `Activity` where a span is not needed:
- `Set_message_tags_ids_and_never_content`: message with `MessageId = "m-1"`, `ContextId = "c-1"`, `TaskId = "t-1"`, `ReferenceTaskIds = ["r-1"]`, text `"SECRET-TEXT"` and a metadata value `"SECRET-META"`. Tags hold `a2a.message.id` = `m-1`, `gen_ai.conversation.id` = `c-1`, `a2a.task.id` = `t-1`, `a2a.message.reference_task_ids` equal to `new[] { "r-1" }`. No tag value contains `SECRET`.
- `Set_agent_skips_empty_card_fields`: card with `Name = "Bot"`, `Description = ""`, `Version = "1.2"`. `gen_ai.agent.name` = `Bot`, `gen_ai.agent.version` = `1.2`, no `gen_ai.agent.description` tag.
- `Stream_keeps_one_state_and_drops_mixed_states`: `SetStreamResult` with `Working` twice leaves `TASK_STATE_WORKING`; then `Completed` removes the tag; then `Completed` again leaves it absent.
- `Set_message_and_agent_skip_unsampled_span`: an `Activity` with `IsAllDataRequested = false` gets no tags, and a null activity does not throw.

In `TelemetryTests.cs` or `TracingTests.cs`, through the handler and client as the existing tests do:
- Server `SendMessage` span has `a2a.message.id`, `a2a.protocol.version` = `1.0`, `gen_ai.agent.name` and `gen_ai.conversation.id`. It has no `gen_ai.operation.name`.
- Server streaming span (`SendStreamingMessage`) has `gen_ai.agent.name`, and has no `a2a.task.state` when the stream went through more than one state; its last `a2a.stream.event` has `a2a.task.state` = `TASK_STATE_COMPLETED`.
- Per-tenant host (`TenantCardProvider`, see `tests/A2A.Tests/AgentRegistryTests.cs`): two tenants' spans carry their own card names.
- Client span with a card has `gen_ai.agent.name`, `.version`, `a2a.protocol.version` and `a2a.message.id`. A client built without a card has no `gen_ai.agent.name`.

- [ ] **Step 2: Run them and confirm they fail**

Run: `dotnet test tests/A2A.Tests -f net10.0 --filter "FullyQualifiedName~TelemetryTests|FullyQualifiedName~TracingTests"`
Expected: build error, new members not defined.

- [ ] **Step 3: Add the tags and helpers to `A2ATelemetry`**

All helpers no-op unless `activity is { IsAllDataRequested: true }`. Empty strings are not tagged.
- `SetMessage`: `Tags.MessageId`, `Tags.ReferenceTaskIds` (`string[]`, only when the list is non-empty), and `SetTask(activity, message.TaskId, message.ContextId)`.
- `SetAgent`: name, description, version.
- `SetOperationTags`: sets `Tags.ProtocolVersion` when given.
- `SetStreamResult`: same id handling as `SetResult`, but the span's state follows D9. Track "mixed" with `activity.SetCustomProperty` under a private key. `SetTag(key, null)` removes a tag. Share the event switch with `SetResult` through one private method so the two cannot drift.

- [ ] **Step 4: Wire the client**

- `ClientTelemetry.TraceAsync`, `TraceStream` and their cores: replace the `string? binding` parameter with `A2AClient client`, and add a trailing `Message? message = null` before or after `ct` as the signature allows. `Start` calls `SetOperationTags` with `client.TelemetryBinding` and `client.TelemetryProtocolVersion`, then `SetAgent(activity, client.AgentCard)` and `SetMessage(activity, message)`. No per-call or per-client allocation is added.
- `A2AClient`: add `internal string? TelemetryBinding => _binding;` and `internal string TelemetryProtocolVersion => _options.ProtocolVersion;`. Change every `ClientTelemetry.TraceAsync`/`TraceStream` call from `_binding` to `this`. `SendMessageAsync` and `SendStreamingMessageAsync` pass `request.Message`.
- Stream core uses `SetStreamResult`.

- [ ] **Step 5: Wire the server**

- `ServerTelemetry.StartOperation`: pass `call.ProtocolVersion` to `SetOperationTags`.
- `TelemetryFilter.Start`: after `StartOperation`, `A2ATelemetry.SetMessage(activity, (request as SendMessageRequest)?.Message)`.
- `TelemetryFilter.Observe`: `SetStreamResult` in place of `SetResult`.
- `ServerTelemetry.SetAgent(AgentCard? card)`: return unless `Source.HasListeners()`; when `Activity.Current` is from `Source`, call `A2ATelemetry.SetAgent`.
- `RequestCards.ResolveAsync`: call `ServerTelemetry.SetAgent` on the cached path and after resolving.

- [ ] **Step 6: Update `PublicAPI.Unshipped.txt`**

New constants and methods; `*REMOVED*` line for the old `SetOperationTags` signature and a line for the new one.

- [ ] **Step 7: Run and confirm**

Run: `dotnet build A2A.slnx -c Release && dotnet test tests/A2A.Tests -f net10.0 && dotnet test tests/A2A.Grpc.Tests -f net10.0`
Expected: 0 warnings, all pass.

- [ ] **Step 8: Commit**

```bash
git add -A src tests
git commit -m "feat(telemetry): add A2A convention attributes to operation spans"
```

### Task 3: Documentation

**Files:**
- Modify: `docs/site/guides/observability.md`, `README.md` (Observability section), `docs/2026-11-07-a2a-gap-analysis.md`, the summary of `A2ATelemetry` in `src/A2A.Protocol/Diagnostics/A2ATelemetry.cs`

- [ ] **Step 1: Update `docs/site/guides/observability.md`**

- Span table: operation spans are named `{method}` (`SendMessage`, `GetTask`, ...). Extension operations use their method name.
- Tag table: `a2a.method.name` replaces `rpc.method`, `rpc.system` goes away, `gen_ai.conversation.id` replaces `a2a.context.id`. Add `a2a.message.id`, `a2a.message.reference_task_ids`, `a2a.protocol.version`, `gen_ai.agent.name`, `gen_ai.agent.description`, `gen_ai.agent.version`. `a2a.task.state` values are proto names (`TASK_STATE_COMPLETED`), and a stream span has it only when the stream saw one state.
- Errors: `error.type` is the A2A reason (`TASK_NOT_FOUND`).
- Metrics table: `a2a.method.name` on `a2a.server.requests`.
- New section `## OpenTelemetry semantic conventions`, in plain short sentences: the SDK follows the A2A page of `open-telemetry/semantic-conventions-genai` (`docs/gen-ai/a2a.md`, commit `da2b5736d7a3`); its status is Development, so names can change before it is stable. The SDK emits no `gen_ai.operation.name` or `invoke_agent` span, because the convention says A2A instrumentation reports A2A requests only, and the agent framework reports the agent run. Differences: the operation span stays a separate span under the ASP.NET Core request span instead of enriching it; `server.address` and `client.address` come from the transport spans. Span names, `a2a.*` counters and SDK-only tags that have no convention stay (`a2a.binding`, `a2a.error.code`, `a2a.agent.execute`, `a2a.push.deliver`).
- "No content": keep it. Say card fields come from the public Agent Card.

- [ ] **Step 2: Update `README.md` Observability**

Same name changes in the span table, **Tags** and **Errors** bullets. One new bullet: conventions followed and their Development status, linking to the guide.

- [ ] **Step 3: Mark the gap**

In `docs/2026-11-07-a2a-gap-analysis.md`, line 160, change item 17 to end with ` Done.` as item 19 does. Change the D31 suggested action to name the A2A conventions page.

- [ ] **Step 4: Sweep and build**

Run: `grep -rnE 'rpc\.method|rpc\.system|a2a\.context\.id|a2a\.(SendMessage|GetTask|\{Operation\})' README.md docs/site src --include='*.md' --include='*.cs' | grep -v '/bin/\|/obj/\|docs/site/_site\|docs/site/api'`
Expected: no output.

Run: `dotnet build A2A.slnx -c Release`
Expected: 0 warnings.

- [ ] **Step 5: Commit**

```bash
git add README.md docs src
git commit -m "docs(telemetry): document the OTel A2A conventions"
```
