# Agent Framework adapter: plan

Spec: `docs/superpowers/specs/2026-11-12-agent-framework-adapter-design.md`. Read it; it binds. Where this plan and the spec disagree, the spec wins; stop and raise it.

Nine tasks, each one commit, each green on its own. Tasks 1 and 2 touch existing packages and change no behaviour; the rest add the new package.

## Global Constraints

- Build gates: `dotnet build A2A.slnx -c Release` ends with 0 warnings. PublicApiAnalyzers: every new public member goes in the project's `PublicAPI.Unshipped.txt`. XML docs on every public member. Public `CancellationToken` parameters are named `cancellationToken`. Namespace follows folder (`tests/A2A.Tests/PackageRulesTests.cs`, `NamespaceRulesTests`).
- No `InternalsVisibleTo` between shipped packages (`No_InternalsVisibleTo_targets_a_src_assembly`). Code shared between `A2A.Extensions.AI` and the new package goes in `src/Shared/AI/` as linked source (Task 1), the way `src/Shared` already serves `A2A.Protocol` and `A2A.Client`.
- The new package references **only** `Microsoft.Agents.AI.Abstractions` (1.28.0) from Agent Framework. Never `Microsoft.Agents.AI`, `Microsoft.Agents.AI.A2A`, `Microsoft.Agents.AI.Hosting*` or the NuGet package `A2A`: the official `A2A` assembly defines `A2A.AgentCard`, `A2A.Message`, `A2A.Part`, `A2A.Artifact` and `A2A.AgentTask`, the same full names as `A2A.Protocol`. Task 3 adds a rule test for this.
- New public types carry `[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]`, as `A2AEXP005` types do. Not on properties of existing types (the System.Text.Json 8 source generator issue noted in the v1.1 design).
- Source-generated or hand-written JSON only (`Utf8JsonWriter`/`Utf8JsonReader`); no reflection serializer paths.
- Shipped public API does not change shape. In particular the shipped `A2ARetentionService` constructor stays; Task 2 adds an overload.
- Plain English in docs and XML comments. Short sentences. Docs guides use inline code only, no C# fenced blocks (`docs/site/README.md`).
- Commit messages end with:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  ```
- Code below is a sketch of shape and names, written without a compiler. Make it build; keep the names, signatures and behaviour.

## Task 1: Move shared executor code to `src/Shared/AI` (no behaviour change)

**Files:** create `src/Shared/AI/ArtifactChunker.cs`, `src/Shared/AI/HistoryPrompt.cs`, `src/Shared/AI/ModelContent.cs`; edit `src/A2A.Extensions.AI/A2A.Extensions.AI.csproj`, `ChatClientAgentExecutor.cs`, `A2AContentExtensions.cs`.

1. `ArtifactChunker`: the private `Run` class of `ChatClientAgentExecutor`, unchanged in behaviour, taking `(string artifactName, int streamChunkSize, TaskUpdater updater)` instead of `ChatClientAgentOptions`. Keeps `AddAsync(AIContent, ct)`, `FinishAsync(ct)`, `Reply`, `HasOutput`, `HasUnresolvedToolCall`. `ErrorContent` still throws; `FunctionCallContent { InformationalOnly: false }` still sets `HasUnresolvedToolCall`.
2. `HistoryPrompt.BuildAsync(AgentRequestContext context, HistoryPromptOptions options, CancellationToken ct)`: the body of `ChatClientAgentExecutor.BuildPromptAsync`. `HistoryPromptOptions` is an internal record: `Instructions` (nullable), `MaxHistoryMessages`, `AllowUrlParts`, `UseContextMemory`, `ContextMemoryMaxTasks`, `ContextMemoryMaxBytes`. The AF executor passes `Instructions = null`.
3. `ModelContent`: the internal members of `A2AContentExtensions` (`ToModelContent`, `ConvertToPart`, `ToMetadata`) and the private helpers they need. `A2AContentExtensions`' public methods call them; its public surface does not change.
4. Each file starts with:
   ```csharp
   #if A2A_AGENT_FRAMEWORK
   namespace A2A.Extensions.AgentFramework.Shared;
   #else
   namespace A2A.Extensions.AI.Shared;
   #endif
   ```
   All types `internal`. The two copies then never share a full name, so no CS0436 when the new package references `A2A.Extensions.AI`.
5. `A2A.Extensions.AI.csproj`: `<Compile Include="..\Shared\AI\*.cs" Link="Shared\AI\%(Filename)%(Extension)"/>`.

**Tests:** none new. All of `tests/A2A.Extensions.AI.Tests` passes unchanged; that is the check that nothing moved in behaviour. Also run `NamespaceRulesTests` (it skips `src/Shared`).

Commit: `refactor(extensions-ai): move the chunker, prompt builder and model content helpers to shared source`.

## Task 2: Context lifecycle hook in `A2A.Server` (D33)

**Files:** create `src/A2A.Server/Retention/IA2AContextLifecycle.cs`; edit `A2ARetentionService.cs`, `ServiceCollectionExtensions.cs`, `ServerLog.cs`, `src/A2A.Server/PublicAPI.Unshipped.txt`; edit `tests/A2A.Tests/RetentionTests.cs`.

```csharp
namespace A2A.Server.Retention;

/// <summary>
/// Told when retention deletes a context, so state kept outside the task store for that context (an agent session, a cache)
/// can go with it. Register any number in DI.
/// </summary>
public interface IA2AContextLifecycle
{
    /// <summary>Called once for each context retention deleted. An exception is logged; it does not stop the sweep.</summary>
    ValueTask OnContextDeletedAsync(ContextRecord context, CancellationToken cancellationToken);
}
```

- `A2ARetentionService`: a new public constructor with the shipped parameters plus `IEnumerable<IA2AContextLifecycle>? lifecycles = null` last; its last four parameters are optional. The shipped constructor keeps its signature but loses its `= null` defaults (PublicApiAnalyzers RS0027: the overload with optional parameters must have the most parameters; `*REMOVED*` the old shipped line in `PublicAPI.Unshipped.txt`). It calls the new one with null. Store as an array; a null entry throws `ArgumentException`.
- `DeleteOrphanContextsAsync`: when `DeleteContextAsync` returns true, call each hook in its own `try`/`catch`. Rethrow `OperationCanceledException` when the sweep token is cancelled (as `StepAsync` does); log anything else with a new `ServerLog.ContextLifecycleHookFailed(logger, ex, contextId)` and continue with the next hook and the next context. Return the delete result unchanged.
- `AddA2ARetention`: with two public constructors, replace `services.TryAddSingleton<A2ARetentionService>()` by a factory, an internal `A2ARetentionService.FromServices(sp)` that calls the new constructor explicitly with `sp.GetServices<IA2AContextLifecycle>()` and resolves the other parameters as DI does today (`GetRequiredService` for required, `GetService` for the optional ones). Tests use the same factory. The `IHostedService` registration does not change.
- Note: `BatchesAsync` already catches per item, so a hook's exception would not stop the batch; the per-hook `try`/`catch` is there so it does not stop the later hooks for that context.

**Tests (`RetentionTests`):**
1. Two orphan contexts deleted: a recording hook sees both `ContextRecord`s (tenant, owner, id).
2. A context that `DeleteContextAsync` refuses (a task uses it again): no hook call.
3. The first hook throws: the second hook still runs for that context, and the next context is still deleted and reported.
4. No hook registered: the sweep behaves as before (existing tests stay green).
5. The shipped constructor still works (construct it directly in a test).

Commit: `feat(server): IA2AContextLifecycle, called when retention deletes a context`.

## Task 3: Package skeleton, rules and CI

**Files:** create `src/A2A.Extensions.AgentFramework/A2A.Extensions.AgentFramework.csproj`, `PublicAPI.Shipped.txt` (empty), `PublicAPI.Unshipped.txt`, `README.md`, `A2AAgentKeys.cs`; create `tests/A2A.Extensions.AgentFramework.Tests/` (csproj, `Support.cs`); edit `Directory.Packages.props`, `A2A.slnx`, `.github/workflows/ci.yml`, `.github/workflows/coverage.yml`, `tests/A2A.Tests/PackageRulesTests.cs`, `docs/site/guides/versioning.md`, `Directory.Build.props`.

- csproj: copy `A2A.Extensions.AI.csproj`. `RootNamespace` `A2A.Extensions.AgentFramework`; `DefineConstants` `$(DefineConstants);A2A_AGENT_FRAMEWORK`; `ProjectReference` `A2A.Extensions.AI`; `PackageReference` `Microsoft.Agents.AI.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`; link `..\Shared\AI\*.cs`. Description: "Microsoft Agent Framework integration for A2A (Agent2Agent): host an AIAgent as an A2A agent, or use a remote A2A agent as an AIAgent."
- `Directory.Packages.props`: `<PackageVersion Include="Microsoft.Agents.AI.Abstractions" Version="1.28.0" />`.
- **AOT check (spec open question 1).** Done: no warnings; see spec open question 1. Also found: `AgentSessionStore`, `AgentSessionStoreKey` (`MAAI001`) and the `ContinuationToken` properties (`MEAI001`) are experimental upstream (spec open question 5). Tasks 5, 6 and 8 add `MAAI001`/`MEAI001` to this csproj's `NoWarn` where they start using them, with a comment, never repo-wide. Original step: Build the package with `IsAotCompatible` on, as every package is (`Directory.Build.props`). If the build reports trim or AOT warnings that come from `Microsoft.Agents.AI.Abstractions` and not from our code, set `<IsAotCompatible>false</IsAotCompatible>` in this csproj only and write the reason, with one warning id, in its README. Record the outcome in the commit message.
- `A2AAgentKeys` (public static, experimental): `TaskId = "a2a.taskId"`, `ContextId = "a2a.contextId"`, `ReferenceTaskIds = "a2a.referenceTaskIds"`, `TaskState = "a2a.taskState"`. Check `docs/site/guides/metadata.md` for clashes first, and add the keys there in Task 9.
- `versioning.md`: an `A2AEXP006` row ("Agent Framework adapter", "Agent Framework hosting and sessions are preview upstream") and a `### A2AEXP006` section in the form of the others. `Directory.Build.props` line 16: add `A2AEXP006` to the repo's own `NoWarn`.
- `PackageRulesTests`: `AgentFramework_references_only_agent_framework_abstractions` with `AssertOnlyAbstractions("A2A.Extensions.AgentFramework", r => r == "Microsoft.Agents.AI.Abstractions" || IsExtensionsAbstraction(r))`. Then `AgentFramework_does_not_reference_the_official_A2A_stack`: no `PackageReference` named `A2A` or starting with `Microsoft.Agents.AI.A2A` or `Microsoft.Agents.AI.Hosting`. Add the assembly to `Every_public_type_lives_in_a_namespace_owned_by_its_assembly` (A2A.Tests then references the new project; if that pulls System.Text.Json 10 into A2A.Tests, which the Extensions.AI design avoided, drop this one line and put the same assertion in the new test project instead).
- Test project: copy `tests/A2A.Extensions.AI.Tests/A2A.Extensions.AI.Tests.csproj`, reference the new package and `A2A.Server.AspNetCore`. `Support.cs`: a `FakeAIAgent : AIAgent` that records each call (messages, session, run options) and plays a script of `AgentResponseUpdate`s; it implements the five abstract members (`RunCoreAsync`, `RunCoreStreamingAsync`, `CreateSessionCoreAsync`, `SerializeSessionCoreAsync`, `DeserializeSessionCoreAsync`) with a `FakeSession : AgentSession` that keeps its received messages in its `StateBag`. A `FakeSessionStore : AgentSessionStore` with a dictionary keyed by `AgentSessionStoreKey`, counting saves and deletes. Copy `Bindings` and the host helper pattern from `tests/A2A.Extensions.AI.Tests`.
- CI: in `ci.yml` add a `Test Extensions.AgentFramework (net10.0)` step after the Extensions.AI step, same form. In `coverage.yml` add `A2A.Extensions.AgentFramework.Tests` to the project loop. `release.yml` packs every `src/*/*.csproj` already; no change.
- `A2A.slnx`: the package next to `A2A.Extensions.AI`, the tests next to its tests.

**Tests:** one smoke test that `FakeAIAgent` runs, so the project is not empty. Run the two new `PackageRulesTests`.

Commit: `build: add A2A.Extensions.AgentFramework skeleton, rules and CI`.

## Task 4: `AIAgentExecutor`, stateless mode

**Files:** create `src/A2A.Extensions.AgentFramework/AIAgentExecutor.cs`, `AIAgentExecutorOptions.cs`, `AgentSessionMode.cs`, `ToolApprovalMode.cs`, `A2AAIAgentBuilderExtensions.cs`; tests `tests/A2A.Extensions.AgentFramework.Tests/ExecutorTests.cs`.

Options as the spec's table. Validation (in setters or a `Validate()` called at registration, matching `ChatClientAgentOptions`): `MaxHistoryMessages >= 1`, `StreamChunkSize >= 1`.

```csharp
public sealed class AIAgentExecutor : IAgentExecutor
{
    public AIAgentExecutor(AIAgent agent, AIAgentExecutorOptions? options = null, AgentSessionStore? sessions = null);
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken);
}

public static class A2AAIAgentBuilderExtensions
{
    public static A2AServerBuilder AddAIAgent(this A2AServerBuilder builder,
        Action<AIAgentExecutorOptions>? configure = null, object? serviceKey = null);
}
```

Flow (stateless only in this task; `SessionMode.Stored` and `ToolApprovals.Bound` throw `NotSupportedException` until Tasks 5 and 6):

1. URL parts refused when `AllowUrlParts` is false: `RejectAsync` with the same text as `ChatClientAgentExecutor` (`"URL parts are not accepted by this agent."`; move the constant to shared code in this task if it is not there yet).
2. `messages = await HistoryPrompt.BuildAsync(context, new(Instructions: null, …), ct)`.
3. `session = await agent.CreateSessionAsync(ct)`.
4. Run options: `new AgentRunOptions { AdditionalProperties = new() }` with `A2AAgentKeys.TaskId`, `A2AAgentKeys.ContextId`, and `A2AAgentKeys.ReferenceTaskIds` (a `string[]` copy of `context.Message.ReferenceTaskIds`) when that list is non-empty. Then `options.ConfigureRunOptions?.Invoke(runOptions, context)`. Never copy `context.Metadata` or `context.Message.Metadata`.
5. `StartWorkAsync`, then `await foreach (var update in agent.RunStreamingAsync(messages, session, runOptions, ct))`: `FinishReason == ChatFinishReason.ContentFilter` throws the same text as `ChatClientAgentExecutor`; each `update.Contents` item goes to `ArtifactChunker.AddAsync`. A `ToolApprovalRequestContent` sets a flag that is treated like an unresolved tool call (`ToolApprovals.Off`).
6. Unresolved tool call (or approval request) with no output: throw `InvalidOperationException("The agent asked for a tool call or an approval that nothing resolved.")`.
7. `FinishAsync`, then the same three endings as `ChatClientAgentExecutor` (`KeepConversationOpen`, `UseContextMemory`, plain `CompleteAsync`).

`AddAIAgent`: `builder.Services.Replace(ServiceDescriptor.Scoped<IAgentExecutor>(sp => new AIAgentExecutor(agent, options, sp.GetService<AgentSessionStore>())))`, with the agent from `GetRequiredService<AIAgent>()` or `GetRequiredKeyedService<AIAgent>(serviceKey)`, resolved from `sp`, which is the execution's scope. Validate options at registration.

Done in Task 4, with these differences: the options also have `ContextMemoryMaxTasks` and `ContextMemoryMaxBytes` (the shared prompt builder needs them); the URL and content-filter texts moved to `src/Shared/AI/ExecutorTexts.cs`; the package's csproj has `NoWarn` `MAAI001` because the constructor takes an `AgentSessionStore` (a consumer that does not pass one sees no `MAAI001`, checked); the tests run over the real server (`AgentHost`), as `tests/A2A.Extensions.AI.Tests/ExecutorTests.cs` does, not over a fake `TaskUpdater` sink.

**Tests (`ExecutorTests`):**
1. Same updates into `ChatClientAgentExecutor` (through a fake `IChatClient`) and `AIAgentExecutor` (through `FakeAIAgent`) give equal artifact chunks, the same `append` and `lastChunk` flags, and the same final state. Do this for text only, text then data, and an empty reply.
2. Prompt equals the Extensions.AI prompt for the same history, minus the system message; with `UseContextMemory` the summary is a user message.
3. A fresh session per execution (two executions, two `CreateSessionAsync` calls).
4. Run options carry task id and context id; `ReferenceTaskIds` present only when the message has them; caller metadata keys absent; `ConfigureRunOptions` sees the context and its changes reach the agent.
5. Content filter, `ErrorContent`, unresolved tool call, unresolved approval request: task fails with the generic server text (the real text is logged, not returned).
6. URL part with `AllowUrlParts = false`: rejected, agent not called.
7. Cancelling the execution token cancels the token the agent sees, and the task ends `CANCELED`.
8. `AddAIAgent`: a scoped dependency of the agent is a new instance per execution; keyed resolution works; bad options fail at registration.

Commit: `feat(agent-framework): host an AIAgent as an A2A agent`.

## Task 5: Stored sessions

**Files:** edit `AIAgentExecutor.cs`, `A2AAIAgentBuilderExtensions.cs`; create `AgentSessionContextLifecycle.cs` (internal); tests `SessionTests.cs`.

- Key: `new AgentSessionStoreKey(context.ContextId).WithPartition("a2a.tenant", "t:" + context.Call.Tenant).WithPartition("a2a.owner", "o:" + context.Call.Principal)`. Keep the prefixes: `WithPartition` throws for an empty value and the default tenant is `""`. Add a test with the default tenant. One internal static `SessionKeys.For(tenant, owner, contextId)` used here and in the lifecycle hook, so the two cannot drift.
- Run: `session = await store.GetOrCreateSessionAsync(agent, key, ct)`; messages = only `context.Message` converted with `ModelContent.ToModelContent` (URL rule applied); the rest as Task 4.
- Save in a `finally`: after success, failure and cancellation. Use `CancellationToken.None` for the save after a cancellation, so a canceled run still keeps its session; bound it with a short timeout (5 s, internal constant) so a hung store cannot hold the execution.
- `AddAIAgent` with `SessionMode.Stored`: at first resolution throw `InvalidOperationException("SessionMode.Stored needs an AgentSessionStore registered in DI.")` when none is registered. (A startup check, if the builder has a validation hook; look for how `A2AStartupDiagnostics` reports similar problems and use that if it fits.) Also register `IA2AContextLifecycle` → `AgentSessionContextLifecycle`, which calls `store.DeleteSessionAsync(agent, SessionKeys.For(record.Tenant, record.Owner, record.ContextId), ct)`. It needs the agent: resolve it from a new scope in the hook (`IServiceScopeFactory`), keyed the same way.
- XML docs and README: two tasks of one context at the same time race on the session; the last save wins.

Done in Task 5, with these differences:
- Save timing: on success the save comes **before** the final status, and a failed save fails the task, instead of a save in a `finally` after the ending (a save error after `CompleteAsync` would have nowhere to go). After a failure or a cancel the save is best effort with `CancellationToken.None`, logged as event 1800 (1400 at first; moved when main gave 1400 to the health check), and the run's own error is rethrown. A save that was already attempted (for example one that timed out) is not retried, so a hung store holds the execution for 5 s, not 10. The bound uses `WaitAsync`, so it holds also for a store that ignores its token.
- Startup check: an internal `IHostedService` (`StoredSessionCheck`), as `DefaultSchemeCheck` does for card authentication, plus a constructor check (`InvalidOperationException` with the same text).
- `AIAgentExecutor` takes an optional `ILogger<AIAgentExecutor>` (unshipped API, so changed in place). `AddAIAgent` records an internal `AIAgentRegistration` (mode, service key) that the hook and the check read, so a later `AddAIAgent` call wins for them as for the executor; both are registered once (`TryAddEnumerable`). In stateless mode the hook does nothing and no store is used even when one is registered.
- Two owners cannot share a context id through the server (it answers `context_denied`), so owner isolation is checked through the owner partition of each caller's key; the shared-id case is two tenants, with the contexts created in the store first (a client may only name a context that exists).

**Tests (`SessionTests`, `FakeSessionStore`):**
1. Second turn in the same context sends only the new message, and the session holds both turns.
2. Two owners with the same context id get two sessions; two tenants likewise.
3. Saved after success, after an agent exception, and after cancellation.
4. A store that hangs on save: the execution still ends within the timeout.
5. Stored without a store: the clear error.
6. Lifecycle: retention deleting a context calls `DeleteSessionAsync` with the same key the run used (use `RetentionTests`' setup with a short `OrphanContextRetention` and a fake clock).

Commit: `feat(agent-framework): stored sessions, deleted with their context`.

## Task 6: Bound tool approvals

**Files:** edit `AIAgentExecutor.cs`; create `ApprovalDescription.cs` (internal); tests `ApprovalTests.cs`.

- Startup: `ToolApprovals.Bound` with `SessionMode.Stateless` throws `InvalidOperationException` at registration, naming both options.
- During the run, the first `ToolApprovalRequestContent`: stop reading the stream (break out of the `await foreach`, which disposes the enumerator), flush the chunker without `lastChunk` only if it has output (otherwise emit nothing), store the request in `session.StateBag` under `"a2a.pendingApproval"`, save the session, then `updater.RequireApprovalAsync(new PendingApproval(request.ToolCall.CallId, description), description, ct)`. Do not complete the task.
- Before storing it, check how `AgentSessionStateBag.SetValue<T>` serializes. If it needs a `JsonSerializerOptions` with a type resolver for `ToolApprovalRequestContent`, use M.E.AI's `AIJsonUtilities.DefaultOptions`, which carries the M.E.AI content types (look it up in the 10.11.0 XML docs before relying on it). If that does not round-trip the content, store a small internal record `(CallId, Description, ToolCallJson)` instead and rebuild the request from it, then say so in a code comment.
- `ApprovalDescription.For(ToolCallContent call)`: for `FunctionCallContent`, `"{Name}({canonical JSON of Arguments})"` canonicalized by RFC 8785 with `A2A.Signing.JsonCanonicalizer.CanonicalizeToString` (public, in `A2A.Protocol`). Write the arguments dictionary to a `JsonNode` first with `Utf8JsonWriter`, not the reflection serializer. For any other `ToolCallContent`, its `GetType().Name` and `CallId`. A `CallId` with a line feed: fail the task (`PendingApproval` would throw anyway; fail with the generic text, log why).
- Follow-up turn, when the session holds a pending request:
  - `context.Approval == Approved`: input = one user `ChatMessage` with `request.CreateResponse(true, null)`; clear the bag entry; run.
  - `NotApproved`: `request.CreateResponse(false, "The caller did not approve this action.")`; clear; run.
  - `None`: the stored request is stale; clear it and handle the message as a new turn.
- Several approval requests in one stream: only the first is asked (the rest are never read, by the break above).

Done in Task 6, with these differences:
- **Security fix found while building it:** the stored entry is `PendingToolApproval(TaskId, Hash, Request)`, not the bare request. Without the task id, task A asking and then task B (same context) asking would let A's approval answer B's call. Without the hash check, a store that serves an older session copy would let a newer approval answer an earlier call of the same task. Both have tests that fail when the check is removed. `ApprovalState.None` is also covered by the hash check (no pending hash on the task) and is kept as an explicit early return.
- StateBag: `AIJsonUtilities.DefaultOptions` round-trips the request (probe on 1.28.0 / 10.11.0), so the request is stored as its M.E.AI JSON inside a small record serialized by an own source-generated context. No fallback was needed.
- The caller's message content follows the approval response in the same user message.
- `ArtifactChunker.FlushAsync` (shared) emits held-back output without `lastChunk`.
- `ToolApprovals = Bound` with `Stateless` throws `InvalidOperationException` from the same check as unknown modes, at registration and in the constructor.
- A streaming test must stop reading at `INPUT_REQUIRED`: the client resumes a stream that ends before a terminal state (streaming guide), so `await foreach` would never end.

**Tests (`ApprovalTests`):**
1. Request → task `INPUT_REQUIRED`; the status metadata hash equals `new PendingApproval(callId, description).Hash`; status text equals `description`.
2. Follow-up echoing the hash → the agent receives an approved response for that call id, and the task completes.
3. Follow-up without the hash → the agent receives a denied response with the fixed reason.
4. Approved hash, but the agent's tool call arguments differ on the next run (the fake replays a changed request): asked again, not run (the hash differs).
5. Stale request (`Approval == None`): cleared, the turn runs normally.
6. Argument key order does not change the hash (`{"b":1,"a":2}` and `{"a":2,"b":1}`).
7. `Bound` with `Stateless`: registration error.

Commit: `feat(agent-framework): tool approvals bound to the exact call`.

## Task 7: `A2ARemoteAIAgent` (client side)

**Files:** create `A2ARemoteAIAgent.cs`, `A2ARemoteAgentSession.cs`, `A2AAIAgentOptions.cs`, `A2AClientAgentExtensions.cs`; tests `RemoteAgentTests.cs`.

```csharp
public static class A2AClientAgentExtensions
{
    public static A2ARemoteAIAgent AsAIAgent(this A2AClient client, A2AAIAgentOptions? options = null);
}

public sealed class A2AAIAgentOptions : A2ARemoteAgentOptions
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
}
```

- Build on the existing facade: `A2ARemoteAIAgent` holds `client.AsChatClient(new A2AChatClientOptions { RequestOptions = …, OnArtifactReceived = … })` and maps through the public M.E.AI types. `AgentResponse(ChatResponse)` and `AgentResponseUpdate(ChatResponseUpdate)` constructors exist (`Microsoft.Agents.AI.Abstractions` 1.28.0 XML docs). This reuses the facade's send rules, turn-only content and failure handling without copying them.
- `Name`, `Description`, `Id` come only from options; `IdCore` override returns `options.Id` when set. Never from `client.AgentCard`. `GetService(typeof(AIAgentMetadata))` returns `new AIAgentMetadata("a2a")`; `GetService(typeof(AgentCard))` returns the client's card when known.
- `A2ARemoteAgentSession : AgentSession` with `ContextId` and `TaskId` (both nullable). `CreateSessionCoreAsync` returns an empty one. `SerializeSessionCoreAsync` writes `{"contextId":…,"taskId":…}` with `Utf8JsonWriter` (nulls omitted). `DeserializeSessionCoreAsync` reads exactly those two optional string properties; anything else (other properties, other value kinds) throws `JsonException`. A session of another type passed to `RunAsync` throws `ArgumentException`.
- Run: `ChatOptions { ConversationId = session.ContextId }`, and `AdditionalProperties[A2AChatKeys.TaskId] = session.TaskId` when set. After the response: `session.ContextId = response.ConversationId`; `session.TaskId` = the task id when `A2AChatKeys.TaskState` is input-required or auth-required, else null. Copy `A2AChatKeys.TaskState` to `AgentResponse.AdditionalProperties[A2AAgentKeys.TaskState]`.
- Streaming the same way over `GetStreamingResponseAsync`; update the session from the last update that carries the ids.
- Failed, rejected, canceled: the facade already throws `InvalidOperationException` with the state only; let it through.
- No session passed: use a throwaway one (each run a new conversation), as AF agents do when called without a session. Check the base `RunAsync` behaviour in the XML docs; if the base creates a session itself, rely on that.

Done in Task 7, with these notes:
- `AgentResponseUpdate` has no `ConversationId`, so callers read the ids from the session; the session is updated on every streaming update (not only at the end), so a caller that stops reading early still has them.
- The base `RunAsync` docs say a new session is made when none is passed but not where; `RunCoreAsync` makes a throwaway `A2ARemoteAgentSession` itself when given null.
- `AllowBackgroundResponses` is ignored until Task 8 (a run waits for the remote task); the README says so.
- `A2ARemoteAgentSession` has a public parameterless constructor (an `AgentSession` subclass) and internal setters.
- `FakeTransport` is copied into the test project from `tests/A2A.Extensions.AI.Tests/Support.cs` (test projects do not share code).
- Task 9's end-to-end case 1 (hosted `AIAgent` called with `AsAIAgent`, both bindings, unary and streaming) is already in `RemoteAgentTests`.

**Tests (`RemoteAgentTests`, a fake `IA2AClient`/transport as in `tests/A2A.Extensions.AI.Tests/ChatClientFacadeTests.cs`):**
1. Only trailing user messages are sent; system messages never.
2. Session round trip: serialize → deserialize gives the same ids; extra property or a number value is refused.
3. Remote `INPUT_REQUIRED`: the response holds the question, the session keeps `TaskId`, the next run continues that task; after completion `TaskId` is null.
4. Remote failure throws without the remote text.
5. `Name` and `Description` null when options leave them null, even with a card that has a name; set from options otherwise.
6. Streaming gives the same text as unary for the same remote answer.

Commit: `feat(agent-framework): use a remote A2A agent as an AIAgent`.

## Task 8: Background responses

**Files:** edit `A2ARemoteAIAgent.cs`; create `ContinuationTokens.cs` (internal); tests in `RemoteAgentTests.cs`.

This path does not go through the facade (it has no `ReturnImmediately`); use `A2AClient` directly and reuse `A2AContentExtensions`' public conversions and `RemoteAgentSupport`-equivalent rules (`RemoteAgentSupport` is internal: copy `StateName` and `NeedsWait` into `src/Shared/AI/RemoteStates.cs` and link it into both packages, as in Task 1).

- `runOptions.AllowBackgroundResponses == true` and no `ContinuationToken`: send with `SendMessageConfiguration.ReturnImmediately = true`. A terminal or interrupted task: map as Task 7. Otherwise return an `AgentResponse` with no messages and `ContinuationToken = ContinuationTokens.For(taskId)`: `ResponseContinuationToken.FromBytes` of UTF-8 `{"taskId":"…"}`.
- `runOptions.ContinuationToken` set: `ContinuationTokens.Read(token)` (exactly that shape, else `ArgumentException`); unary → `GetTaskAsync(taskId)`, mapped as above (a new token again while still running); streaming → `ObserveTaskAsync(taskId)`, mapped as the facade's stream rules. The request messages must be empty in this case; otherwise `ArgumentException` (check what AF documents for this; follow it if it says otherwise).
- `AllowBackgroundResponses` false: the token is never produced.

Done in Task 8, with these differences:
- Token shape: `{"taskId","artifacts":{id:parts},"status"}`, not only `taskId`. Needed for turn-only content (continuing a task in the background) and for resuming a stream after the last part the caller had (the client's event cursor is internal). Unknown properties, a missing task id or a negative count are refused.
- Shared code: `RemoteAgentSupport` moved to `src/Shared/AI` whole (not only `StateName`/`NeedsWait`), with the facade's `BuildMessage` and `ThrowIfFailed`, so both packages send and fail the same way. The facade's response and stream mapping stayed in `A2AChatClient` (it tracks whole artifacts; background reads need part counts); the adapter's `BackgroundRun` follows the same content rules, and a test compares the streamed text with the facade's on the same events.
- Streaming with `AllowBackgroundResponses` (no token) also goes through `BackgroundRun`, so its updates carry tokens, as the Agent Framework docs say for streamed background responses.
- The package's csproj adds `MEAI001` to its `NoWarn`.
- Agent Framework does not document messages passed with a token; they are refused (`ArgumentException`), as planned.

**Tests:**
1. Running remote task: a token comes back; a later run with it returns the result once complete.
2. Token round trip; a foreign token is refused.
3. Streaming with a token resumes and ends at the terminal state.
4. Without `AllowBackgroundResponses` no token appears even for a slow agent (the call waits, as today).

Commit: `feat(agent-framework): background responses with continuation tokens`.

## Task 9: End to end, sample, docs

**Files:** create `tests/A2A.Extensions.AgentFramework.Tests/EndToEndTests.cs`; create `samples/AgentFrameworkAgent/` (csproj, `Program.cs`); edit `samples/run-samples.sh`, `samples/README.md`, `A2A.slnx`; create `docs/site/guides/agent-framework.md`; edit `docs/site/guides/toc.yml`, `docs/site/guides/metadata.md`, `README.md`, `CHANGELOG.md`, `docs/2026-11-07-a2a-gap-analysis.md`.

Done in Task 9, with these notes:
- End-to-end case 1 is `RemoteAgentTests.A_hosted_agent_answers_a_remote_agent_over_unary_and_streaming` (Task 7); `EndToEndTests` has cases 2-4. Case 4 cancels through A2A and then polls the canceled task through the remote agent's continuation token.
- Sample: a small rule-based `AIAgent` written in the sample (no `Microsoft.Agents.AI` reference at all), with a comment that shows `new ChatClientAgent(chatClient, instructions:, name:)` for a real model (parameter names from the 1.28.0 XML docs). It opts in to `MEAI001` for its background response.
- Guide code comes from `samples/Snippets/AgentFramework.cs` regions (the guide checker refuses fenced C#), compiled with the other snippets; type names are inline code, not `xref` links.
- The guide also went into `docs/site/index.md` and `guides/overview.md`, which list every guide.
- The resume text says what the token is for: the A2A client already reconnects a dropped connection itself; the token serves a caller that stops reading (a restart) and continues later.
- Coverage baseline: `A2A.Extensions.AgentFramework` 82.5. The shared sources this assembly compiles but uses little of (`ModelContent`'s metadata conversions, `RemoteAgentSupport.LookupKnownAsync`) and generated code count against it; the package's own files are 73-100% covered (lowest: PendingToolApproval.cs, its unreadable-JSON branches).

**End to end (all `Bindings.All`, ASP.NET test host):**
1. `AddAIAgent` with `FakeAIAgent`, called with `AsAIAgent`: unary and streaming answers match.
2. Stored session over two turns from one `A2ARemoteAgentSession`.
3. Bound approval: the remote agent's `INPUT_REQUIRED` with the hash reaches the client; a raw follow-up with the hash in metadata (phase 2 will automate this on the client) completes the task.
4. A2A `CancelTask` during a run ends the task `CANCELED` and the fake saw cancellation.

**Sample `samples/AgentFrameworkAgent`:** self-contained like `samples/ChatAgent`. Hosts an `AIAgent` built over the existing `EchoChatClient`-style fake (no model key), and calls it with `AsAIAgent`. Prints `OK:` lines as the other samples do. Add `wanted agentframework && run_self_contained agentframework samples/AgentFrameworkAgent` to `run-samples.sh` and register the name where the script lists valid names. Do not reference `Microsoft.Agents.AI` (it is not a package dependency); if building a real `ChatClientAgent` needs it, the sample alone may reference it, and its README says the adapter does not.

**Docs:**
- `agent-framework.md`: what it is; that it does not mix with `Microsoft.Agents.AI.A2A` in one project, and why (type names); hosting (`AddAIAgent`, options table, stateless vs stored, the race note); approvals; the lifecycle hook; client (`AsAIAgent`, sessions, interrupted tasks, background responses); security (spec's summary); experimental `A2AEXP006`. Link the spec. Inline code only. `toc.yml`: after `Microsoft.Extensions.AI`.
- `metadata.md`: the `A2AAgentKeys` keys and where each appears (run options, response properties; never on the wire).
- README: a row in both package tables; "Host an AF `AIAgent`, or use an agent as an `AIAgent`" → `A2A.Extensions.AgentFramework`; add the guide to the guide list.
- CHANGELOG `Unreleased`: a `### Agent Framework` section (new package, experimental) and, under the server, `IA2AContextLifecycle`.
- Gap analysis open items: D33 row → "Done: `A2A.Extensions.AgentFramework` (scoped agent per execution, `ReferenceTaskIds` in run options, every execution is a task) and `IA2AContextLifecycle`; design: `docs/superpowers/specs/2026-11-12-agent-framework-adapter-design.md`." Leave D1, D2, D3.
- Run `docs/site/check-guides.py` if it applies to new guides.

Commit: `docs(agent-framework): guide, sample, end-to-end tests`.

## Done when

- All nine commits green in CI, 0 warnings, coverage baseline not lowered (`.github/coverage-baseline.json`: add the new test project's entry from the candidate file the coverage job writes).
- `dotnet pack` produces `RomanAmbinder.A2A.Extensions.AgentFramework` and its nuspec lists no `A2A` and no `Microsoft.Agents.AI.A2A*`/`Hosting*` dependency.
- Spec open questions 1 (AOT) and 2 (`CurrentRunContext`) have an answer written in the PR description; 3 and 4 stay open for review.
