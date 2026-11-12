# A2A.Extensions.AgentFramework

Microsoft Agent Framework support for A2A. NuGet ID: `RomanAmbinder.A2A.Extensions.AgentFramework`.

**Status: in development, experimental (`A2AEXP006`).** Hosting an `AIAgent` works (stateless, with stored sessions,
with bound tool approvals), and so does using a remote A2A agent as an `AIAgent`, with background responses. The design is in
[docs/superpowers/specs/2026-11-12-agent-framework-adapter-design.md](../../docs/superpowers/specs/2026-11-12-agent-framework-adapter-design.md).

## Host an AIAgent as an A2A agent

```csharp
builder.Services.AddSingleton<AIAgent>(myAgent);   // or AddScoped: resolved from each execution's scope
builder.Services.AddA2A(a => a
    .AddAgentCard(card)
    .AddAIAgent(o => o.StreamChunkSize = 64));
```

The reply streams to the caller as chunks of one artifact named `response`, exactly as `AddChatClientAgent` streams a chat
client's reply. Every execution is an A2A task, so cancel and follow-ups work. Each execution gets a new session, and the
prompt is built from the A2A task history. Pass a `serviceKey` to use a keyed `AIAgent`.

The agent gets the A2A task id, context id and the message's reference task ids in `AgentRunOptions.AdditionalProperties`
(keys in `A2AAgentKeys`). Caller metadata is never passed on. Use `ConfigureRunOptions` to set more, from data you trust.

Options (`AIAgentExecutorOptions`), with the same meaning as in `ChatClientAgentOptions` where the name is the same:

| Option                  | Default     | Meaning                                                         |
|-------------------------|-------------|-----------------------------------------------------------------|
| `ArtifactName`          | `response`  | Name of the reply artifact                                      |
| `MaxHistoryMessages`    | 100         | Newest history messages sent to the agent (at least 1)          |
| `StreamChunkSize`       | 64          | Text characters collected before a chunk is sent (at least 1)   |
| `AllowUrlParts`         | false       | Off: a message with a URL part is rejected                      |
| `KeepConversationOpen`  | false       | End each turn in `INPUT_REQUIRED` instead of `COMPLETED`        |
| `UseContextMemory`      | false       | Earlier tasks of the context and its summary, as user messages  |
| `ContextMemoryMaxTasks` | 20          | With `UseContextMemory`: earlier tasks to read                  |
| `ContextMemoryMaxBytes` | 262144      | With `UseContextMemory`: size limit of earlier messages         |
| `SessionMode`           | `Stateless` | `Stored`: keep sessions in an `AgentSessionStore` (below)       |
| `ToolApprovals`         | `Off`       | `Bound`: ask the caller to confirm each tool call (below)       |
| `ConfigureRunOptions`   | none        | Called on each run's `AgentRunOptions`                          |

The agent's instructions and chat options are the agent's own, so they are not options here. A content filter stop, error
content, or a tool call or approval request that nothing resolved fails the task with a generic message; the real error is
logged. With `ToolApprovals = Off`, an approval request next to other output is ignored.

## Stored sessions

```csharp
builder.Services.AddSingleton<AgentSessionStore>(myStore);   // any Agent Framework session store
builder.Services.AddA2A(a => a
    .AddAgentCard(card)
    .AddAIAgent(o => o.SessionMode = AgentSessionMode.Stored));
// on the A2A builder: .AddRetention() deletes a context's session with the context
```

- One session per context and caller: the key is the context id, with the tenant and the owner as partitions, so one caller
  never loads another caller's session for the same context id.
- Only the new message is sent; the session holds the conversation. `MaxHistoryMessages` and `UseContextMemory` do not shape
  the prompt in this mode.
- The session is saved after each run. When the run succeeds, the save comes before the task's final status, and a failed
  save fails the task (the turn is not lost quietly). After a failure or a cancel the save is best effort and logged
  (event 1800, see Logs). A save may take at most 5 seconds, so a store that hangs cannot hold the execution.
- Two tasks of one context that run at the same time race on the session: the last save wins.
- The host refuses to start in this mode without an `AgentSessionStore`.
- When the retention sweep deletes a context, the context's session is deleted from the store too.
- `AgentSessionStore` is evaluation-only upstream (`MAAI001`): code that registers one opts in to that ID.

## Tool approvals

```csharp
.AddAIAgent(o =>
{
    o.SessionMode = AgentSessionMode.Stored;   // needed: the request waits in the session
    o.ToolApprovals = ToolApprovalMode.Bound;
})
```

When the agent asks to approve a tool call (it streams a `ToolApprovalRequestContent`):

- The run stops. What the agent said before stays with the task. The task waits in `INPUT_REQUIRED` with the call's
  canonical description as its status text, for example `delete_file({"n":2,"path":"/x"})` (arguments as RFC 8785 JSON),
  and a hash of it in the status metadata (`a2a.approval.hash`).
- A follow-up that echoes the hash in its metadata approves that call: the agent gets an approval response and runs again.
  A follow-up without it declines, with the reason "The caller did not approve this action.". A wrong hash is refused by the
  server before the agent runs. The caller's own words follow the response.
- If the agent then asks for a different call, the caller is asked again: an approval answers one exact call.
- Only the first request of a run is asked.
- The request is answered only for the task that asked and the hash it showed. A new task in the context, or an older copy
  of the session from the store, drops the request without answering it.

## Use a remote A2A agent as an AIAgent

```csharp
var client = await A2AClient.ConnectAsync("https://agent.example.com/");
AIAgent weather = client.AsAIAgent(new A2AAIAgentOptions { Name = "weather", Description = "Forecasts" });

var session = await weather.CreateSessionAsync();
var reply = await weather.RunAsync("Weather in Paris?", session);
```

- It sends through `A2AChatClient` (`A2A.Extensions.AI`), with the same rules: only the trailing run of user messages is
  sent, merged into one A2A message; system and tool messages are never sent; a response holds this turn's content only.
- `A2ARemoteAgentSession` keeps the remote context id, and the task id while the remote task waits for input
  (`input-required`, `auth-required`). The next run with the same session continues the conversation, or that task. It
  serializes to `{"contextId":…,"taskId":…}` and refuses any other JSON; its `StateBag` is not kept. Without a session each
  run is a new conversation.
- Responses and updates carry `A2AAgentKeys.TaskId` and `A2AAgentKeys.TaskState` in `AdditionalProperties`.
- A failed, rejected or canceled remote task throws `InvalidOperationException` with the state only, not the remote text.
- `Name`, `Description` and `Id` come only from `A2AAIAgentOptions`, never from the remote card: Agent Framework can put
  them in front of a model, and a card is remote text. `GetService<AgentCard>()` returns the card when the client knows it.
- The name is not `A2AAgent`, so it is not confused with Agent Framework's own `Microsoft.Agents.AI.A2A.A2AAgent`.

### Background responses

```csharp
var options = new AgentRunOptions { AllowBackgroundResponses = true };
var response = await weather.RunAsync("Long forecast", session, options);
while (response.ContinuationToken is { } token)
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    response = await weather.RunAsync([], session, new AgentRunOptions { ContinuationToken = token });
}
```

- With `AllowBackgroundResponses` the message is sent without waiting (`ReturnImmediately`). While the remote task runs,
  the response has no messages and a `ContinuationToken`. Pass it back, with no messages, to poll: the agent reads the
  task (`GetTask`) and never sends again. A finished, interrupted or failed task answers as without background responses,
  and the token is then null.
- A streamed run gives a token on every update but the last. The A2A client reconnects a dropped connection by itself;
  the token is for a caller that stops reading (for example a restart). Pass the latest token to `RunStreamingAsync` (no
  messages) to continue: the agent follows the task (`ObserveTaskAsync`) and continues after the last artifact part the
  caller had, so nothing is repeated.
- The token is JSON: the remote task id, how many parts of each artifact the caller had, and the status message it had. It
  is not a secret: it names a task the caller can read anyway. A token from anywhere else is refused (`ArgumentException`).
- `AgentRunOptions.ContinuationToken` and `AgentResponse.ContinuationToken` are evaluation-only upstream (`MEAI001`).

## Logs

| Id | Level | When |
|---|---|---|
| 1800 | Warning | A session save after a failed or canceled run failed. |
| 1801 | Warning | Deleting the session of a deleted context failed. |

## Do not mix with Microsoft's A2A packages

This package builds on the A2A packages of this SDK (`RomanAmbinder.A2A.*`). Microsoft's `Microsoft.Agents.AI.A2A` and
`Microsoft.Agents.AI.Hosting.A2A` build on the NuGet package `A2A`, which defines `A2A.AgentCard`, `A2A.Message`,
`A2A.Part`, `A2A.Artifact` and `A2A.AgentTask`: the same full type names as `A2A.Protocol`. A project that references
both gets ambiguous types. Use one stack or the other in a project.

The package depends only on `Microsoft.Agents.AI.Abstractions` from Agent Framework (a stable release), not on
`Microsoft.Agents.AI` or the preview hosting packages.

## Native AOT

The package is AOT compatible. `Microsoft.Agents.AI.Abstractions` 1.28.0 declares itself trimmable and AOT compatible,
and a native AOT publish that calls the Agent Framework APIs this package uses (agent runs, sessions and their state bag,
the session store key, continuation tokens, tool approvals) gave no trim or AOT warnings.

## Experimental upstream APIs

Agent Framework marks some APIs this package will use as evaluation-only: the session store (`AgentSessionStore`,
`AgentSessionStoreKey`, `MAAI001`) and continuation tokens (`AgentRunOptions.ContinuationToken`,
`AgentResponse.ContinuationToken`, `MEAI001`). Code that uses stored sessions or background responses must opt in to
those IDs as well as `A2AEXP006`.
