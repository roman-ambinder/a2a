# A2A.Extensions.AgentFramework: Microsoft Agent Framework adapter

Date: 2026-11-12. Status: draft, for review.

## Goal

Make the SDK usable from Microsoft Agent Framework (AF) .NET, in both directions:

- host any AF `AIAgent` as an A2A agent on this SDK's server;
- use a remote A2A agent as an AF `AIAgent` (so it can sit in AF workflows, orchestrations and tool lists).

This closes gap analysis D33 ("AF hosting: `ReferenceTaskIds` unsupported, message-only mode cannot cancel/resume, scoped lifetimes ignored, context memory lifecycle unresolved") and the first item the Extensions.AI design left out of scope ("Agent Framework adapters").

## Why a separate adapter

AF already ships A2A packages, but they are built on the official A2A SDK, not this one:

- `Microsoft.Agents.AI.A2A` and `Microsoft.Agents.AI.Hosting.A2A` (both `1.28.0-preview.261106.1`) depend on the NuGet package `A2A` `1.0.0-preview4`.
- `Microsoft.Agents.AI.Hosting.A2A.A2AAgentHandler` takes the official SDK's `A2A.RequestContext`, `A2A.AgentEventQueue` and `A2A.TaskUpdater`. It cannot sit on this SDK's pipeline.
- The official `A2A` assembly defines `A2A.AgentCard`, `A2A.Message`, `A2A.Part`, `A2A.Artifact` and `A2A.AgentTask`. `A2A.Protocol` defines the same full names (`src/A2A.Protocol/Models/AgentCard.cs`, `Core.cs`, `Part.cs`, all `namespace A2A;`). A project that references both gets ambiguous types. So this adapter **must not reference `Microsoft.Agents.AI.A2A`, `Microsoft.Agents.AI.Hosting.A2A` or `A2A`**, and the README must say that the two stacks do not mix in one project.

A user of this SDK who wants AF therefore has no route today. The adapter gives them one, with this SDK's stores, limits, ownership, approval binding and push delivery.

## Facts this design relies on

Read from the package XML docs and assembly metadata on 2026-11-12 (`Microsoft.Agents.AI.Abstractions` 1.28.0, `Microsoft.Extensions.AI.Abstractions` 10.11.0).

| Fact | Source |
|---|---|
| `Microsoft.Agents.AI.Abstractions` 1.28.0 is a stable release. It targets net10.0, net9.0, net8.0, net472 and netstandard2.0, and depends on `Microsoft.Extensions.AI.Abstractions` 10.11.0, `System.Text.Json` 10.0.13, `Microsoft.Bcl.AsyncInterfaces` and `System.Threading.Tasks.Extensions`. | nuspec |
| `AIAgent` is abstract. Its abstract members are `RunCoreAsync`, `RunCoreStreamingAsync`, `CreateSessionCoreAsync`, `SerializeSessionCoreAsync`, `DeserializeSessionCoreAsync`. | metadata |
| `AIAgent` has `Id`, `Name`, `Description`, `GetService`, `CurrentRunContext`. Public `RunAsync`/`RunStreamingAsync` overloads take `string`, `ChatMessage`, `IEnumerable<ChatMessage>` or nothing, plus `AgentSession?`, `AgentRunOptions?`. | XML docs |
| `AgentSession` is abstract and has a `StateBag` (`AgentSessionStateBag`). | metadata, XML docs |
| `AgentRunOptions` has `AdditionalProperties`, `AllowBackgroundResponses`, `ContinuationToken`, `ResponseFormat`, `Clone()`. | XML docs |
| `AgentResponse` has `Messages`, `Text`, `ContinuationToken`, `FinishReason`, `Usage`, `ResponseId`, `AgentId`. `AgentResponseUpdate` has `Contents`, `Role`, `MessageId`, `ContinuationToken`, `FinishReason`. | XML docs |
| `AgentSessionStore` is abstract: `GetSessionAsync`, `SaveSessionAsync`, `DeleteSessionAsync` are abstract; `GetOrCreateSessionAsync` is not. `AgentSessionStoreKey(sessionId, partitions)`: "Every partition contributes to identity." | metadata, XML docs |
| `InMemoryAgentSessionStore` is in `Microsoft.Agents.AI.Hosting` (preview), not in Abstractions. | XML docs |
| `ToolApprovalRequestContent` (derives from `InputRequestContent`) has `ToolCall` (`ToolCallContent`), `CreateResponse(bool, string)`. `ToolApprovalResponseContent` has `Approved`, `Reason`, `ToolCall`. `FunctionCallContent` derives from `ToolCallContent`. | metadata, XML docs |
| `ResponseContinuationToken` has `FromBytes(ReadOnlyMemory<byte>)` and `ToBytes()`. | XML docs |

## Package

- Project `src/A2A.Extensions.AgentFramework`, namespace `A2A.Extensions.AgentFramework`, NuGet id `RomanAmbinder.A2A.Extensions.AgentFramework`. net10.0, like `A2A.Extensions.AI`.
- References `A2A.Extensions.AI` (for the content conversions and the chunker) and **only** `Microsoft.Agents.AI.Abstractions` 1.28.0 (stable). Not `Microsoft.Agents.AI` (it pulls Evaluation, VectorData, ML.Tokenizers and more), and none of the preview hosting packages.
- `Microsoft.Extensions.AI.Abstractions` stays at 10.11.0, the version AF 1.28.0 asks for. No change for `A2A.Extensions.AI`.
- New public types are `[Experimental("A2AEXP006")]` (next free id; 001 to 005 are taken) until AF's session and approval APIs settle.
- Tests in a new `tests/A2A.Extensions.AgentFramework.Tests`, same reason as the Extensions.AI tests (separate JSON stack).

## Server: `AIAgentExecutor : IAgentExecutor`

Constructor: `(AIAgent agent, AIAgentExecutorOptions? options = null, AgentSessionStore? sessions = null)`.

Registration: `A2AServerBuilder.AddAIAgent(Action<AIAgentExecutorOptions>? configure = null, object? serviceKey = null)`. Scoped, replaces an earlier executor, factory lambda (AOT safe). The `AIAgent` is resolved from the execution's DI scope (keyed when `serviceKey` is set), so scoped services the agent depends on are safe. This is the "scoped lifetimes ignored" part of D33. An `AgentSessionStore` is taken from DI when registered.

### Options (`AIAgentExecutorOptions`)

The first eight match `ChatClientAgentOptions`, with the same defaults and the same meaning.

| Option | Default | Meaning |
|---|---|---|
| `ArtifactName` | `"response"` | Name of the reply artifact. |
| `MaxHistoryMessages` | 100 | Stateless mode only: newest messages kept. At least 1. |
| `StreamChunkSize` | 64 | Text characters per artifact chunk. At least 1. |
| `AllowUrlParts` | false | As in Extensions.AI: URL parts are refused. |
| `KeepConversationOpen` | false | End each turn in `INPUT_REQUIRED`. |
| `UseContextMemory` | false | Stateless mode only: earlier tasks of the context in the prompt. |
| `ContextMemoryMaxTasks` | 20 | With `UseContextMemory`, as in `ChatClientAgentOptions` (added in Task 4: the shared prompt builder needs them). |
| `ContextMemoryMaxBytes` | 262144 | With `UseContextMemory`, as in `ChatClientAgentOptions`. |
| `SessionMode` | `Stateless` | `Stateless` or `Stored`. See below. |
| `ConfigureRunOptions` | null | `Action<AgentRunOptions, AgentRequestContext>`, called on a fresh `AgentRunOptions` for each run. |
| `ToolApprovals` | `Off` | `Off` or `Bound`. See below. |

`Instructions` and `ChatOptions` are not options here: they belong to the `AIAgent`.

### Sessions

The A2A task store is the source of truth for history. AF sessions are a second, optional copy.

- **`Stateless` (default).** Each execution creates a fresh session (`CreateSessionAsync`). The prompt is built from A2A history exactly as `ChatClientAgentExecutor.BuildPromptAsync` does (system message excluded, since instructions are the agent's): history, the new message if missing, trim, leading agent messages dropped, optional context memory as a user message. That builder moves to linked source in `src/Shared/AI/`, compiled into both packages (the repo forbids `InternalsVisibleTo` between shipped packages). Nothing outlives the execution, so nothing needs a lifecycle.
- **`Stored`.** Requires an `AgentSessionStore` (startup fails without one, naming the option). The key is `new AgentSessionStoreKey(contextId).WithPartition("a2a.tenant", "t:" + call.Tenant).WithPartition("a2a.owner", "o:" + call.Principal)`. The prefixes are needed: `WithPartition` throws `ArgumentException` ("Argument is whitespace") for an empty value, and this SDK's default tenant is `""` (found in Task 3 by running it). Owner and tenant are partitions, so one caller can never load another caller's session for the same context id. Only the new message is sent; the session holds the rest. The session is saved after every run, including failed and canceled runs, so an approval or an interrupted state can resume.
- Two tasks of one context that run at the same time in `Stored` mode race on the session: last save wins. This is documented, as `ConversationMemory.SetSummaryAsync` documents it. Serialising them would need a lock across instances; out of scope.

### Flow

1. URL parts refused when `AllowUrlParts` is false (same text as Extensions.AI).
2. Build the input (per session mode, above).
3. Run options: a new `AgentRunOptions`; `AdditionalProperties` get `A2AAgentKeys.TaskId`, `A2AAgentKeys.ContextId` and, when the message has them, `A2AAgentKeys.ReferenceTaskIds` (`string[]`, from `Message.ReferenceTaskIds`). This is the "`ReferenceTaskIds` unsupported" part of D33. Then `ConfigureRunOptions`. Caller metadata is **not** copied: it does not reach the model (Extensions.AI rule).
4. `StartWorkAsync()`, then `RunStreamingAsync(messages, session, runOptions, ct)`. Each `AgentResponseUpdate.Contents` goes through the same chunker as `ChatClientAgentExecutor.Run` (moved to the same linked source), so `lastChunk` and batching behave identically. `FinishReason == ContentFilter`, `ErrorContent`, and an unresolved tool call with no output throw `InvalidOperationException`; the server fails the task with a generic message and logs the real error.
5. `ToolApprovalRequestContent` in the output: see Tool approvals. With `ToolApprovals = Off` it is treated like an unresolved tool call.
6. End: `CompleteAsync`, or `INPUT_REQUIRED` with the reply as status message under `KeepConversationOpen`, or the reply in the completed status message under `UseContextMemory` (same as Extensions.AI).
7. Cancellation: the token passed to `RunStreamingAsync` is the execution's token, so A2A `CancelTask` stops the AF run and the task ends `CANCELED`. Because every execution is a task (never a message-only reply), cancel and resume always work. This is the "message-only mode cannot cancel/resume" part of D33.

### Tool approvals (`ToolApprovals = Bound`)

AF asks for approval with `ToolApprovalRequestContent`. This SDK already has approval binding against loopjacking (`TaskUpdater.RequireApprovalAsync(PendingApproval, text)`, `AgentRequestContext.Approval`). The adapter joins them:

- Requires `SessionMode = Stored`, because the pending request has to survive until the follow-up. Startup fails otherwise.
- On the first `ToolApprovalRequestContent`: stop reading the stream, save the session, and call `RequireApprovalAsync(new PendingApproval(operationId, description), text)`. `operationId` is `ToolCall.CallId`. `description` is a canonical text of the call: for `FunctionCallContent`, the function name and its arguments as RFC 8785 JSON (the SDK already has JCS). The same `description` is the status text the user sees, so the user approves exactly what is hashed.
- The request itself is kept in the session's `StateBag` under one adapter key, **with the task id and the hash** (added in Task 6). Sessions are per context and approvals per task, so another task in the context can replace the entry; the stored request is answered only when its task id is the follow-up's task and its hash is the one the task waits for. Otherwise it is stale: dropped, never answered. The hash check also covers a store that returns an older copy of the session. The request is stored as M.E.AI JSON (`AIJsonUtilities.DefaultOptions`, source generated), which round-trips `ToolApprovalRequestContent` with its `FunctionCallContent` (checked in Task 6; argument values come back as `JsonElement`, and the canonical description is the same for both).
- Follow-up with `Approval == Approved`: send `request.CreateResponse(true, null)` as a user message and run again. With `NotApproved`: send `CreateResponse(false, "The caller did not approve this action.")`, so the model can go on without the tool. With `None` and a pending request: the stored request is stale (cleared) and the message is handled as a new turn. The caller's own message content follows the response in the same user message, so a reason given with a "no" is not lost.
- Several approval requests in one run: only the first is asked; the rest are answered after it, one per turn. Batch approval needs one hash over several actions; out of scope.

### Context lifecycle hook (D33)

Today retention deletes orphan contexts (`A2ARetentionService.DeleteOrphanContextsAsync` → `TaskStore.DeleteContextAsync`), and nothing else learns of it. Add to `A2A.Server`:

```csharp
public interface IA2AContextLifecycle
{
    ValueTask OnContextDeletedAsync(ContextRecord context, CancellationToken cancellationToken);
}
```

- Retention calls every registered `IA2AContextLifecycle` after `DeleteContextAsync` returns true. Each call is wrapped on its own: an exception is logged and the sweep goes on with the next hook and the next context. (`BatchesAsync` already catches per item, so a throw would not stop the batch; but it would stop the later hooks for that context, so each hook is wrapped as well.) The context is already deleted, so a failed hook leaves an orphan session behind; that is logged, not retried.
- In `Stored` mode the adapter registers one that calls `AgentSessionStore.DeleteSessionAsync` with the key built from `ContextRecord.Tenant`, `ContextRecord.Owner` and `ContextRecord.ContextId`. Sessions then live as long as their context, and no longer.
- The hook is generic: any agent that keeps per-context state outside the task store can use it.

## Client: `AIAgent` over `A2AClient`

`A2AClient.AsAIAgent(A2AAIAgentOptions? options = null)` returns `A2ARemoteAIAgent : AIAgent`. The name is not `A2AAgent`, to avoid confusion with AF's own `Microsoft.Agents.AI.A2A.A2AAgent`.

`A2AAIAgentOptions : A2ARemoteAgentOptions` (so `RequestOptions` and `OnArtifactReceived` work as in Extensions.AI) adds `Name`, `Description` and `Id`.

- **Name and description never come from the card.** AF can put an agent's name and description into a model prompt (orchestration, agent-as-tool); a card is remote text. This is the Extensions.AI rule for tool names. The card name is available only through `GetService` metadata.
- **Session:** `A2ARemoteAgentSession : AgentSession` holds `ContextId` and, while the remote task is interrupted, `TaskId`. Serialized as `{"contextId":…,"taskId":…}`; `DeserializeSessionCoreAsync` refuses anything else.
- **Run:** same send rules as `A2AChatClient`: only the trailing run of user messages, merged into one A2A message; system and tool messages are never sent. The session's context id and task id are applied. The response holds this turn's content only (same `GetTask` rule as the facade).
- **States:** `COMPLETED` returns the reply. `INPUT_REQUIRED` and `AUTH_REQUIRED` return the status message text, keep `TaskId` in the session, and set `A2AAgentKeys.TaskState` in `AgentResponse.AdditionalProperties`, so the next run continues the task. `FAILED`, `REJECTED`, `CANCELED` throw `InvalidOperationException` with the state only, never the remote text.
- **Streaming:** `RunStreamingAsync` uses `SendStreamingMessageAsync` with the facade's content rules.
- **Background responses:** with `AllowBackgroundResponses = true` the send uses `ReturnImmediately = true`. A task that is not terminal yet returns a response whose `ContinuationToken` is `ResponseContinuationToken.FromBytes` of UTF-8 JSON `{"taskId":…,"artifacts":{"id":parts,…},"status":"messageId"}` (the last two optional; added in Task 8). A run with that token calls `GetTask` (unary) or `ObserveTaskAsync` (streaming) instead of sending. Agent Framework documents that a streamed update's token resumes "from the point of interruption"; the client cannot resume a subscription from an event cursor (it is internal), so the token records how many parts of each artifact the caller had, and a resumed stream skips them. The server appends chunk parts unmerged (`Parts.AddRange`), so the count is exact. Streamed runs with `AllowBackgroundResponses` carry a token on every update but the last, as the Agent Framework docs say.
- **Remote approvals (phase 2):** a remote status message carrying `A2AProtocol.ApprovalHashKey` becomes a `ToolApprovalRequestContent` so AF's own approval flow can answer it; approving echoes the hash in the next message's metadata. Left for phase 2 because the mapping of a remote description to a `ToolCallContent` needs its own review.

## Security summary

- Everything the Extensions.AI design says still holds: remote and caller content is untrusted; URL parts off by default; caller metadata and system prompts are not forwarded; reasoning and tool details do not reach the caller; errors are generic.
- Stored sessions are partitioned by tenant and owner.
- Approvals are hash-bound to the exact call the user saw.
- Card text never becomes an AF agent name or description.

## Wiring

- `A2A.slnx`: the new package, test project and a sample (`samples/AgentFrameworkAgent`).
- `Directory.Packages.props`: `Microsoft.Agents.AI.Abstractions` 1.28.0.
- `A2A.Server`: `IA2AContextLifecycle` and the retention call (PublicAPI.Unshipped). The shipped `A2ARetentionService` constructor keeps its signature (its `= null` defaults move to the new overload, as Roslyn's RS0027 rule asks); the new overload adds `lifecycles`.
- README package table, a guide page under `docs/site/guides`, CHANGELOG, and the D33 row of the gap analysis open items.

## Testing

Fake `AIAgent` subclass and fake `AgentSessionStore`, no network:

- Executor: chunking and `lastChunk` identical to `ChatClientAgentExecutor` for the same updates; stateless prompt equals the Extensions.AI prompt; `Stored` mode sends only the new message and saves after success, failure and cancel; partitions isolate two owners with the same context id; `ReferenceTaskIds`, task id and context id in `AdditionalProperties`; caller metadata absent; content filter, `ErrorContent` and unresolved tool call fail generically; A2A cancel cancels the run token.
- Approvals: hash equals `PendingApproval(callId, description).Hash`; approved, not approved and stale follow-ups; a changed argument after approval is refused; startup fails with `Bound` and `Stateless`.
- Lifecycle: retention calls the hook only for contexts it deleted; a throwing hook does not stop the sweep; the adapter deletes the right session key.
- Client agent: trailing user messages only; session round trip and refusal of a foreign JSON shape; interrupted task continues on the next run; failure throws without remote text; continuation token round trip; card name not used as `Name`.
- End to end over the ASP.NET test host on JSON-RPC and HTTP+JSON: an `AIAgent` hosted by `AddAIAgent`, called through `AsAIAgent`.

## Open questions

1. AOT and trimming: **answered in Task 3.** `Microsoft.Agents.AI.Abstractions` 1.28.0 carries `AssemblyMetadata("IsTrimmable", "True")` and `("IsAotCompatible", "True")`, and a native AOT publish of a probe that calls the APIs this design uses (subclassed `AIAgent` runs, sessions, `StateBag` with a source-generated context, `AgentSessionStoreKey`, `ResponseContinuationToken`, `ToolApprovalRequestContent.CreateResponse`, `AgentResponse(ChatResponse)`) gave 0 warnings and ran. The package stays `IsAotCompatible`.
2. `AIAgent.CurrentRunContext`: **answered in Task 9.** It is static, with a public getter and a protected setter, and "flows across async calls" (1.28.0 metadata and XML docs), so code outside an agent cannot set it and it cannot replace `AdditionalProperties`. It does carry them: during a run hosted with `AddAIAgent`, `AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties[A2AAgentKeys.TaskId]` is the A2A task id (test `Code_inside_the_run_reads_the_A2A_ids_through_the_current_run_context`), so a tool called during the run can read the A2A ids that way.
3. Should `ToolApprovals = Bound` be the default when an `AgentSessionStore` is registered? Off by default is the safer start.
4. AF hosting is still preview, and its session API may change before 1.0 of `Microsoft.Agents.AI.Hosting`. This package depends only on the stable Abstractions to limit that, and is experimental until reviewed against the next AF release.
5. **Upstream experimental APIs (found in Task 3).** `AgentSessionStore` and `AgentSessionStoreKey` are `[Experimental("MAAI001")]`, and `AgentRunOptions.ContinuationToken` and `AgentResponse.ContinuationToken` are `[Experimental("MEAI001")]`. `Stored` sessions, the context lifecycle hook and background responses therefore rest on evaluation-only upstream APIs, and `AIAgentExecutor`'s constructor exposes `AgentSessionStore`, so its callers see `MAAI001` too. Plain stateless hosting and the client agent without background responses do not. The adapter stays experimental (`A2AEXP006`) at least until these are stable upstream.

## Out of scope

AF workflows hosted as one A2A agent, `Microsoft.Agents.AI.Hosting` integration (its `InMemoryAgentSessionStore` works if the user adds it), batch approvals, a cross-instance lock for `Stored` sessions, remote approvals (phase 2), and A2A 1.1 live input (`AgentRequestContext.LiveInput`) mapped to AF.
