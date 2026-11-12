# Microsoft Agent Framework

The `RomanAmbinder.A2A.Extensions.AgentFramework` package connects A2A to Microsoft Agent Framework. You can host any Agent Framework `AIAgent` as an A2A agent, and use a remote A2A agent as an `AIAgent`, so it can take part in Agent Framework orchestrations and workflows. Read this guide if you build agents on Agent Framework.

The package is experimental (`A2AEXP006`, see [versioning](versioning.md#experimental-apis)). It depends on `Microsoft.Agents.AI.Abstractions` only, a stable Agent Framework release, and on [`A2A.Extensions.AI`](extensions-ai.md). It is AOT compatible.

| You want to | Use |
|---|---|
| Serve an `AIAgent` as an A2A agent | `AddAIAgent` |
| Keep Agent Framework sessions between turns | `SessionMode = AgentSessionMode.Stored` |
| Let the caller confirm each tool call | `ToolApprovals = ToolApprovalMode.Bound` |
| Use a remote A2A agent as an `AIAgent` | `AsAIAgent` |

## Do not mix with Microsoft's A2A packages

Agent Framework has its own A2A packages, `Microsoft.Agents.AI.A2A` and `Microsoft.Agents.AI.Hosting.A2A`. They build on the NuGet package `A2A`, the official A2A .NET SDK. That package defines `A2A.AgentCard`, `A2A.Message`, `A2A.Part`, `A2A.Artifact` and `A2A.AgentTask`: the same full type names as this SDK's `A2A.Protocol`. A project that references both stacks gets ambiguous types. Use one stack or the other in a project. This package never references the official ones; a package rule test checks that.

## Host an agent

Register an `AIAgent`, then call `AddAIAgent` on the A2A builder:

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#Host)]

The reply streams to the caller as chunks of one artifact named `response`, exactly as `AddChatClientAgent` streams a chat client's reply (the two share the code). Every execution is an A2A task, so cancel and follow-ups always work: cancelling the task cancels the token the agent runs with, and the task ends `CANCELED`.

`AddAIAgent` registers a scoped executor and replaces any executor registered before. The agent is resolved from each execution's DI scope, so it can depend on scoped services. Pass `serviceKey` for a keyed agent.

The agent gets the A2A task id, the context id and the message's reference task ids in `AgentRunOptions.AdditionalProperties` (keys in `A2AAgentKeys`). Caller metadata is never copied there, because an agent may put those properties in front of a model. Code that runs during the run, such as a tool, reads them through `AIAgent.CurrentRunContext?.RunOptions?.AdditionalProperties`. Set more with `ConfigureRunOptions`, from data you trust:

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#HostScoped)]

A content filter stop, error content, or a tool call or approval request that nothing resolved fails the task with a generic message, and the server logs the real error. Reasoning, usage and tool details are never sent to the caller.

### Options

`AIAgentExecutorOptions` has these settings. The ones with the same name as in `ChatClientAgentOptions` mean the same. Every number throws `ArgumentOutOfRangeException` when set below 1. The agent's instructions and chat options belong to the agent, so they are not options here.

| Option | Default | Meaning |
|---|---|---|
| `ArtifactName` | `response` | Name of the reply artifact |
| `MaxHistoryMessages` | 100 | Stateless mode: how many of the newest task messages go to the agent |
| `StreamChunkSize` | 64 | Text characters to collect before a chunk goes to the caller |
| `AllowUrlParts` | `false` | A message with a URL part is rejected; the agent or its provider could fetch the URL |
| `KeepConversationOpen` | `false` | End each turn in `INPUT_REQUIRED` instead of `COMPLETED` |
| `UseContextMemory` | `false` | Stateless mode: give the agent the earlier tasks of the same context. See [Conversation memory](conversation-memory.md) |
| `ContextMemoryMaxTasks` | 20 | With `UseContextMemory`: how many earlier tasks to read |
| `ContextMemoryMaxBytes` | 262144 | With `UseContextMemory`: size limit for earlier messages |
| `SessionMode` | `Stateless` | Where the conversation is kept between turns (below) |
| `ToolApprovals` | `Off` | `Bound`: the caller confirms each tool call (below) |
| `ConfigureRunOptions` | none | Called on each run's `AgentRunOptions` |

### Stateless and stored sessions

In `Stateless` mode (the default) each execution gets a new Agent Framework session, and the prompt is built from the A2A task history, as for a chat client. Nothing outlives the execution.

In `Stored` mode the session of each context and caller is kept in an `AgentSessionStore` you register, and only the new message is sent:

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#HostStored)]

- The key is the context id, with the tenant and the owner as partitions: one caller never loads another caller's session for the same context id.
- The session is saved after each run. When the run succeeds, the save comes before the task's final status, and a failed save fails the task, so a turn is never lost quietly. After a failure or a cancel the save is best effort and logged (event 1800). A save may take at most 5 seconds, so a store that hangs cannot hold the execution.
- Two tasks of one context that run at the same time race on the session: the last save wins.
- The host refuses to start in this mode without an `AgentSessionStore`.
- `MaxHistoryMessages` and `UseContextMemory` do not shape the prompt in this mode: the session does.
- `AgentSessionStore` is evaluation-only in Agent Framework (`MAAI001`), so code that registers one opts in to that ID.

### Deleted with the context

When the [retention](retention.md) sweep deletes a context, it tells every registered `IA2AContextLifecycle` (package `A2A.Server`). In stored mode the adapter registers one that deletes the context's session, so sessions live as long as their context. A failed delete is logged (event 1801). The hook is general: any agent that keeps per-context state outside the task store can use it.

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#Lifecycle)]

### Tool approvals

With `ToolApprovals = ToolApprovalMode.Bound` (stored mode only), a tool approval request from the agent becomes an A2A approval bound to the exact call (see [Task safety](task-safety.md)):

- The run stops. What the agent said before stays with the task. The task waits in `INPUT_REQUIRED` with the call's canonical description as its status text, for example `delete_file({"path":"/tmp/x"})` (the arguments as RFC 8785 JSON, so key order does not matter), and its hash in the status metadata (`a2a.approval.hash`).
- A follow-up that echoes the hash approves that call: the agent gets an approval response and runs again. A follow-up without it declines, with the reason "The caller did not approve this action.". A wrong hash is refused before the agent runs. The caller's own words follow the response.
- If the agent then asks for a different call, the caller is asked again: an approval answers one exact call. Only the first request of a run is asked.
- The pending request is kept in the session with the task id and the hash. It is answered only for the task that asked and the hash that task shows. A new task in the context, or an older copy of the session from the store, drops it unanswered.

## Use a remote agent as an AIAgent

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#Remote)]

`AsAIAgent` returns an `A2ARemoteAIAgent`. It sends through `A2AChatClient` from [`A2A.Extensions.AI`](extensions-ai.md), with the same rules: only the trailing run of user messages is sent, merged into one A2A message; system and tool messages are never sent; a response holds this turn's content only. A failed, rejected or canceled remote task throws `InvalidOperationException` with the state only, not the remote text.

- **Sessions.** `A2ARemoteAgentSession` keeps the remote context id, and the task id while the remote task waits for input (`input-required` or `auth-required`). The next run with the same session continues the conversation, or that task. It serializes to `{"contextId":…,"taskId":…}` and refuses any other JSON. Without a session each run is a new conversation.
- **Ids.** Responses and updates carry `A2AAgentKeys.TaskId` and `A2AAgentKeys.TaskState` in `AdditionalProperties`.
- **Names.** `Name`, `Description` and `Id` come only from `A2AAIAgentOptions`, never from the remote card: Agent Framework can put them in front of a model, and a card is remote text. `GetService<AgentCard>()` returns the card when the client knows it.

The type is not called `A2AAgent`, so it is not confused with Agent Framework's own `Microsoft.Agents.AI.A2A.A2AAgent`.

### Background responses

With `AgentRunOptions.AllowBackgroundResponses` the message is sent without waiting. While the remote task runs, the response has no messages and a `ContinuationToken`. Pass it back, with no messages, to poll; the agent reads the task and never sends again:

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#Background)]

A streamed run gives a token on every update but the last. The A2A client already reconnects a dropped connection by itself. The token is for a caller that stops reading, for example because its process restarts: keep the latest token, and pass it later to continue. The agent follows the task and continues after the last artifact part it delivered, so nothing is repeated.

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#Resume)]

[!code-csharp[](../../../samples/Snippets/AgentFramework.cs#ResumeLater)]

The token is JSON: the task id, how many parts of each artifact the caller had, and the status message it had. It names a task the caller can read anyway; it is not a secret. A token from anywhere else is refused with `ArgumentException`. The continuation token properties are evaluation-only in Agent Framework (`MEAI001`).

## Security

- Caller and remote content is untrusted in both directions. URL parts are off by default. Caller metadata does not reach the agent. Reasoning and tool details do not reach the caller. Errors are generic.
- Stored sessions are partitioned by tenant and owner.
- Approvals are bound by hash to the exact call the caller saw, and to the task that asked.
- Card text never becomes an agent name or description.

## Sample

[samples/AgentFrameworkAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/AgentFrameworkAgent/Program.cs) hosts a small rule-based `AIAgent` and calls it with `AsAIAgent`: a session, a follow-up, streaming and a background response. Run it with `samples/run-samples.sh agentframework`.

The design is in [the adapter design](https://github.com/roman-ambinder/a2a/blob/main/docs/superpowers/specs/2026-11-12-agent-framework-adapter-design.md).
