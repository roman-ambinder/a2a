# Required-field validation before send: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `A2AClient` refuses a request with a missing required field locally, with an error naming the field, on every binding.

**Architecture:** One public `ValidateOutgoing` overload per request type in a new partial file of `A2AValidator` (`A2A.Protocol`). `A2AClient` calls it once per operation. No new `required` members.

**Tech Stack:** C#, net8.0 + net10.0, xUnit, PublicApiAnalyzers.

**Spec:** `docs/superpowers/specs/2026-11-08-required-field-validation-design.md`

## Global Constraints

- `dotnet build A2A.slnx -c Release` has 0 warnings. Warnings are errors, XML docs required on public members.
- Every new public member goes in `src/A2A.Protocol/PublicAPI.Unshipped.txt`.
- No happy-path allocation in any `ValidateOutgoing` overload.
- Errors are `A2AException` with `A2AErrorCode.InvalidParams`. Blank means `string.IsNullOrWhiteSpace`.
- Presence checks only. Do not call `ValidatePushConfig` from the outgoing path.
- Do not change `ValidateIncoming`, `ValidatePushConfig` or `ValidatePart`, or any server code.
- Do not add C# `required` to any member.
- In `A2AClient`, do not edit the `A2AValidator.ValidateOutgoing(request)` lines in the two send methods. Another session works on that path.

## Review Focus

- Whitespace-only id: treated as missing, same as blank. Test in Task 1.
- Push config nested in SendMessage with no `TaskId`: accepted. Test in Task 1.
- JSON `null` data part, empty text and empty bytes: accepted. Test in Task 1.
- Push URL that is not http(s), and a signing scheme with a malformed secret: not refused locally. Test in Task 1.
- Null request to any operation: `ArgumentNullException`, not `NullReferenceException`. Test in Task 2.

---

### Task 1: Outgoing validator

**Files:**
- Create: `src/A2A.Protocol/Validation/A2AValidator.Outgoing.cs`
- Modify: `src/A2A.Protocol/Validation/A2AValidator.cs`
- Modify: `src/A2A.Protocol/PublicAPI.Unshipped.txt`
- Test: `tests/A2A.Tests/OutgoingValidationTests.cs`

**Interfaces:**
- Produces, all `public static void` in `A2A.Validation.A2AValidator`, each throwing `ArgumentNullException` on a null argument:
  `ValidateOutgoing(SendMessageRequest request)` (moved, extended), and new overloads for `GetTaskRequest`, `CancelTaskRequest`, `SubscribeToTaskRequest`, `TaskPushNotificationConfig config`, `GetTaskPushNotificationConfigRequest`, `DeleteTaskPushNotificationConfigRequest`, `ListTaskPushNotificationConfigsRequest`.

- [ ] **Step 1: Write failing tests** in `OutgoingValidationTests`, using a helper that asserts `A2AErrorCode.InvalidParams` and the exact message:

| Test input | Expected `Message` |
|---|---|
| `GetTaskRequest { Id = "" }`, and `Id = "  "` | `id is required` |
| `CancelTaskRequest { Id = "" }` | `id is required` |
| `SubscribeToTaskRequest { Id = "" }` | `id is required` |
| `GetTaskPushNotificationConfigRequest { TaskId = "", Id = "c" }` | `taskId is required` |
| `GetTaskPushNotificationConfigRequest { TaskId = "t", Id = "" }` | `id is required` |
| `DeleteTaskPushNotificationConfigRequest`, same two cases | same two messages |
| `ListTaskPushNotificationConfigsRequest { TaskId = "" }` | `taskId is required` |
| `TaskPushNotificationConfig { TaskId = "t", Url = "" }` | `config.url is required` |
| `TaskPushNotificationConfig { Url = "https://x" }` | `config.taskId is required` |
| same with `Authentication = new() { Scheme = "" }` and `TaskId = "t"` | `config.authentication.scheme is required` |
| Message with `Role = Role.Unspecified` | `message.role is required` |
| Message with `Parts = [new TextPart(null!)]` | `message.parts[0].text is required` |
| Message with `Parts = [Part.FromText("a"), new UrlPart(null!)]` | `message.parts[1].url is required` |
| Message with `Parts = [new DataPart(default)]` | `message.parts[0].data is required` |
| `Configuration.TaskPushNotificationConfig { Url = "" }` | `configuration.taskPushNotificationConfig.url is required` |
| same with `Url = "https://x", Authentication = new() { Scheme = " " }` | `configuration.taskPushNotificationConfig.authentication.scheme is required` |

Plus these accepted cases, which must not throw:

- `SendMessageRequest` with a nested push config with `Url` set and no `TaskId`.
- Parts `[Part.FromText("")]`, `[Part.FromBytes(ReadOnlyMemory<byte>.Empty, "a/b")]`, `[Part.FromData(JsonDocument.Parse("null").RootElement)]`.
- `TaskPushNotificationConfig { TaskId = "t", Url = "ftp://x" }`.
- `TaskPushNotificationConfig { TaskId = "t", Url = "https://x", Authentication = new() { Scheme = A2A.Push.WebhookSignature.Scheme, Credentials = "not-a-secret" } }`.

Plus `Happy_path_does_not_allocate`: build one valid instance of each of the 8 request types (SendMessage with three parts and a nested push config) before measuring. Run each overload 1000 times as warmup. Then `var before = GC.GetAllocatedBytesForCurrentThread();` run each once, read `after`, `Assert.Equal(before, after)`.

- [ ] **Step 2: Run, confirm failure.** `dotnet test tests/A2A.Tests -f net10.0 --filter OutgoingValidationTests`. Expected: compile errors for missing overloads.

- [ ] **Step 3: Make `A2AValidator` `partial`.** Move `ValidateOutgoing(SendMessageRequest)` to the new file. In `ValidateMessage`, replace the `ValidatePart($"...")` loop with an inline null check that builds the path only when throwing; the message stays `{path}.parts[{i}] is null`. Keep `ValidatePart` public and unchanged.

- [ ] **Step 4: Implement the overloads** in `A2AValidator.Outgoing.cs`, reusing `RequireId`. For SendMessage, after `ValidateMessage(request.Message, "message")`, check role, then part content with an index loop and a `switch` on the part type, then the nested push config. Share one private presence helper for push config taking `(TaskPushNotificationConfig config, string urlPath, string schemePath)`, with `const` strings for the two paths' fields so no path is concatenated on the happy path. Task id check for Create goes in the Create overload only. Field order for errors: `url`, then `taskId`, then `authentication.scheme`. Each overload has an XML summary naming its fields.

- [ ] **Step 5: Update `PublicAPI.Unshipped.txt`** with the 7 new overloads. The moved one is already in Shipped.

- [ ] **Step 6: Run, confirm pass.** Same filter, on `-f net10.0` and `-f net8.0`. Also run `--filter "UnionTests|CallValidatorTests"`. Expected: all pass.

- [ ] **Step 7: Commit.** `feat(protocol): validate required fields on every outgoing request`

---

### Task 2: Wire into `A2AClient`, prove it on every binding

**Files:**
- Modify: `src/A2A.Client/A2AClient.cs`
- Modify: `CHANGELOG.md`, `docs/2026-11-07-a2a-gap-analysis.md`
- Test: `tests/A2A.Tests/ClientRequiredFieldTests.cs`
- Test: `tests/A2A.Grpc.Tests/GrpcTransportTests.cs` (add one test)
- Fix: any existing test or sample that sent a blank required field by accident

**Interfaces:**
- Consumes: Task 1's `A2AValidator.ValidateOutgoing` overloads.

- [ ] **Step 1: Write failing tests.** `ClientRequiredFieldTests`, a `[Theory]` over both HTTP bindings: `new A2AClient(new JsonRpcTransport(http, uri))` and `new A2AClient(new HttpJsonTransport(http, uri))`, over `RecordingHandler` (`tests/A2A.Tests/TestHost.cs`). For each operation, with the blank field, assert `A2AException`, `InvalidParams`, the exact message from the Task 1 table, and `Assert.Empty(handler.Calls)`:
`GetTaskAsync("")`, `CancelTaskAsync("")`, `SubscribeToTaskAsync("")`, `ObserveTaskAsync("")` (all `id is required`), `CreateTaskPushNotificationConfigAsync` with blank `Url`, `GetTaskPushNotificationConfigAsync`, `DeleteTaskPushNotificationConfigAsync`, `ListTaskPushNotificationConfigsAsync` with blank `TaskId`, `SendMessageAsync` with `Role.Unspecified`.
Use a client with no `AgentCard` so capability checks are skipped. Streaming calls throw on invocation, before enumeration: wrap in a lambda.

Add `Null_request_is_ArgumentNullException`, one call per operation with a typed null.
Add `Whitespace_id_is_refused`: `GetTaskAsync("  ")`.
Add `Valid_request_reaches_the_transport`: `GetTaskAsync("t1")`, assert `handler.Calls` has one call; the handler's canned response may fail parsing, so catch whatever follows and assert on `Calls` only.

In `GrpcTransportTests`, add `Missing_required_field_fails_before_the_call`: `new A2AClient(Transport(invoker))` over `ScriptedInvoker`, `GetTaskAsync("")`, assert `InvalidParams`, `id is required`, and that the invoker saw no call.

- [ ] **Step 2: Run, confirm failure.** `dotnet test tests/A2A.Tests -f net10.0 --filter ClientRequiredFieldTests`. Expected: tests fail, requests reach the handler.

- [ ] **Step 3: Add one line to each of the 8 methods**, first in the body, before tenant fill and `RequireCapability`: `A2AValidator.ValidateOutgoing(request);` (`config` for Create) on `GetTaskAsync`, `CancelTaskAsync`, `SubscribeToTaskAsync`, `CreateTaskPushNotificationConfigAsync`, `GetTaskPushNotificationConfigAsync`, `ListTaskPushNotificationConfigsAsync`, `DeleteTaskPushNotificationConfigAsync`. In `ObserveTaskAsync` replace `ArgumentException.ThrowIfNullOrEmpty(taskId)` with `A2AValidator.RequireId(taskId, "id")`. Do not add checks to `ListTasksAsync` or `GetExtendedAgentCardAsync`, and do not touch the internal `Resubscribe` or `GetTaskForResume`.

- [ ] **Step 4: Run new tests, confirm pass**, including `dotnet test tests/A2A.Grpc.Tests -f net10.0 --filter Missing_required_field`.

- [ ] **Step 5: Run the full suites.** `dotnet test tests/A2A.Tests -f net10.0`, `dotnet test tests/A2A.Grpc.Tests -f net10.0`, `dotnet test tests/A2A.Extensions.AI.Tests -f net10.0`, `dotnet test tests/A2A.V0_3Compat.Tests -f net10.0`. If an existing test fails because it relied on a blank required field reaching the server, change the test to a real value only if the test is not about that blank value. If the test's point is that the server rejects a blank, send it with the transport directly (`IA2AClient` on the raw transport), which stays unvalidated. Report each such change.

- [ ] **Step 6: Docs.** `CHANGELOG.md` 0.1.0, one bullet under a fitting section: the client now refuses missing required fields locally on every operation, naming the field. Gap analysis D29 row, last column: append `Done: runtime check before send; no new `required` members, see docs/superpowers/specs/2026-11-08-required-field-validation-design.md.`

- [ ] **Step 7: Commit.** `feat(client): reject missing required fields before sending`
