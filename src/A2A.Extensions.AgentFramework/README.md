# A2A.Extensions.AgentFramework

Microsoft Agent Framework support for A2A. NuGet ID: `RomanAmbinder.A2A.Extensions.AgentFramework`.

**Status: in development, experimental (`A2AEXP006`).** Hosting an `AIAgent` works in stateless mode. Stored
sessions, tool approvals and the client side (a remote A2A agent as an `AIAgent`) are not done yet. The design is in
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
| `SessionMode`           | `Stateless` | `Stored` is not available yet (throws `NotSupportedException`)  |
| `ToolApprovals`         | `Off`       | `Bound` is not available yet (throws `NotSupportedException`)   |
| `ConfigureRunOptions`   | none        | Called on each run's `AgentRunOptions`                          |

The agent's instructions and chat options are the agent's own, so they are not options here. A content filter stop, error
content, or a tool call or approval request that nothing resolved fails the task with a generic message; the real error is
logged. With `ToolApprovals = Off`, an approval request next to other output is ignored.

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
