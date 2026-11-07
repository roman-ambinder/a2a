# P0 Security and Correctness Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the existing A2A .NET SDK so that safe, correct behaviour is the default: owner-scoped tasks, auth on every binding, idempotent sends, resumable streams, trusted card verification, safe webhooks, bounded requests.

**Architecture:** Staged refactor of the existing four projects. Transport-agnostic core in `A2A.Server` (`A2ARequestHandler`, stores, event store), HTTP bindings in `A2A.Server.AspNetCore`, wire model and signing in `A2A.Protocol`, client in `A2A.Client`. Every stage keeps the build green and all tests passing.

**Tech Stack:** .NET SDK 10.0.401, C# latest, net8.0 + net10.0 libraries, System.Text.Json, ASP.NET Core minimal endpoints, xUnit 2.9, `Microsoft.AspNetCore.TestHost`.

**Spec:** `docs/superpowers/specs/2026-11-07-p0-security-correctness-design.md`. Section numbers below (§7.1 etc.) refer to it. Read §10 before starting.

## Global Constraints

- Libraries target `net8.0;net10.0`; tests target `net10.0`. `TreatWarningsAsErrors` stays on for `src`, and is turned on for `tests` in Task 0.
- No third-party runtime dependency in `A2A.Protocol`, `A2A.Client`, `A2A.Server`, `A2A.Server.AspNetCore`. Only `Microsoft.Extensions.*`, ASP.NET Core shared framework, BCL.
- Every addition is wire-compatible with plain A2A v1.0 peers: extra headers and metadata only, never a new required field.
- No security default that must be turned on. Opt-out allowed, opt-in not.
- A task, context or push config owned by another caller is reported exactly like a missing one: `TaskNotFound` (-32001, HTTP 404), or for contexts `InvalidParams` with text `contextId not found`.
- No behaviour differs between JSON-RPC and HTTP+JSON.
- Keep type names `ServerCallContext`, `AgentTask`, `IAgentExecutor`, `TaskUpdater`, `IA2AClient`, `A2ARequestHandler`, `TaskEventBus`. Change their contracts as tasks say.
- Existing public API need not be preserved (unreleased).
- Never delete a test to make a stage pass. Rewrite it only if it asserts behaviour the spec forbids, and say so in the commit message.
- Build and full test run after every task: `dotnet build -warnaserror` from repo root succeeds and `dotnet test tests/A2A.Tests` reports `Failed: 0`.
- Commit messages end with the line `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **Two identical sends racing.** Two `SendMessage` calls with the same `messageId` arrive at the same instant. A person expects exactly one task. Test added in Task 14.
2. **A resume cursor that belongs to another task or is garbage.** `Last-Event-ID: otherTask:5` or `Last-Event-ID: abc`. A person expects a fresh snapshot of the requested task, never events from another task and never an error. Test added in Task 12.
3. **Same idempotency key from two different callers.** Alice and Bob both send `Idempotency-Key: k1`. A person expects two independent tasks, and Bob must never receive Alice's task. Test added in Task 14.
4. **Obfuscated private webhook addresses.** `https://[::ffff:127.0.0.1]/`, `https://0x7f000001/`, `https://2130706433/`, `https://169.254.169.254.nip.io/` style names that resolve private. A person expects all to be refused. Test added in Task 16.
5. **Tenant values that look alike.** Tenant `T1` vs `t1`, and body tenant `""` vs absent. A person expects ordinal, case-sensitive tenants, and `""` treated the same as absent. Test added in Task 1.

---

### Task 0: Baseline under git, warnings fixed

**Files:**
- Create: `.gitignore`
- Modify: `tests/A2A.Tests/TestHost.cs:72`, `tests/A2A.Tests/ProtocolTests.cs:72-84`, `Directory.Build.props`

**Interfaces:**
- Consumes: nothing.
- Produces: git repository with baseline commit; tests compile with warnings as errors.

- [ ] **Step 1: Confirm no other writer.** Run `find src tests samples -name '*.cs' -newermt '-10 minutes' -not -path '*/obj/*'` twice, five minutes apart. Expected: no output both times. If files are changing, stop and tell the user.
- [ ] **Step 2: Create `.gitignore`** with `bin/`, `obj/`, `*.user`, `.vs/`, `.idea/`, `TestResults/`.
- [ ] **Step 3: `git init`, add all, commit** `chore: baseline before P0 refactor`.
- [ ] **Step 4: Turn on warnings as errors for tests.** In `Directory.Build.props` add `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` to a property group that applies when the project path contains `/tests/`.
- [ ] **Step 5: Run `dotnet build -warnaserror`.** Expected: FAIL with `CS0108` in `TestHost.cs` and `xUnit1025` in `ProtocolTests.cs`.
- [ ] **Step 6: Fix.** Rename `TestAuthHandler.Scheme` constant use so it no longer hides `AuthenticationHandler.Scheme` (keep `SchemeName`, remove the hiding member). Remove the duplicate `InlineData` rows in `Numbers_follow_ecmascript_formatting`.
- [ ] **Step 7: Build and test.** Expected: build succeeds, `Passed: 46` (or the same count minus removed duplicate rows; note the count).
- [ ] **Step 8: Commit** `test: compile tests with warnings as errors`.

---

### Task 1: Caller identity contract (§6.2)

**Files:**
- Modify: `src/A2A.Server/ServerCallContext.cs`, `src/A2A.Server.AspNetCore/HttpPlumbing.cs:12-30`, `src/A2A.Server.AspNetCore/A2AHttpOptions.cs`, `src/A2A.Server/A2ARequestHandler.cs` (every `call.Tenant ??= request.Tenant`), `src/A2A.Server/ServiceCollectionExtensions.cs`
- Create: `src/A2A.Server/Identity/ITenantResolver.cs`, `src/A2A.Server/Identity/IPrincipalResolver.cs`, `src/A2A.Server.AspNetCore/Identity/DefaultResolvers.cs`
- Test: `tests/A2A.Tests/IdentityTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces:
  - `ServerCallContext.Tenant : string` (non-null, `""` = default tenant), `ServerCallContext.Principal : string` (non-null). `CallerId` removed.
  - `ServerCallContext.BindTenant(string? bodyTenant) : void` — if `bodyTenant` is null or `""`, no-op; if `Tenant` is `""` and came from no route, set it; if both non-empty and different (ordinal), throw `A2AException.InvalidParams("tenant in request body does not match the endpoint tenant")`.
  - `public const string ServerCallContext.AnonymousPrincipal = "anonymous"`.
  - `public interface ITenantResolver { string Resolve(HttpContext http); }` in `A2A.Server.AspNetCore` (HTTP-specific; lives with bindings). Default: route value `tenant` or `""`.
  - `public interface IPrincipalResolver { string? Resolve(ClaimsPrincipal user); }` in `A2A.Server` (transport-agnostic). Default order: `ClaimTypes.NameIdentifier`, then `sub`, then `null`. Null for an authenticated user with neither claim → `Unauthenticated`.
  - `HttpPlumbing.CreateCallContext(HttpContext, A2AHttpOptions)` uses both resolvers from DI and sets `TenantFromRoute = true` whenever the route has a non-empty `tenant` value (REST already maps `/{tenant}/...` prefixes in `RestEndpoint.cs:19`).

- [ ] **Step 1: Write failing tests** in `IdentityTests.cs` using `TestAgentHost`:

```csharp
[Fact] public async Task Anonymous_callers_get_anonymous_principal()
{   // Task 6 will make anonymous opt-in; until then the host allows it.
    await using var host = await TestAgentHost.StartAsync();
    var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("task")).Task!;
    var rec = await host.App.Services.GetRequiredService<ITaskStore>().GetAsync(task.Id);
    Assert.Equal(ServerCallContext.AnonymousPrincipal, rec!.OwnerId);
}

[Fact] public void Body_tenant_conflicting_with_route_tenant_is_rejected()
{
    var call = new ServerCallContext { Tenant = "t1", TenantFromRoute = true, Principal = "p" };
    var ex = Assert.Throws<A2AException>(() => call.BindTenant("t2"));
    Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
}

[Theory] [InlineData(null)] [InlineData("")]
public void Empty_body_tenant_is_same_as_absent(string? body)
{
    var call = new ServerCallContext { Tenant = "", Principal = "p" };
    call.BindTenant(body);
    Assert.Equal("", call.Tenant);
}

[Fact] public async Task Tenants_are_case_sensitive()   // Review Focus 5
{
    await using var host = await TestAgentHost.StartAsync();
    var http = host.Http("alice");
    var t = await CreateTaskViaRestAsync(http, "/a2a/T1");
    var resp = await http.GetAsync($"/a2a/t1/tasks/{t}");
    Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
}
```

  `CreateTaskViaRestAsync` is a local helper posting `{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"task"}]}}` to `{prefix}/message:send` with header `A2A-Version: 1.0` and returning `task.id`. Note: `TaskRecord.OwnerId` is renamed to `Owner` in Task 3; update this test there.
- [ ] **Step 2: Run** `dotnet test tests/A2A.Tests --filter FullyQualifiedName~IdentityTests`. Expected: compile FAIL (`BindTenant`, `Principal`, `TenantFromRoute` missing).
- [ ] **Step 3: Implement.** Add `TenantFromRoute : bool` (init) to `ServerCallContext`. Replace each `call.Tenant ??= request.X` in `A2ARequestHandler` with `call.BindTenant(request.X)`. Remove `A2AHttpOptions.CallerIdResolver`; register defaults with `TryAddSingleton`. Unauthenticated user → `Principal = AnonymousPrincipal`. Update `OwnerTenantAccessPolicy` and `TaskRecord` writes to use `Principal` (the type is deleted in Task 4).
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`. Fix existing tests that referenced `CallerId` (`TestHost` extended card description uses `call.Principal`).
- [ ] **Step 5: Commit** `feat(server): non-null caller tenant and principal`.

---

### Task 2: Error table additions (§6.3)

**Files:**
- Modify: `src/A2A.Protocol/Errors/A2AException.cs`, `src/A2A.Server.AspNetCore/HttpPlumbing.cs:38-52`, `src/A2A.Server/A2AServerOptions.cs`, `src/A2A.Server.AspNetCore/JsonRpcEndpoint.cs`, `src/A2A.Server.AspNetCore/RestEndpoint.cs` (wherever `Normalize` output is written)
- Test: `tests/A2A.Tests/ErrorTests.cs`

**Interfaces:**
- Produces:
  - `A2AErrorCode.RateLimited = -32000`.
  - `A2AException.ReasonOverride : string?` (init). `A2AErrors.BuildDetails` uses `ex.ReasonOverride ?? Reason(ex.Code)`.
  - `A2AException.Retryable : bool` (init; default from `A2AErrors.DefaultRetryable(code)`: true for `Internal`, `RateLimited`; false otherwise). `BuildDetails` always writes `metadata.retryable` as `"true"`/`"false"`.
  - `A2AException.RetryAfter : TimeSpan?` (init). Written as `metadata.retryAfter` in whole seconds; REST and JSON-RPC HTTP responses also set `Retry-After` header.
  - Factories: `A2AException.PayloadTooLarge(long limit)` → code `InvalidRequest`, reason `PAYLOAD_TOO_LARGE`, `HttpStatus = 413`. `A2AException.IdempotencyKeyMismatch()` → `InvalidRequest`, reason `IDEMPOTENCY_KEY_MISMATCH`, `HttpStatus = 422`. `A2AException.RateLimited(TimeSpan retryAfter)` → `RateLimited`, reason `RATE_LIMITED`, `HttpStatus = 429`.
  - Client side: `A2AException.IsRetryable` reads parsed `metadata.retryable` when present.
  - `A2AServerOptions.IncludeExceptionDetails : bool?` — null means "true when `IHostEnvironment.IsDevelopment()`". `HttpPlumbing.Normalize(Exception, ILogger, bool includeDetails)`: unknown exceptions → `new A2AException(Internal)` with default text, plus `ex.GetType().Name: ex.Message` appended only when `includeDetails`.
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public void Payload_too_large_maps_to_413_with_reason()
{
    var ex = A2AException.PayloadTooLarge(10);
    Assert.Equal(413, ex.HttpStatus);
    var info = A2AErrors.BuildDetails(ex)[0];
    Assert.Equal("PAYLOAD_TOO_LARGE", info.GetProperty("reason").GetString());
    Assert.Equal("false", info.GetProperty("metadata").GetProperty("retryable").GetString());
}
[Fact] public void Rate_limited_is_retryable_with_retry_after()
{
    var info = A2AErrors.BuildDetails(A2AException.RateLimited(TimeSpan.FromSeconds(7)))[0].GetProperty("metadata");
    Assert.Equal("true", info.GetProperty("retryable").GetString());
    Assert.Equal("7", info.GetProperty("retryAfter").GetString());
}
[Theory][MemberData(nameof(BindingTests.Bindings), MemberType = typeof(BindingTests))]
public async Task Unexpected_exception_text_hidden_outside_development(string binding)
{   // ScenarioAgent "fail" throws InvalidOperationException("boom") inside the executor -> FAILED task, already hidden.
    // Use a store that throws to hit the endpoint path:
    await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<ITaskStore, ThrowingTaskStore>());
    var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).GetTaskAsync("x"));
    Assert.Equal(A2AErrorCode.Internal, ex.Code);
    Assert.DoesNotContain("store exploded", ex.Message);
}
```

  Add an optional `Action<IServiceCollection>? services` parameter to `TestAgentHost.StartAsync`, applied after `AddA2AServer`. `ThrowingTaskStore` throws `InvalidOperationException("store exploded")` from every method. The test host environment is `Production` by default under `WebApplication.CreateBuilder()`; assert that by checking `host.App.Environment.IsDevelopment()` is false.
- [ ] **Step 2: Run** filter `ErrorTests`. Expected: compile FAIL.
- [ ] **Step 3: Implement** the members above. JSON-RPC mapping: `RateLimited` → JSON-RPC code `-32000`. HTTP status precedence: `ex.HttpStatus ?? A2AErrors.HttpStatus(code)`; `RateLimited` default 429.
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(protocol): retryable errors, size/rate/idempotency reasons, hidden exception text`.

---

### Task 3: Owner-scoped, versioned task store (§7.1 contract)

**Files:**
- Create: `src/A2A.Server/Storage/TaskStore.cs`, `src/A2A.Server/Storage/InMemoryTaskStore.cs`, `src/A2A.Server/Storage/StoreExceptions.cs`
- Modify: `src/A2A.Server/Stores.cs` (remove `ITaskStore`, old `TaskRecord`, old `InMemoryTaskStore`; keep `PageCursor`, push store until Task 5), `src/A2A.Server/A2ARequestHandler.cs` (compile against new contract with minimal changes; behaviour changes come in Task 4)
- Test: `tests/A2A.Tests/TaskStoreTests.cs`

**Interfaces:**
- Produces (namespace `A2A.Server.Storage`):

```csharp
public readonly record struct TaskKey(string Tenant, string Owner, string TaskId);
public sealed record TaskRecord(string Tenant, string Owner, AgentTask Task, long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{ public TaskKey Key => new(Tenant, Owner, Task.Id); }
public sealed record ContextRecord(string Tenant, string Owner, string ContextId, DateTimeOffset CreatedAt);
public sealed record TaskQuery(string Tenant, string Owner, string? ContextId, TaskState? Status, DateTimeOffset? StatusTimestampAfter, int PageSize, string? PageToken);
public sealed record TaskPage(IReadOnlyList<TaskRecord> Records, string NextPageToken, int TotalSize);
public sealed class ConcurrencyException(TaskKey key, long expected) : Exception;
public sealed class DuplicateTaskException(TaskKey key) : Exception;

public abstract class TaskStore
{
    public abstract ValueTask<TaskRecord?> GetAsync(TaskKey key, CancellationToken ct = default);
    public abstract ValueTask<TaskRecord> CreateAsync(TaskRecord record, CancellationToken ct = default);           // Version must be 0 on input; stored as 1
    public abstract ValueTask<TaskRecord> UpdateAsync(TaskRecord record, long expectedVersion, CancellationToken ct = default); // returns stored record with Version = expected + 1
    public abstract ValueTask<TaskPage> ListAsync(TaskQuery query, CancellationToken ct = default);
    public abstract ValueTask<ContextRecord?> GetContextAsync(string tenant, string contextId, CancellationToken ct = default);
    public abstract ValueTask<ContextRecord> GetOrCreateContextAsync(ContextRecord record, CancellationToken ct = default); // atomic; returns existing if present
}
public static class TaskIds { public static string New(); }  // 16 random bytes (RandomNumberGenerator), Base64Url, 22 chars
```

  `InMemoryTaskStore : TaskStore`: dictionary keyed by `(Tenant, TaskId)` storing the record; `GetAsync` returns null when `Owner` differs. Deep-copies via `A2AJson.Clone` on every read and write. `UpdateAsync` uses `ConcurrentDictionary.TryUpdate` compare on the stored instance after version check. Reuse existing `QueryAsync` filtering, ordering and `PageCursor`.
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] public async Task Get_with_wrong_owner_returns_null()
{
    var s = new InMemoryTaskStore();
    var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
    Assert.Null(await s.GetAsync(new TaskKey("t", "bob", "id1")));
    Assert.Equal(1, (await s.GetAsync(r.Key))!.Version);
}
[Fact] public async Task Update_with_stale_version_throws()
{
    var s = new InMemoryTaskStore();
    var r = await s.CreateAsync(NewRecord("t", "alice", "id1"));
    var r2 = await s.UpdateAsync(r, 1);
    Assert.Equal(2, r2.Version);
    await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(r, 1).AsTask());
}
[Fact] public async Task Create_twice_throws_duplicate()
{ var s = new InMemoryTaskStore(); await s.CreateAsync(NewRecord("t","a","id1"));
  await Assert.ThrowsAsync<DuplicateTaskException>(() => s.CreateAsync(NewRecord("t","b","id1")).AsTask()); }
[Fact] public async Task Mutating_returned_record_does_not_change_store()
{ var s = new InMemoryTaskStore(); var r = await s.CreateAsync(NewRecord("t","a","id1"));
  r.Task.Status.State = TaskState.Failed;
  Assert.Equal(TaskState.Submitted, (await s.GetAsync(r.Key))!.Task.Status.State); }
[Fact] public async Task GetOrCreateContext_is_first_writer_wins()
{ var s = new InMemoryTaskStore();
  var a = s.GetOrCreateContextAsync(new("t","alice","c1",DateTimeOffset.UtcNow)).AsTask();
  var b = s.GetOrCreateContextAsync(new("t","bob","c1",DateTimeOffset.UtcNow)).AsTask();
  var res = await Task.WhenAll(a, b);
  Assert.Equal(res[0].Owner, res[1].Owner); }
[Fact] public void Task_ids_are_22_char_base64url_and_unique()
{ var ids = Enumerable.Range(0, 1000).Select(_ => TaskIds.New()).ToHashSet();
  Assert.Equal(1000, ids.Count); Assert.All(ids, i => Assert.Matches("^[A-Za-z0-9_-]{22}$", i)); }
```

  Test helpers in this file: `NewRecord(string tenant, string owner, string id)` returns a `TaskRecord` with state `Submitted`, `Version = 0`, timestamps `UtcNow`; `WithState(TaskRecord r, TaskState s)` returns `r with { Task = clone with Status.State = s }` (used again in Task 4).

  Plus: move the existing list-paging assertions (ownership filter, keyset paging, bad page token → `InvalidParams`) into `ListAsync` tests.
- [ ] **Step 2: Run** filter `TaskStoreTests`. Expected: compile FAIL.
- [ ] **Step 3: Implement** the contract and in-memory store. In `A2ARequestHandler`, adapt calls mechanically: build `TaskKey(call.Tenant, call.Principal, id)` for gets; use `CreateAsync` where the old code created a record and `UpdateAsync(record, record.Version)` where it saved. Replace `GetAccessibleAsync` with `_tasks.GetAsync(Key(call, id))`. Replace the unscoped re-reads in `CancelTaskAsync` and `SubscribeToTaskAsync` with keyed reads. Keep `ITaskAccessPolicy` registered but unused until Task 4. Move test doubles (`ThrowingTaskStore` from Task 2) and the Task 1 identity test to the new contract (`rec.Owner`, `GetAsync(new TaskKey("", ServerCallContext.AnonymousPrincipal, task.Id))`).
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`. `Tasks_are_scoped_to_their_owner` must still pass.
- [ ] **Step 5: Commit** `refactor(server): owner-keyed versioned TaskStore`.

---

### Task 4: State machine, context ownership, follow-up policy (§7.1 rules)

**Files:**
- Create: `src/A2A.Server/TaskStateMachine.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs` (`PrepareAsync`, `ConsumeAsync`, `Apply`), `src/A2A.Server/A2AServerOptions.cs`, `src/A2A.Server/Stores.cs` (delete `ITaskAccessPolicy`, `OwnerTenantAccessPolicy`), `src/A2A.Server/ServiceCollectionExtensions.cs`
- Test: `tests/A2A.Tests/TaskLifecycleTests.cs`

**Interfaces:**
- Consumes: `TaskStore`, `TaskKey`, `ConcurrencyException`, `TaskIds.New()` (Task 3); `ServerCallContext.Principal/Tenant` (Task 1).
- Produces:
  - `public enum FollowUpPolicy { InterruptedOnly, AllowSteering }`; `A2AServerOptions.FollowUpPolicy` default `InterruptedOnly`.
  - `public enum ClientContextIdPolicy { Reject, AdoptIfUnknown }`; `A2AServerOptions.ClientContextIdPolicy` default `Reject`.
  - `public static class TaskStateMachine { public static bool CanTransition(TaskState from, TaskState to); public static bool AcceptsFollowUp(TaskState state, FollowUpPolicy policy); }`. Terminal = `Completed, Failed, Canceled, Rejected`. Terminal → anything: false. `AcceptsFollowUp`: `InterruptedOnly` → `InputRequired`, `AuthRequired`; `AllowSteering` adds `Submitted`, `Working`.
  - Event application loop in `ConsumeAsync`: read → validate transition → `UpdateAsync(record, record.Version)`; on `ConcurrencyException` reload and retry up to 3 times; if reloaded task is terminal, drop the event with a debug log.
- [ ] **Step 1: Write failing tests** (all `[Theory]` over both bindings unless marked):

```csharp
Unknown_client_context_id_is_rejected_by_default        // send with contextId "nope" -> InvalidParams, message "contextId not found"
Unknown_client_context_id_adopted_when_policy_allows    // ClientContextIdPolicy.AdoptIfUnknown -> task.ContextId == "c-client"
Foreign_context_is_reported_as_not_found                 // alice creates task (gets contextId C); bob sends with contextId C -> InvalidParams "contextId not found"
Follow_up_to_working_task_rejected_under_strict_policy   // "slow" with returnImmediately; second message with taskId -> UnsupportedOperation
Follow_up_to_working_task_accepted_with_steering         // FollowUpPolicy.AllowSteering -> no error
Follow_up_to_auth_required_task_accepted                 // "auth" scenario; follow-up message with taskId is accepted (no exception)
Terminal_task_rejects_follow_up_with_reference_hint      // existing test; assert message contains "referenceTaskIds"
[Fact] public async Task Terminal_state_first_writer_wins()
{
    Assert.False(TaskStateMachine.CanTransition(TaskState.Completed, TaskState.Canceled));
    var s = new InMemoryTaskStore();
    var v1 = await s.CreateAsync(NewRecord("t", "a", "id1"));                 // Submitted, Version 1
    var v2 = await s.UpdateAsync(WithState(v1, TaskState.Working), 1);         // Version 2
    var a = await s.UpdateAsync(WithState(v2, TaskState.Completed), 2);        // writer A wins, Version 3
    await Assert.ThrowsAsync<ConcurrencyException>(() => s.UpdateAsync(WithState(v2, TaskState.Canceled), 2).AsTask());
    Assert.Equal(TaskState.Completed, (await s.GetAsync(a.Key))!.Task.Status.State);
}
```
- [ ] **Step 2: Run** filter `TaskLifecycleTests`. Expected: FAIL (policies missing; foreign context currently accepted).
- [ ] **Step 3: Implement.** In `PrepareAsync`: no `contextId` → `TaskIds.New()` and `GetOrCreateContextAsync`; with `contextId` → `GetContextAsync`; null → per policy; owner differs → `A2AException.InvalidParams("contextId not found")`. Replace the terminal check with `TaskStateMachine.AcceptsFollowUp`; terminal message text: `"The task is in a terminal state. Start a new task and list this one in referenceTaskIds."`. Non-accepting non-terminal state → `UnsupportedOperation` with text `"The task does not accept messages in state {state}."`. Task ids from `TaskIds.New()`. Delete `ITaskAccessPolicy` and `OwnerTenantAccessPolicy` and their registration.
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): context ownership, follow-up policy, terminal guard`.

---

### Task 5: Push config store re-keyed and secrets redacted (§7.6 ownership, secrets)

**Files:**
- Create: `src/A2A.Server/Storage/PushConfigStore.cs`, `src/A2A.Server/Push/IPushSecretProtector.cs`, `src/A2A.Server.AspNetCore/Push/DataProtectionPushSecretProtector.cs`
- Modify: `src/A2A.Server/Stores.cs` (remove old push store; file may end up holding only `PageCursor` — move it to `Storage/PageCursor.cs` and delete `Stores.cs`), `src/A2A.Server/A2ARequestHandler.cs` (push config methods), `src/A2A.Server/Push/PushNotifications.cs` (sender reads via new store), `src/A2A.Server.AspNetCore/A2AEndpointRouteBuilderExtensions.cs` (DI registration helper)
- Test: `tests/A2A.Tests/PushConfigTests.cs`

**Interfaces:**
- Consumes: `TaskKey` (Task 3).
- Produces:

```csharp
public sealed record PushConfigRecord(TaskKey TaskKey, TaskPushNotificationConfig Config);   // Config holds protected secrets
public abstract class PushConfigStore
{
    public abstract ValueTask SaveAsync(PushConfigRecord record, CancellationToken ct = default);
    public abstract ValueTask<PushConfigRecord?> GetAsync(TaskKey task, string configId, CancellationToken ct = default);
    public abstract ValueTask<IReadOnlyList<PushConfigRecord>> ListAsync(TaskKey task, CancellationToken ct = default);   // ordered by Config.Id ordinal
    public abstract ValueTask<int> CountAsync(TaskKey task, CancellationToken ct = default);
    public abstract ValueTask<bool> DeleteAsync(TaskKey task, string configId, CancellationToken ct = default);
}
public interface IPushSecretProtector { string Protect(string plaintext); string Unprotect(string protectedValue); }
```

  `InMemoryPushConfigStore : PushConfigStore`. Default protector in `A2A.Server` is `PassThroughPushSecretProtector` only for non-ASP.NET hosts; `A2A.Server.AspNetCore` registers `DataProtectionPushSecretProtector` (purpose string `"A2A.PushSecrets.v1"`) via `TryAddSingleton` replacing it. Handler redaction helper `Redact(TaskPushNotificationConfig) : TaskPushNotificationConfig` sets `Token = ""` and `Authentication.Credentials = ""` when non-empty.
- [ ] **Step 1: Write failing tests** (both bindings):

```csharp
Get_and_list_redact_token_and_credentials   // create with Token "s3cret", Authentication {Scheme "Bearer", Credentials "abc"}; Get and List return Token "" and Credentials ""; Create response also redacted
Stored_secret_is_not_plaintext              // read InMemoryPushConfigStore directly: Config.Token != "s3cret"
Foreign_owner_cannot_read_push_config       // alice creates; bob Get -> TaskNotFound
Push_configs_survive_terminal_state         // complete the task; List still returns the config
Create_beyond_MaxPushConfigsPerTask_fails   // limit 10 (hard-coded here; becomes Limits.MaxPushConfigsPerTask in Task 7) -> InvalidRequest
```
- [ ] **Step 2: Run** filter `PushConfigTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** Sender unprotects at delivery time only. Existing test `Push_notifications_delivered_in_order_with_token_auth_and_sequence` must still see the plaintext token in the webhook header.
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`. If `Push_config_crud` asserted round-tripped secrets, rewrite it to expect redaction and note it in the commit.
- [ ] **Step 5: Commit** `feat(server): owner-keyed push configs with protected, redacted secrets`.

---

### Task 6: Authorization on every binding (§7.2)

**Files:**
- Create: `src/A2A.Server/Authorization/A2AAuthorizationHandler.cs`, `src/A2A.Server/Authorization/A2AOperation.cs`, `src/A2A.Server.AspNetCore/A2AStartupValidator.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs` (call hook at top of each public operation), `src/A2A.Server/A2AServerOptions.cs` (remove `RequireAuthenticatedCaller`, add `AllowAnonymous`), `src/A2A.Server.AspNetCore/A2AEndpointRouteBuilderExtensions.cs`, `src/A2A.Server.AspNetCore/JsonRpcEndpoint.cs`, `tests/A2A.Tests/TestHost.cs`
- Test: `tests/A2A.Tests/AuthorizationTests.cs`

**Interfaces:**
- Consumes: `ServerCallContext` (Task 1).
- Produces:
  - `public enum A2AOperation { SendMessage, SendStreamingMessage, GetTask, ListTasks, CancelTask, SubscribeToTask, CreateTaskPushNotificationConfig, GetTaskPushNotificationConfig, ListTaskPushNotificationConfigs, DeleteTaskPushNotificationConfig, GetExtendedAgentCard }`.
  - `public abstract class A2AAuthorizationHandler { public virtual ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperation op, object request, CancellationToken ct) => ValueTask.FromResult(true); }` plus `internal sealed class AllowAllAuthorizationHandler : A2AAuthorizationHandler`.
  - Denied → task-scoped ops throw `TaskNotFound(id)`; `SendMessage`/`SendStreamingMessage` without taskId, `ListTasks`, `GetExtendedAgentCard` throw `A2AException(PermissionDenied)` (HTTP 403).
  - `A2AServerOptions.AllowAnonymous : bool` default false.
  - `MapA2A(pattern)` maps both bindings and returns a `RouteGroupBuilder`; every endpoint it creates is inside that group. New overload of pattern support: `MapA2A("/{tenant}/a2a")` sets `TenantFromRoute`.
  - Builder API (§9): `AddA2AServer(Action<A2AServerOptions>? configure = null) : A2AServerBuilder` with `AddAgentExecutor<T>() where T : class, IAgentExecutor` (scoped), `AddAgentExecutor(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task>)`, `AddAgentCard(AgentCard)`, `AddAuthorizationHandler<T>() where T : A2AAuthorizationHandler`, `AddInMemoryStores()` (registered by default; explicit call is a no-op). `A2AServerBuilder.Services : IServiceCollection`. The old `AddA2AServer<TExecutor>(configure)` overload is removed; `TestAgentHost` moves to the builder.
  - `MapA2A` default pattern becomes `"/"` (D32).
  - `A2AStartupValidator : IHostedService` (registered by `MapA2A`'s DI side via `AddA2AServer`): on start, inspects `EndpointDataSource` for endpoints with display names starting `"A2A "` (excluding `"A2A Agent Card"`, `"A2A Agent Card JWKS"`); if any lacks `IAuthorizeData` metadata and `AllowAnonymous` is false, throws `InvalidOperationException("A2A endpoints have no authorization policy. Call RequireAuthorization() on MapA2A(...) or set A2AServerOptions.AllowAnonymous = true.")`. If `AllowAnonymous` is true, logs once at warning: `"A2A endpoints allow anonymous callers. All anonymous callers share one owner and can read each other's tasks."`.
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Startup_fails_without_authorization_or_explicit_anonymous   // StartAsync(mapAuth: false, allowAnonymous: false) throws InvalidOperationException containing "RequireAuthorization"
[Fact] Explicit_anonymous_starts_and_warns                          // allowAnonymous: true; captured logger has the warning text exactly once
[Theory] RequireAuthorization_covers_every_binding_endpoint        // with RequireAuthorization(): unauthenticated POST /a2a (JSON-RPC GetTask), GET /a2a/tasks/x, POST /a2a/message:stream, GET /a2a/extendedAgentCard all return 401
[Theory] Handler_denial_on_task_op_looks_like_not_found             // custom handler denies GetTask; alice's own task -> TaskNotFound
[Theory] Handler_denial_on_list_is_permission_denied                // denies ListTasks -> HTTP 403 / JSON-RPC error carrying reason PERMISSION_DENIED
[Fact] Agent_card_endpoint_stays_anonymous                          // GET /.well-known/agent-card.json without user -> 200
[Fact] Builder_registers_executor_card_and_handler                 // AddA2AServer().AddAgentExecutor<ScenarioAgent>().AddAgentCard(card).AddAuthorizationHandler<DenyAll>() resolves all three from DI
```

  Change `TestAgentHost.StartAsync` signature to `StartAsync(Action<A2AServerOptions>? configure = null, Action<A2AHttpOptions>? configureHttp = null, Action<IServiceCollection>? services = null, bool requireAuth = true)`. `requireAuth: true` calls `.RequireAuthorization()` on `MapA2A`; all existing tests then send `X-Test-User`. Update `TestAgentHost.Client(binding, user)` default `user = "alice"`. Tests needing anonymous pass `requireAuth: false` and `o.AllowAnonymous = true`.
- [ ] **Step 2: Run** filter `AuthorizationTests`. Expected: FAIL.
- [ ] **Step 3: Implement.** For JSON-RPC, a PermissionDenied/Unauthenticated `A2AException` maps to the HTTP status (401/403) before writing a JSON-RPC body, matching existing `Unauthenticated` handling.
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`. Existing tests that relied on anonymous access get `user: "alice"`, except `Anonymous_callers_get_anonymous_principal` (Task 1), which starts the host with `requireAuth: false` and `o.AllowAnonymous = true`.
- [ ] **Step 5: Update `samples/EchoAgent/Program.cs`** to use API-key auth (header `X-Api-Key`, key from configuration `EchoAgent:ApiKey`) with `RequireAuthorization()`; update `samples/EchoClient/Program.cs` to send the key via `AddA2AClient(...).AddHttpMessageHandler(...)`. Build: `dotnet build samples/EchoAgent samples/EchoClient -warnaserror` succeeds.
- [ ] **Step 6: Commit** `feat(server): authorization hook, fail-closed startup check`.

---

### Task 7: Limits and rate limiting (§7.7)

**Files:**
- Create: `src/A2A.Server/A2ALimits.cs`, `src/A2A.Server/Validation/RequestLimitValidator.cs`, `src/A2A.Server.AspNetCore/RateLimiting/A2ARateLimiting.cs`
- Modify: `src/A2A.Server/A2AServerOptions.cs` (add `Limits`, remove `MaxPageSize`), `src/A2A.Server.AspNetCore/A2AHttpOptions.cs` (remove `MaxRequestBodySize`), `src/A2A.Server.AspNetCore/HttpPlumbing.cs:157-170` (`ReadBodyAsync` throws `PayloadTooLarge`), `src/A2A.Server/A2ARequestHandler.cs`, `src/A2A.Protocol/Json/A2AJson.cs`
- Test: `tests/A2A.Tests/LimitsTests.cs`

**Interfaces:**
- Produces: `public sealed class A2ALimits` with these properties and defaults (exact):

| Property | Type | Default |
|---|---|---|
| `MaxRequestBodyBytes` | long | 10 * 1024 * 1024 |
| `MaxPartsPerMessage` | int | 100 |
| `MaxRawPartBytes` | int | 8 * 1024 * 1024 |
| `MaxJsonDepth` | int | 32 |
| `MaxHistoryLengthReturned` | int | 100 |
| `MaxListPageSize` | int | 100 |
| `MaxConcurrentStreamsPerTask` | int | 10 |
| `MaxConcurrentStreamsPerPrincipal` | int | 50 |
| `MaxEventBytes` | int | 4 * 1024 * 1024 |
| `MaxStreamDuration` | TimeSpan | 1 h |
| `StreamReauthInterval` | TimeSpan | 5 min |
| `MaxPushConfigsPerTask` | int | 10 |

  `A2AServerOptions.Limits : A2ALimits` (get-only, non-null). `RequestLimitValidator.Validate(Message, A2ALimits)` throws `PayloadTooLarge` for raw bytes and `InvalidRequest` for part count. `Shape` caps returned history at `MaxHistoryLengthReturned` even when `historyLength` is null.
  JSON: `A2AJson.Options.MaxDepth = 32`; on net10 set `AllowDuplicateProperties = false`; on net8 a pre-parse duplicate-name check in `HttpPlumbing` using `Utf8JsonReader` (only on net8 via `#if !NET10_0_OR_GREATER`; note tests run on net10 only, so the net8 path is covered by a unit test of the checker method itself, made `internal` and callable from tests).
  Rate limiting: `services.AddA2ARateLimiting(Action<A2ARateLimitOptions>? configure)` registers ASP.NET Core rate limiter policy named `"a2a"`, token bucket partitioned by `"{tenant}|{principal}"`, `TokenLimit = 20`, `TokensPerPeriod = 60`, `ReplenishmentPeriod = 1 min`, `QueueLimit = 0`. `MapA2A` applies `.RequireRateLimiting("a2a")` when the policy is registered. `AddA2AServer` calls `AddA2ARateLimiting()` by default (opt-out: `A2AServerOptions.EnableRateLimiting = false`). Host must call `app.UseRateLimiter()`; the startup validator from Task 6 fails start with text `"Call app.UseRateLimiter() or set A2AServerOptions.EnableRateLimiting = false."` when the policy exists but the middleware is absent (detect via a marker set by a tiny middleware registered by `UseA2A()` — add `app.UseA2A()` extension that calls `UseRateLimiter()`; document it). Rejection writes the Task 2 `RateLimited` error with `Retry-After`.
- [ ] **Step 1: Write failing tests** (both bindings where HTTP):

```csharp
Body_over_limit_returns_413_payload_too_large      // Limits.MaxRequestBodyBytes = 1024; 2 KB text part
Too_many_parts_rejected                            // 101 parts -> InvalidRequest
Raw_part_over_limit_rejected                       // MaxRawPartBytes = 16; 32-byte raw -> PayloadTooLarge
Duplicate_json_property_rejected                   // {"message":{...},"message":{...}} -> InvalidParams / 400
Json_deeper_than_32_rejected                       // metadata nested 40 levels -> 400
History_capped_without_history_length              // MaxHistoryLengthReturned = 2, 4-turn task, GetTask(historyLength null) -> 2 messages
Rate_limit_returns_429_with_retry_after            // TokenLimit 2 via configure; 3rd request -> 429, header Retry-After present, reason RATE_LIMITED
Rate_limit_partitions_by_principal                 // alice exhausted; bob still OK
[Fact] Net8_duplicate_checker_detects_duplicates   // internal checker on "{\"a\":1,\"a\":2}" returns true
```
- [ ] **Step 2: Run** filter `LimitsTests`. Expected: FAIL.
- [ ] **Step 3: Implement** as above. Update `TestAgentHost` to call `app.UseA2A()` instead of nothing.
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): default request limits and per-caller rate limiting`.

---

### Task 8: Closed `Part` hierarchy (§7.9)

**Files:**
- Create: `src/A2A.Protocol/Models/Part.cs`, `src/A2A.Protocol/Json/PartConverter.cs`
- Modify: `src/A2A.Protocol/Models/Core.cs` (remove old `Part`, `PartKind`), every call site (`AgentExecutor.cs`, `A2ARequestHandler.cs`, `A2AValidator.cs`, `A2AClient.cs`, samples, tests)
- Test: `tests/A2A.Tests/PartTests.cs`

**Interfaces:**
- Produces:

```csharp
[JsonConverter(typeof(PartConverter))]
public abstract record Part
{
    public Dictionary<string, JsonElement>? Metadata { get; init; }
    public string? Filename { get; init; }
    public string? MediaType { get; init; }
    public static TextPart FromText(string text, string? mediaType = null);
    public static RawPart FromBytes(ReadOnlyMemory<byte> bytes, string mediaType, string? filename = null);
    public static UrlPart FromUrl(Uri url, string? mediaType = null, string? filename = null);
    public static DataPart FromData(JsonElement data, string? mediaType = "application/json");
}
public sealed record TextPart(string Text) : Part;
public sealed record RawPart(ReadOnlyMemory<byte> Raw) : Part;   // base64 on the wire
public sealed record UrlPart(Uri Url) : Part;
public sealed record DataPart(JsonElement Data) : Part;
```

  `Message.GetText()` concatenates `TextPart.Text`. `PartConverter.Read`: zero content members → `JsonException("Part must set exactly one of text, raw, url, data; found none")`; more than one → `JsonException("Part must set exactly one of text, raw, url, data; found: text, url")` (names in wire order). Bindings already map `JsonException` to `InvalidParams`. `FromData<T>` generic removed (needs reflection; callers serialize first).
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Each_part_kind_round_trips()            // TextPart, RawPart([1,2,3]) -> "raw":"AQID", UrlPart, DataPart; types preserved after round trip
[Fact] Part_with_two_contents_is_rejected()     // {"text":"a","url":"https://x"} -> JsonException message contains "found: text, url"
[Fact] Part_with_no_content_is_rejected()       // {"mediaType":"text/plain"} -> JsonException contains "found none"
[Theory] Server_rejects_multi_content_part      // both bindings -> InvalidParams
```

  Rewrite `Parts_round_trip_each_content_kind_and_omit_nulls` in `ProtocolTests.cs` to pattern-match subtypes instead of `PartKind`.
- [ ] **Step 2: Run** filter `PartTests`. Expected: compile FAIL.
- [ ] **Step 3: Implement** and update call sites (`artifact.Parts.Select(p => p.Text)` in tests becomes `((TextPart)p).Text` or `OfType<TextPart>()`).
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `refactor(protocol): closed Part hierarchy with strict converter`.

---

### Task 9: Response and security-scheme unions, required fields (§7.9)

**Files:**
- Modify: `src/A2A.Protocol/Models/Requests.cs:52-70` (`SendMessageResponse`, `StreamResponse`), `src/A2A.Protocol/Models/AgentCard.cs:112-` (`SecurityScheme`), `src/A2A.Protocol/Models/Core.cs` (`required`), `src/A2A.Protocol/Validation/A2AValidator.cs`, all call sites
- Create: `src/A2A.Protocol/Json/OneOfConverter.cs`
- Test: `tests/A2A.Tests/UnionTests.cs`

**Interfaces:**
- Produces:
  - `abstract record SendMessageResponse` with `sealed record TaskResponse(AgentTask Task)` and `sealed record MessageResponse(Message Message)`.
  - `abstract record StreamResponse` with `TaskEvent(AgentTask Task)`, `MessageEvent(Message Message)`, `StatusUpdateEvent(TaskStatusUpdateEvent StatusUpdate)`, `ArtifactUpdateEvent(TaskArtifactUpdateEvent ArtifactUpdate)`; keep `StreamResponse.Of(...)` overloads returning these.
  - `abstract record SecurityScheme` with five subtypes matching proto oneof names (`ApiKeySecurityScheme` etc. become the payload records wrapped by `ApiKeyScheme(ApiKeySecurityScheme Value)` …); keep `SecurityScheme.Bearer()` factory.
  - `OneOfConverter` base: reads object, requires exactly one of the known member names; error text format as `PartConverter`.
  - `required` on: `Message.MessageId`, `Message.Role`, `Message.Parts`; `Artifact.ArtifactId`, `Artifact.Parts`; `AgentTask.Id`, `AgentTask.Status`; `AgentCard.Name/Description/SupportedInterfaces/Version/Capabilities/DefaultInputModes/DefaultOutputModes/Skills`.
  - `A2AValidator.ValidateOutgoing(SendMessageRequest)`: `messageId` non-empty, `parts` non-empty. Client calls it before send (throws `A2AException(InvalidParams)` locally).
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Stream_response_with_two_members_rejected()   // {"task":{...},"message":{...}} -> JsonException "found: task, message"
[Fact] Send_message_response_round_trips_each_case()
[Fact] Security_scheme_round_trips_and_rejects_two() // apiKeySecurityScheme + httpAuthSecurityScheme -> JsonException
[Fact] Client_rejects_message_without_parts_before_sending() // RecordingHandler sees zero requests
```
- [ ] **Step 2: Run** filter `UnionTests`. Expected: compile FAIL.
- [ ] **Step 3: Implement** and update call sites: `evt.Message is not null` → `evt is MessageEvent`, etc.
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `refactor(protocol): discriminated unions and required members`.

---

### Task 10: Source-generated JSON, AOT-safe libraries (§5)

**Files:**
- Create: `src/A2A.Protocol/Json/A2AJsonContext.cs`
- Modify: `src/A2A.Protocol/Json/A2AJson.cs`, `Directory.Build.props` (`<IsAotCompatible>true</IsAotCompatible>` for `src` net8.0+), any reflection-based call (`GetFromJsonAsync<JwkSet>` in `AgentCardVerifier.cs:88`, `JsonSerializer.SerializeToElement(info)` in `A2AException.cs` `BuildDetails`, anonymous types)
- Test: `tests/A2A.Tests/JsonTests` (existing class in `ProtocolTests.cs`)

**Interfaces:**
- Produces: `internal partial class A2AJsonContext : JsonSerializerContext` with `[JsonSerializable]` for every model, request, response, `JsonRpcRequest/Response`, `JwkSet`, error detail type. `A2AJson.Options.TypeInfoResolver = A2AJsonContext.Default` (no `populateMissingResolver`). `A2AJson.Clone<T>` uses `JsonTypeInfo<T>` from options.
- [ ] **Step 1: Write failing test:**

```csharp
[Fact] public void Options_have_no_reflection_fallback()
{
    Assert.IsAssignableFrom<A2AJsonContext>(A2AJson.Options.TypeInfoResolver);
    Assert.Throws<NotSupportedException>(() => A2AJson.Serialize(new { a = 1 }));   // anonymous type not in context
}
```
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Set `IsAotCompatible`; fix every `IL2026`/`IL3050` warning the build reports (warnings are errors).
- [ ] **Step 4: Run** `dotnet build -warnaserror` and full tests. Expected: both pass.
- [ ] **Step 5: Commit** `build: source-generated JSON, AOT-compatible libraries`.

---

### Task 11: Event store with per-task sequence (§7.4 storage)

**Files:**
- Create: `src/A2A.Server/Storage/EventStore.cs`, `src/A2A.Server/Storage/InMemoryEventStore.cs`, `src/A2A.Server/Streaming/SequencedEvent.cs`
- Modify: `src/A2A.Server/TaskEventBus.cs` (publish `SequencedEvent`), `src/A2A.Server/A2ARequestHandler.cs` (`ConsumeAsync`, `Publish`, `SubscribeToTaskAsync`, streaming methods now yield `SequencedEvent`), `src/A2A.Server/Push/PushNotifications.cs` (`Enqueue(TaskKey, SequencedEvent)`; sequence comes from the event, remove sender-side counter), `src/A2A.Server/A2AServerOptions.cs`
- Test: `tests/A2A.Tests/EventStoreTests.cs`

**Interfaces:**
- Consumes: `TaskKey` (Task 3), `StreamResponse` subtypes (Task 9).
- Produces:

```csharp
public readonly record struct SequencedEvent(long Sequence, StreamResponse Event);   // Sequence 0 = not stored (direct message reply)
public abstract class EventStore
{
    public abstract ValueTask<long> AppendAsync(TaskKey task, StreamResponse evt, CancellationToken ct = default);   // first event of a task gets 1
    public abstract IAsyncEnumerable<SequencedEvent> ReadAfterAsync(TaskKey task, long afterSequence, CancellationToken ct = default);
    public abstract ValueTask<long?> OldestRetainedAsync(TaskKey task, CancellationToken ct = default);   // null if no events retained
    public abstract ValueTask<long> LastSequenceAsync(TaskKey task, CancellationToken ct = default);      // 0 if none
    public abstract ValueTask MarkTerminalAsync(TaskKey task, CancellationToken ct = default);
}
public sealed class StreamingOptions { public int ReplayBufferSize { get; set; } = 1000; public TimeSpan TerminalRetention { get; set; } = TimeSpan.FromMinutes(10); public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(15); }
```

  `A2AServerOptions.Streaming : StreamingOptions`. Move keep-alive from `A2AHttpOptions.StreamKeepAliveInterval` here. Order inside the per-task lock: state-machine check → `EventStore.AppendAsync` → `TaskStore.UpdateAsync` → after lock: bus publish, push enqueue, emit to initiator. Initial `Task` snapshot event is appended too (it is the first event, sequence 1). In-memory store trims to the last `ReplayBufferSize` per task and evicts a terminal task's buffer `TerminalRetention` after `MarkTerminalAsync` (timer via `TimeProvider`, injected; default `TimeProvider.System`).
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Sequences_start_at_1_and_increase_per_task()           // two tasks interleaved; each 1,2,3
[Fact] Buffer_trims_to_replay_size()                          // ReplayBufferSize 3, append 5 -> OldestRetained == 3
[Fact] Terminal_buffer_evicted_after_retention()              // FakeTimeProvider (write a tiny one in tests; no package) advance 11 min -> OldestRetained null
[Theory] Push_sequence_header_matches_event_sequence           // webhook X-A2A-Notification-Sequence values equal event-store sequences
```
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): event store with per-task sequence`.

---

### Task 12: Resumable SSE on the server (§7.4 wire, §7.2 stream re-auth)

**Files:**
- Modify: `src/A2A.Server.AspNetCore/HttpPlumbing.cs:68-150` (`id:` frames, `Last-Event-ID`, keep-alive text `: ka`), `src/A2A.Server.AspNetCore/JsonRpcEndpoint.cs`, `src/A2A.Server.AspNetCore/RestEndpoint.cs`, `src/A2A.Server/A2ARequestHandler.cs` (`SubscribeToTaskAsync(request, call, long? resumeAfter, ct)`, stream limits, re-auth loop)
- Create: `src/A2A.Server/Streaming/StreamCursor.cs`, `src/A2A.Server/Streaming/StreamAdmission.cs`
- Test: `tests/A2A.Tests/StreamingResumeTests.cs`

**Interfaces:**
- Consumes: `EventStore`, `SequencedEvent` (Task 11); `A2AAuthorizationHandler` (Task 6); `A2ALimits` (Task 7).
- Produces:
  - `public static class StreamCursor { public static string Format(string taskId, long seq); public static bool TryParse(string? value, string expectedTaskId, out long seq); }` — format `"{taskId}:{seq}"`; `TryParse` false for null, malformed, non-positive, or other task id.
  - SSE frame: `id: {cursor}\ndata: {json}\n\n` when `Sequence > 0`; no `id:` line for sequence 0.
  - Resume rules in `SubscribeToTaskAsync` when `resumeAfter` is given (from a valid `Last-Event-ID`):
    1. cursor ≥ oldest retained − 1 → replay `ReadAfterAsync(cursor)` then live, de-duplicating by sequence (subscribe to bus before reading the store; drop live events with `Sequence <= lastYielded`).
    2. cursor older than retained, or no events retained → yield current `Task` snapshot (as `TaskEvent`, with the store's last sequence as its id), then live.
    3. task terminal and cursor ≥ last sequence → yield `Task` snapshot and complete.
  - Without `Last-Event-ID`, existing behaviour stays: terminal task → `UnsupportedOperation`.
  - `SendStreamingMessage` with a `Last-Event-ID` header: treated as a resume of the task named in the cursor (look up by the cursor's task id under the caller's key); invalid cursor → ignore header and process as a normal send.
  - `StreamAdmission`: per-task and per-principal counters; exceeding `MaxConcurrentStreamsPerTask` or `MaxConcurrentStreamsPerPrincipal` → `RateLimited(TimeSpan.FromSeconds(1))`.
  - Every stream: after `StreamReauthInterval`, calls `A2AAuthorizationHandler.AuthorizeAsync(call, op, request, ct)`; false → yields nothing more and ends. At `MaxStreamDuration` the stream ends normally (no error event).
  - Event larger than `MaxEventBytes` after serialization → producer's execution fails the task with `FAILED` and message `"The agent produced an event larger than the configured limit."`.
- [ ] **Step 1: Write failing tests** (both bindings unless marked):

```csharp
Frames_carry_task_scoped_ids                 // raw SSE read: every data frame after first has "id: {taskId}:{n}", n increasing by 1
Resume_replays_missed_events_without_gaps    // "task" scenario: read 2 frames, drop connection, resubscribe with Last-Event-ID of frame 2 -> remaining frames, sequences contiguous, no duplicates
Resume_with_trimmed_cursor_gets_snapshot     // ReplayBufferSize 2; resume from seq 1 -> first frame is TaskEvent
Resume_on_finished_task_returns_snapshot_and_closes
Fresh_subscribe_on_finished_task_is_unsupported   // no Last-Event-ID -> UnsupportedOperation (spec-exact)
Cursor_for_other_task_or_garbage_gets_snapshot    // Review Focus 2: Last-Event-ID "other:5" and "abc" -> first frame is TaskEvent of requested task
Keep_alive_comment_sent_when_idle            // KeepAliveInterval 50 ms; "slow" scenario; raw body contains ": ka"
Too_many_streams_per_task_rejected           // MaxConcurrentStreamsPerTask 1; second subscribe -> RATE_LIMITED
Stream_ends_when_authorization_revoked       // StreamReauthInterval 100 ms; handler flips to deny; stream completes within 1 s
Oversized_event_fails_task                   // MaxEventBytes 1024; scenario "big" (add to ScenarioAgent: artifact with 2 KB text) -> task FAILED with the exact message
```
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): resumable SSE with cursors, stream limits and re-auth`.

---

### Task 13: Client stream resume and `ObserveTaskAsync` (§7.4 client)

**Files:**
- Modify: `src/A2A.Client/Transports/JsonRpcTransport.cs`, `src/A2A.Client/Transports/HttpJsonTransport.cs`, `src/A2A.Client/Transports/HttpTransportBase.cs`, `src/A2A.Client/A2AClient.cs`, `src/A2A.Client/IA2AClient.cs`
- Create: `src/A2A.Client/Streaming/ResumableStream.cs`
- Test: `tests/A2A.Tests/ClientResumeTests.cs`

**Interfaces:**
- Consumes: SSE `id:` frames (Task 12); `SseEvent.Id` (exists).
- Produces:
  - Transports expose `SubscribeToTaskAsync(SubscribeToTaskRequest, string? lastEventId, CancellationToken)` (internal overload) sending `Last-Event-ID`.
  - `A2AClient.ObserveTaskAsync(string taskId, CancellationToken ct = default) : IAsyncEnumerable<StreamResponse>`: subscribe; track last `id`; on `HttpRequestException`/`IOException` reconnect with backoff 0.5 s, 1 s, 2 s, 4 s, 8 s (max 5 attempts, then rethrow); on `UnsupportedOperation` from a terminal task, call `GetTaskAsync` and yield it as `TaskEvent`, then complete.
  - `A2AClient.SendStreamingMessageAsync` uses the same reconnect logic once the first event revealed a task id.
- [ ] **Step 1: Write failing tests:**

```csharp
[Theory] Observe_survives_connection_drop       // DelegatingHandler in the test HttpClient aborts the response stream after 2 frames once; observed sequence of events equals an uninterrupted run
[Theory] Observe_finished_task_yields_final_snapshot
[Theory] Observe_gives_up_after_five_failures   // handler always throws HttpRequestException -> exception after 5 attempts (use injectable TimeProvider for backoff)
```
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Backoff waits use an injectable `TimeProvider` (`A2AClientOptions.TimeProvider`, default `TimeProvider.System`).
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(client): resumable streams and ObserveTaskAsync`.

---

### Task 14: Idempotent `SendMessage` on the server (§7.3 server)

**Files:**
- Create: `src/A2A.Server/Idempotency/IdempotencyStore.cs`, `src/A2A.Server/Idempotency/InMemoryIdempotencyStore.cs`, `src/A2A.Server/Idempotency/RequestHasher.cs`
- Modify: `src/A2A.Server/A2ARequestHandler.cs` (`SendMessageAsync`, `SendStreamingMessageAsync`), `src/A2A.Server/ServerCallContext.cs` (add `IdempotencyKey : string?`), `src/A2A.Server.AspNetCore/HttpPlumbing.cs` (read `Idempotency-Key`), `src/A2A.Protocol/A2AProtocol.cs` (`Headers.IdempotencyKey = "Idempotency-Key"`), `src/A2A.Server/A2AServerOptions.cs`
- Test: `tests/A2A.Tests/IdempotencyTests.cs`

**Interfaces:**
- Consumes: `TaskKey`, `EventStore.ReadAfterAsync` (Tasks 3, 11); `A2AException.IdempotencyKeyMismatch()` (Task 2).
- Produces:

```csharp
public readonly record struct IdempotencyScope(string Tenant, string Principal, string Key);
public abstract record IdempotencyResult
{
    public sealed record New : IdempotencyResult;
    public sealed record InProgress(Task<IdempotencyOutcome> Completion) : IdempotencyResult;
    public sealed record Completed(IdempotencyOutcome Outcome) : IdempotencyResult;
    public sealed record Mismatch : IdempotencyResult;
}
public abstract record IdempotencyOutcome { public sealed record TaskCreated(string TaskId) : IdempotencyOutcome; public sealed record DirectMessage(Message Message) : IdempotencyOutcome; }
public abstract class IdempotencyStore
{
    public abstract ValueTask<IdempotencyResult> TryBeginAsync(IdempotencyScope scope, byte[] requestHash, CancellationToken ct = default);
    public abstract ValueTask CompleteAsync(IdempotencyScope scope, IdempotencyOutcome outcome, CancellationToken ct = default);
    public abstract ValueTask AbandonAsync(IdempotencyScope scope, CancellationToken ct = default);   // on failure before any task/message exists
}
public sealed class IdempotencyOptions { public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24); public int MaxEntries { get; set; } = 100_000; }
public static class RequestHasher { public static byte[] Hash(SendMessageRequest request); }   // SHA-256 over JsonCanonicalizer.Canonicalize of {message, configuration} serialized with A2AJson
```

  `A2AServerOptions.Idempotency : IdempotencyOptions`. Key = `call.IdempotencyKey ?? request.Message.MessageId`. In-memory store: `TryBeginAsync` atomic via `ConcurrentDictionary.GetOrAdd` with a `TaskCompletionSource<IdempotencyOutcome>`; LRU eviction over `MaxEntries` increments counter `a2a.idempotency.evictions` on meter `"A2A"`.
  Behaviour on `Completed`/`InProgress` (after awaiting completion with the request's cancellation token):
  - `SendMessage` + `TaskCreated` → return current task, shaped with the request's `historyLength`.
  - `SendMessage` + `DirectMessage` → return the same message.
  - `SendStreamingMessage` + `TaskCreated` → stream `ReadAfterAsync(0)` then live (same code path as resume from cursor 0).
  - `SendStreamingMessage` + `DirectMessage` → single `MessageEvent`.
- [ ] **Step 1: Write failing tests** (both bindings):

```csharp
Retry_with_same_message_id_returns_same_task          // send "task" twice with identical Message (same messageId) -> same Task.Id; store holds one task
Idempotency_header_is_the_key_when_present            // send A: messageId m1, Idempotency-Key K. send B: messageId m2, same key K -> 422 IDEMPOTENCY_KEY_MISMATCH (proves K, not m2, was the key)
Same_key_different_payload_is_mismatch                // same messageId, different text -> 422 IDEMPOTENCY_KEY_MISMATCH
Concurrent_duplicates_create_one_task                 // Review Focus 1: Task.WhenAll of 10 identical sends of "slow" with returnImmediately -> 1 distinct task id
Keys_are_scoped_per_principal                         // Review Focus 3: alice and bob send same Idempotency-Key and same body -> different task ids; bob's GetTask on alice's id -> TaskNotFound
Streaming_retry_replays_from_start                    // stream "task", then same request again -> second stream's events equal the first's
Direct_reply_is_replayed                              // "hello" twice -> same reply MessageId
Entry_expires_after_retention                         // Retention via FakeTimeProvider advance 25 h -> second send creates a new task
```
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Begin idempotency after authorization and before context/task resolution. Record `TaskCreated` as soon as the task is first persisted (so concurrent duplicates attach to the live execution). `AbandonAsync` when `PrepareAsync` throws.
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(server): idempotent SendMessage keyed by Idempotency-Key or messageId`.

---

### Task 15: Client safe defaults, per-call options, retry, idempotency key (§7.3 client, §7.8)

**Files:**
- Create: `src/A2A.Client/A2ARequestOptions.cs`, `src/A2A.Client/Resilience/RetryPolicy.cs`
- Modify: `src/A2A.Client/IA2AClient.cs`, `src/A2A.Client/A2AClient.cs`, transports, `src/A2A.Client/DependencyInjection.cs`, `src/A2A.Client/A2AClientFactory.cs`, `src/A2A.Client/A2ACardResolver.cs` (shared handler defaults)
- Test: `tests/A2A.Tests/ClientDefaultsTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class A2ARequestOptions { public IDictionary<string,string> Headers { get; } ; public string? IdempotencyKey { get; set; } ; public TimeSpan? Timeout { get; set; } ; public IList<string>? Extensions { get; set; } ; public Func<HttpRequestMessage, CancellationToken, ValueTask>? ConfigureRequest { get; set; } }`.
  - Every `IA2AClient` method gains a trailing `A2ARequestOptions? options` parameter placed before `CancellationToken` (suppress CA1068 is not needed: `CancellationToken` stays last).
  - `SendMessageAsync`/`SendStreamingMessageAsync`: if `message.MessageId` is empty, set `Message.NewId()`; header `Idempotency-Key` = `options.IdempotencyKey ?? message.MessageId`.
  - Default unary timeout 100 s; streams: no timeout. Implemented with a linked `CancellationTokenSource`; `HttpClient.Timeout = Timeout.InfiniteTimeSpan` on SDK-created clients.
  - `AddA2AClient` primary handler: `SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false }`.
  - `RetryPolicy`: max 3 attempts; backoff `200 ms * 2^(attempt-1)` with ±20 % jitter; honours `Retry-After` (seconds); retries on `HttpRequestException`, HTTP 503, HTTP 429, or `A2AException.IsRetryable`. Applies to: `SendMessage`, `GetTask`, `ListTasks`, `CancelTask`, `GetTaskPushNotificationConfig`, `ListTaskPushNotificationConfigs`, `GetExtendedAgentCard`. Not to: create/delete push config, streaming calls (those use Task 13 resume).
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Send_sets_message_id_and_idempotency_header()           // RecordingHandler captures header == body messageId, both non-empty
[Fact] Explicit_idempotency_key_wins()
[Fact] Retries_send_on_503_with_same_key()                     // handler returns 503 then 200; 2 requests, identical Idempotency-Key and body
[Fact] Does_not_retry_create_push_config_on_503()               // 1 request, exception surfaced
[Fact] Honours_retry_after()                                    // 429 with Retry-After: 2 -> FakeTimeProvider sees a 2 s wait
[Fact] Per_call_headers_are_sent()
[Fact] Unary_timeout_applies_but_stream_has_none()              // handler delays 200 ms; Timeout 50 ms -> TaskCanceledException for GetTask
[Fact] Default_handler_has_no_cookies_and_no_redirects()        // resolve named HttpClient's primary handler via IHttpMessageHandlerFactory inspection or a 302 test server -> client gets 302, no follow
```
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(client): per-call options, safe HTTP defaults, retry, idempotency key`.

---

### Task 16: Push delivery hardening and receiver helper (§7.6 delivery)

**Files:**
- Modify: `src/A2A.Server/Push/PushNotifications.cs` (`PushNotificationOptions`, `PushUrlValidator`, `HttpPushNotificationSender`), `src/A2A.Protocol/A2AProtocol.cs` (`Headers.DeliveryId = "X-A2A-Delivery-Id"`)
- Create: `src/A2A.Client/Push/PushNotificationReceiver.cs`
- Test: `tests/A2A.Tests/PushDeliveryTests.cs`

**Interfaces:**
- Consumes: `SequencedEvent` (Task 11), `PushConfigStore`, `IPushSecretProtector` (Task 5).
- Produces:
  - `PushNotificationOptions.AllowedNetworks : IList<(IPAddress Network, int PrefixLength)>` — targets inside these bypass the private-range block. Keep `AllowedHosts`, `AllowPrivateNetworks`, `AllowInsecureUrls`.
  - `PushUrlValidator.IsPrivate(IPAddress)` covers (and tests list): 127.0.0.0/8, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16, 100.64.0.0/10, 0.0.0.0/8, 224.0.0.0/4, 255.255.255.255, ::1, ::, fc00::/7, fe80::/10, ff00::/8, and IPv4-mapped (`::ffff:a.b.c.d`) forms of the IPv4 ranges.
  - URL re-validated (scheme, host allowlist, DNS + IP check) at each delivery attempt, not only at create.
  - `PushUrlValidator.CreateHandler` sets `UseProxy = false` unless `PushNotificationOptions.Proxy : IWebProxy?` is set; `UseCookies = false`; `AllowAutoRedirect = false`.
  - Headers per delivery: `X-A2A-Notification-Token: {token}` when token set; `Authorization`: from `authentication` if present, else `Bearer {token}` when token set; `X-A2A-Notification-Sequence: {seq}`; `X-A2A-Delivery-Id: {Guid "N"}` (same id across retries of one delivery).
  - `public sealed class PushNotificationReceiver(TimeProvider? time = null) { public PushValidationResult Validate(IReadOnlyDictionary<string,string> headers, string expectedToken); }` with `enum PushValidationResult { Valid, InvalidToken, Duplicate }`. Token compare via `CryptographicOperations.FixedTimeEquals` over UTF-8 bytes; accepts token from either header; remembers delivery ids for 10 minutes.
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Bearer_and_token_headers_both_sent()            // RecordingHandler: Authorization "Bearer s3cret" and X-A2A-Notification-Token "s3cret"
[Fact] Delivery_id_stable_across_retries()             // handler returns 500 then 200 -> two calls, same X-A2A-Delivery-Id
[Theory]                                               // Review Focus 4
[InlineData("https://[::ffff:127.0.0.1]/h")] [InlineData("https://0x7f000001/h")] [InlineData("https://2130706433/h")]
[InlineData("https://[fd00::1]/h")] [InlineData("https://100.64.0.1/h")] [InlineData("https://169.254.169.254/h")]
Obfuscated_private_targets_rejected(string url)       // PushUrlValidator.Validate throws InvalidParams (AllowPrivateNetworks false)
[Fact] Hostname_resolving_private_rejected_at_delivery // inject a resolver returning 10.0.0.5 for "evil.example"; config created while it resolved public; delivery skipped and logged
[Fact] Allowed_network_bypasses_block()                // AllowedNetworks 10.0.0.0/8 -> 10.0.0.5 accepted
[Fact] Receiver_accepts_either_header_and_dedupes()
[Fact] Receiver_rejects_wrong_token()
[Fact] Push_handler_uses_no_proxy_cookies_or_redirects() // inspect CreateHandler(new()) properties
```

  Add an internal seam `Func<string, CancellationToken, ValueTask<IPAddress[]>> PushUrlValidator.Resolve` (default `Dns.GetHostAddressesAsync`) settable from tests via `InternalsVisibleTo`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.** Parse numeric host forms through `IPAddress.TryParse` after normalising hex/decimal (`Uri` already normalises `0x7f000001` and `2130706433` to `127.0.0.1`; assert in the test that the guard sees the normalised address).
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(push): bearer + token headers, delivery id, per-delivery validation, receiver helper`.

---

### Task 17: Card canonicalization confirmed and reworked (§7.5 canonicalization)

**Files:**
- Modify: `src/A2A.Protocol/Signing/AgentCardCanonicalizer.cs`, `src/A2A.Protocol/Signing/AgentCardSigner.cs`
- Create: `tests/A2A.Tests/Vectors/card-signing/*.json`, `tests/A2A.Tests/Vectors/README.md`, `tests/A2A.Tests/CanonicalizationVectorTests.cs`

**Interfaces:**
- Produces: `AgentCardCanonicalizer.Canonicalize(JsonObject card) : byte[]` (single entry point; `Normalized`/`Verbatim` removed unless step 1 shows a2a-js needs `Verbatim`). `AgentCardSigner.Sign` throws `ArgumentException("Card contains values that canonicalization would remove: {json paths}")` when the card has values the pruning rule removes.

- [ ] **Step 1: Confirm the pruning rule (developer step, outside CI).** Read a2a-python 1.2.1 card signing code (`a2a/utils/signing.py` or the module the release uses) and a2a-js 1.3.0 equivalent. Record in `tests/A2A.Tests/Vectors/README.md`: exactly which values each removes (`null`, `""`, `[]`, `{}`, `false`, `0`, enum zero) and whether REQUIRED fields are kept when empty. If the two SDKs disagree, follow a2a-python and note the difference.
- [ ] **Step 2: Generate vectors.** With a2a-python 1.2.1 in a throwaway virtualenv (not committed), sign at least 6 cards: minimal; full (all optional fields); with empty optional strings/lists; with `false` capabilities; with unicode/escapes in description; with extension params. Save each as `{name}.input.json`, `{name}.canonical.txt`, `{name}.signed.json`, plus the public JWKS `keys.jwks.json`. Note the exact commands in the README.
- [ ] **Step 3: Write failing tests:**

```csharp
[Theory] [MemberData(nameof(VectorNames))]
public void Canonical_bytes_match_python(string name)   // Canonicalize(input) == File.ReadAllBytes(canonical)
[Theory] [MemberData(nameof(VectorNames))]
public async Task Python_signatures_verify(string name) // AgentCardVerifier with StaticKeyResolver(keys.jwks) on signed.json -> Verified
[Fact] public void Signer_refuses_card_with_prunable_values() // card with Description "" in a skill -> ArgumentException listing "$.skills[0].description"
```

  Copy vectors to output via `<None Include="Vectors\**" CopyToOutputDirectory="PreserveNewest" />` in the test csproj.
- [ ] **Step 4: Run.** Expected: FAIL (current rule strips proto defaults and keeps REQUIRED empties differently).
- [ ] **Step 5: Implement** the rule recorded in step 1. Keep the existing spec-example test passing or, if it contradicts a2a-python, rewrite it and note why in the commit.
- [ ] **Step 6: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 7: Commit** `feat(signing): canonicalization matching a2a-python with committed vectors`.

---

### Task 18: Card trust policy, pinning, untrusted text (§7.5 verification)

**Files:**
- Modify: `src/A2A.Protocol/Signing/AgentCardVerifier.cs`, `src/A2A.Client/A2ACardResolver.cs`, `src/A2A.Client/DependencyInjection.cs`
- Create: `src/A2A.Protocol/Signing/CardTrustPolicy.cs`, `src/A2A.Client/Cards/ICardPinStore.cs`, `src/A2A.Client/Cards/InMemoryCardPinStore.cs`, `src/A2A.Client/Cards/UntrustedText.cs`
- Test: `tests/A2A.Tests/CardTrustTests.cs`

**Interfaces:**
- Consumes: `AgentCardCanonicalizer.Canonicalize` (Task 17).
- Produces:
  - `public enum AgentCardVerificationStatus { Verified, Unsigned, Unverified, Failed }` (replaces current values).
  - `public sealed record AgentCardVerificationResult(AgentCardVerificationStatus Status, string? KeyId, string? Issuer, string? Reason)`.
  - `public sealed class CardTrustPolicy { public IDictionary<string, JwkSet> TrustedKeys { get; } /* key = origin "https://host[:port]" */ ; public ISet<string> AllowedJkuHosts { get; } ; public bool? RequireSignature { get; set; } /* null = true when TrustedKeys or AllowedJkuHosts non-empty */ ; public ISet<string> AcceptedTyp { get; } = {"JOSE", "JOSE+JSON"}; }`.
  - Status rules: no signatures → `Unsigned`. `typ` missing or not accepted, or `alg` not allowed → `Failed`. A signature whose key is found in `TrustedKeys[origin]` or in a JWKS fetched from an allowlisted `jku` host (HTTPS only, fetched through an `HttpClient` whose handler uses the push SSRF guard `PushUrlValidator.CreateHandler`) and verifies → `Verified`. Key found as above but signature invalid → `Failed`. No applicable trusted key (including same-host `jku` not in `AllowedJkuHosts`) → `Unverified`.
  - `StaticKeyResolver` and `JwksKeyResolver` replaced by the policy; `CompositeKeyResolver` removed. `A2ACardResolverOptions.SignaturePolicy` and the `SignaturePolicy` enum are removed.
  - Algorithms: keep the existing `JwsAlgorithms.All` set (ES256/384/512, RS256/384/512, PS256/384/512); it is a superset of the spec's list. EdDSA is not offered because the BCL has no public Ed25519 type on net8.0/net10.0. (plan call)
  - Resolver: `A2ACardResolverOptions.Trust : CardTrustPolicy`, `PinPolicy : PinPolicy { Notify, Strict, Off }` default `Notify`, `PinStore : ICardPinStore` default `InMemoryCardPinStore`, `MaxCardTextLength = 2000`. `Failed` always throws `AgentCardVerificationException`; `Unsigned`/`Unverified` throw only when effective `RequireSignature` is true. Pin = SHA-256 of `Canonicalize(card)` per origin; differing hash → event `CardChanged(origin, oldHash, newHash)` + warning log + counter `a2a.card.changed`; `Strict` throws `AgentCardPinException`. Same hash with different `kid` → information log only.
  - `public readonly struct UntrustedText { public string Raw { get; } ; public override string ToString(); }` — `ToString` strips C0/C1 control chars except `\n` and `\t`, removes Unicode bidi controls (U+202A–U+202E, U+2066–U+2069), truncates to `MaxCardTextLength` with `…`.
  - `ResolvedAgentCard` gains `Description : UntrustedText`, `Skills : IReadOnlyList<ResolvedSkill>` with `Name`, `Description`, `Examples` as `UntrustedText`, and `Identity : (string Origin, string? KeyId)`.
- [ ] **Step 1: Write failing tests:**

```csharp
[Fact] Forged_card_with_attacker_jku_is_never_verified()     // attacker-signed card, jku "https://evil.example/jwks". (a) no trust configured -> Status Unverified, resolver returns the card. (b) TrustedKeys holds the real key for the origin -> attacker kid not trusted -> Unverified, effective RequireSignature true -> resolver throws AgentCardVerificationException
[Fact] Same_host_jku_without_allowlist_is_unverified()
[Fact] Allowlisted_jku_verifies()                            // fake JWKS endpoint via test handler
[Fact] Trusted_key_wrong_signature_fails()                   // tamper one byte -> Failed, resolver throws even with RequireSignature false
[Fact] Missing_typ_fails()
[Fact] Trusted_keys_are_per_origin()                         // key trusted for https://a.example does not verify card from https://b.example -> Unverified
[Fact] Card_change_raises_event_and_strict_throws()          // serve card v1 then v2 (different description)
[Fact] Rotated_key_same_content_is_not_a_change()
[Fact] Untrusted_text_strips_controls_and_truncates()        // "a\u0007b‮c" -> "abc"; 3000-char string -> 2000 chars ending with "…"
[Fact] Unsigned_card_allowed_when_no_trust_configured()      // default options -> resolves, Status Unsigned
```

  Update existing `SigningTests` (`Sign_then_verify_succeeds_and_tampering_fails`, `Jwks_resolver_refuses_cross_host_and_insecure_jku`, `Unsigned_card_reports_unsigned`, `Alg_none_and_unknown_kid_are_rejected`) to the new API and statuses; `unknown kid` now yields `Unverified`.
- [ ] **Step 2: Run.** Expected: compile FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run full tests.** Expected: `Failed: 0`.
- [ ] **Step 5: Commit** `feat(signing): per-origin trust policy, jku allowlist, card pinning, untrusted text`.

---

### Task 19: Transport parity suite and named regressions (§7.10)

**Files:**
- Create: `tests/A2A.Tests/Parity/ParityTests.cs`, `tests/A2A.Tests/Parity/RegressionTests.cs`
- Modify: `tests/A2A.Tests/TestHost.cs` (scenarios `"stream-error"`, `"persist-fail"` as needed)

**Interfaces:**
- Consumes: everything above.
- Produces: no product code unless a test exposes a parity bug; fix such bugs in the owning file with its own commit.

- [ ] **Step 1: Write `ParityTests`** as `[Theory]` over both bindings for each of the 11 methods × these cases: happy path; `metadata` round-trip (request metadata reaches `AgentRequestContext.Metadata` / cancel metadata reaches `IAgentExecutor.CancelAsync`); tenant in route vs body (match, mismatch → `InvalidParams`); `historyLength` 0/1/null; foreign owner → `TaskNotFound`; handler denial. Each case is one test method named `{Method}_{Case}`.
- [ ] **Step 2: Write `RegressionTests`**, one test each, named after the a2a-dotnet issue:

```csharp
Issue478_error_after_stream_headers_becomes_error_event     // agent throws after first event -> client sees FAILED status event, stream closes cleanly
Issue495_persistence_failure_mid_stream_is_not_silent_eof   // TaskStore throws on 2nd update -> client sees an error, not a clean end
Issue551_jsonrpc_error_inside_stream_surfaces_as_exception
Issue508_input_required_continuation_over_jsonrpc_returns_task
Issue593_rest_cancel_passes_metadata
Issue591_rest_get_subscribe_works                            // existing Rest_subscribe_accepts_get_as_in_proto may be moved here
Issue540_duplicate_json_properties_rejected                  // may reference Task 7 test
Issue350_background_exception_does_not_leave_task_working    // returnImmediately + "fail" -> eventually FAILED
```
- [ ] **Step 3: Run** `dotnet test tests/A2A.Tests --filter "FullyQualifiedName~Parity"`. Expected: PASS, or failures that reveal bugs.
- [ ] **Step 4: Fix any failure** in the owning product file; commit each fix separately as `fix({area}): {what}`.
- [ ] **Step 5: Update samples.** `EchoAgent` uses `app.UseA2A()`, `MapA2A("/").RequireAuthorization()`, signing key from configuration, and `o.Limits` defaults. `EchoClient` uses `ObserveTaskAsync` and `CardTrustPolicy` with the agent's JWKS. Build both with `-warnaserror`.
- [ ] **Step 6: Final verification.** `dotnet build -warnaserror` and `dotnet test tests/A2A.Tests`. Expected: `Failed: 0`. Record the pass count in the commit message.
- [ ] **Step 7: Commit** `test: transport parity suite and named regressions`.
