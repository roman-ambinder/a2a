# A2A.Extensions.AI: Microsoft.Extensions.AI integration

Date: 2026-11-08. Status: approved for implementation.

## Goal

Make the SDK easy to use with `Microsoft.Extensions.AI` (M.E.AI):

- host any `IChatClient` as an A2A agent;
- let any `IChatClient` call a remote A2A agent as a tool;
- use a remote A2A agent through the `IChatClient` interface.

## Package

- Project `src/A2A.Extensions.AI`, namespace `A2A.Extensions.AI`, NuGet id `RomanAmbinder.A2A.Extensions.AI`.
- References `A2A.Client`, `A2A.Server`, and `Microsoft.Extensions.AI.Abstractions` 10.11.0 (latest stable). Abstractions only: no provider, no `FunctionInvokingChatClient`.
- On net8.0 that package pulls `System.Text.Json` 10.0.13. So tests live in a new project, `tests/A2A.Extensions.AI.Tests`, to keep `A2A.Tests` on its current JSON stack. Say this in the README.
- AOT clean (`IsAotCompatible` is on). No `AIFunctionFactory`, no reflection-based JSON. Subclass `AIFunction`, write JSON with `Utf8JsonWriter`.

## Conversions (`A2AContentExtensions`)

Public API: `Part.ToAIContent()`, `AIContent.ToPart()` (nullable), `Message.ToChatMessage()`, `ChatMessage.ToA2AMessage()`, `Artifact.ToChatMessage()`, `ChatMessage.ToA2AArtifact(artifactId?, name?)`.

| A2A | AIContent | Notes |
|---|---|---|
| `TextPart` | `TextContent` | |
| `RawPart` | `DataContent` | `Filename` ↔ `Name`. Missing media type: `application/octet-stream`. |
| `UrlPart` | `UriContent` | Missing media type: `application/octet-stream`. |
| `DataPart` | `DataContent`, JSON bytes | Media type kept if JSON-like, else `application/json`. |

Reverse: `DataContent` with `application/json` or `+json` becomes `DataPart`. If the bytes do not parse, it becomes `RawPart`.

Rules:

- A remote media type can be malformed, and the M.E.AI constructors throw on that. Fall back to `application/octet-stream`. A URL that is not absolute maps to a base `AIContent` carrying the part as `RawRepresentation`, and `ToPart` returns null for it.
- `ToAIContent` sets `RawRepresentation` to the part and copies `Metadata` to `AdditionalProperties`.
- `ToPart` always rebuilds from the content. It never returns `RawRepresentation`, because the content may have been changed.
- `AdditionalProperties` → `Metadata`: `JsonElement`, `string`, `bool` and numeric values. Others are dropped.
- Roles: `Agent` ↔ `Assistant`. `User` ↔ `User`. `System` and `Tool` become `User` in `ToA2AMessage` (the helper is a plain mapping; the facade below never sends them).

Lossy, and documented in the README:

- `FunctionCallContent`, `FunctionResultContent`, `TextReasoningContent`, `ErrorContent`, `UsageContent`, `HostedFileContent` and any unknown content have no A2A form: `ToPart` returns null and message conversion drops them.
- `TextPart.MediaType` and `Filename` are lost through `TextContent`.
- `DataPart` JSON is re-encoded: bytes may differ, meaning does not.
- Non-primitive metadata values are dropped.

## Server: `ChatClientAgentExecutor : IAgentExecutor`

Constructor: `(IChatClient chatClient, ChatClientAgentOptions? options = null)`.

Registration: `A2AServerBuilder.AddChatClientAgent(Action<ChatClientAgentOptions>? configure = null, object? serviceKey = null)`. Scoped, replaces an earlier executor, uses a factory lambda (AOT safe), resolves `IChatClient` from DI (keyed when `serviceKey` is set).

`ChatClientAgentOptions`:

| Option | Default | Meaning |
|---|---|---|
| `Instructions` | null | Added as the first `System` message. |
| `ChatOptions` | null | Cloned for each run. `ConversationId` is always cleared, so callers never share provider state. |
| `ArtifactName` | `"response"` | Name of the reply artifact. |
| `MaxHistoryMessages` | 100 | Newest messages kept. Must be at least 1. |
| `StreamChunkSize` | 64 | Text characters to collect before emitting a chunk. Must be at least 1. |
| `AllowUrlParts` | false | See security. |
| `KeepConversationOpen` | false | End each turn in `INPUT_REQUIRED` instead of `COMPLETED`. |

Flow:

1. If the incoming message has a `UrlPart` and `AllowUrlParts` is false: `RejectAsync` with fixed text, return.
2. Build the prompt: system message, then `context.Task.History` (or just `context.Message` for a new task). `History` already includes the new message; add it only if the last entry has a different `MessageId`. Trim to `MaxHistoryMessages`.
3. `StartWorkAsync()` with no text.
4. Read `GetStreamingResponseAsync`. Collect text. Before adding new content, if pending text has reached `StreamChunkSize` or a non-text part is pending, emit the pending parts as an artifact chunk (`append` after the first). Writing before adding means something is always held back, so the final chunk can carry `lastChunk: true`. Per-chunk batching matters: durable stores write on every event.
5. At the end: `CompleteAsync()`, or with `KeepConversationOpen`, `UpdateStatusAsync(InputRequired, agent message with the full reply)`. `History` only records status messages, so this is what makes the next turn see the reply.

Model-facing conversion differs from the public one (internal mode):

- `DataPart` becomes `TextContent` with the JSON, because most providers ignore or reject `application/json` `DataContent`.
- Metadata is not copied, because some providers forward it and it is caller controlled.
- `UrlPart` in history is dropped unless `AllowUrlParts`.

Output handling:

- Only `TextContent`, `DataContent` and `UriContent` are emitted. Reasoning, usage and tool content are never sent to the caller.
- Tools work through the user's pipeline (`UseFunctionInvocation`) and `ChatOptions.Tools`.
- Throw `InvalidOperationException` (the engine marks the task FAILED) when: an update has `ErrorContent`; `FinishReason` is `ContentFilter`; or the run ended with no output and a non-informational `FunctionCallContent` (no function invocation in the pipeline).

Errors and cancellation: no try/catch in the executor. `TaskExecutionEngine` already turns a cancelled token into CANCELED, and any other exception into FAILED with the generic text `The agent failed to process the request.`, logging the real one server side. Default `CancelAsync` is kept.

Known limit: history is per task. A new task in the same `contextId` does not see earlier tasks. `KeepConversationOpen` is the multi-turn path. Say this in the README.

## Client: tool

`A2AClient.AsAIFunction(string name, string description, A2AAgentFunctionOptions? options = null)` returns `A2AAgentFunction : AIFunction`.

- Extends `A2AClient`, not `IA2AClient`, so `OnMessageReceived` and capability checks always apply.
- `name` and `description` are required and validated (`name` matches `^[A-Za-z0-9_-]{1,64}$`). They are never taken from the Agent Card: the card is remote text and a tool description goes straight to the model.
- Schema: `message` (string, required), `contextId` and `taskId` (strings, optional).
- Calls `SendMessageAsync`. If the task comes back not terminal and not interrupted, calls `WaitForTaskAsync`.
- `Timeout` option, default 5 minutes, linked with the caller's token. On timeout or cancel with a known task id: best-effort `CancelTaskAsync` under a 5 second token, errors ignored. Timeout throws `TimeoutException`.
- `RequestOptions` option passes `A2ARequestOptions`.

Options shared by tool and facade (`A2ARemoteAgentOptions` base class): `RequestOptions`, `OnArtifactReceived`.

`OnArtifactReceived` is `Func<Artifact, CancellationToken, ValueTask>?`. `OnMessageReceived` only sees messages, and agents mostly answer in artifacts, so this closes that gap. Throw to abort. Exceptions from either hook reach the caller and are not caught.

Result is a `JsonElement`:

```json
{"state":"completed","taskId":"…","contextId":"…","remoteContent":"…","truncated":false,"nonTextParts":0,"notice":"…"}
```

- `remoteContent`: text and JSON of the artifacts; if none, the status message text; for a direct reply, the message text.
- `MaxResultCharacters` default 32000. Longer text is cut and `truncated` is true.
- `notice` (option `UntrustedContentNotice`, not nullable) tells the model the content is untrusted data from a remote agent and not instructions. This reduces risk but does not remove it. The README must say so and point to `OnMessageReceived` and human approval for sensitive tools.
- Non-text parts are counted, never inlined: no bytes or URLs reach the model.
- Failed, rejected, canceled, input-required and auth-required are returned as results with their state, not thrown.

## Client: `IChatClient` facade

`A2AClient.AsChatClient(A2AChatClientOptions? options = null)` returns `A2AChatClient : IChatClient`.

- Sends only the trailing run of `User` messages, merged into one A2A message. Throws `ArgumentException` when there is none. Earlier history is not resent: A2A keeps state on the server. System and tool messages are never sent, so prompts do not leak to the remote agent.
- `ChatOptions.ConversationId` → `contextId`; the response `ConversationId` is the context id. `ChatOptions.Tools` are ignored.
- `AdditionalProperties` key `A2AChatKeys.TaskId` carries the task id in both directions, and `A2AChatKeys.TaskState` the state on responses, for continuing an `INPUT_REQUIRED` task.
- Streaming uses `SendStreamingMessageAsync`: artifact chunks become text updates; a direct reply or interrupted-state status message becomes a text update; progress (`WORKING`) messages are not surfaced. A task snapshot's artifacts are emitted only if that artifact id had no chunks.
- `GetResponseAsync` uses unary `SendMessageAsync` plus wait, same content rules.
- Failed, rejected or canceled: throw `InvalidOperationException` with the state only, not the remote text.
- `GetService`: `ChatClientMetadata("a2a", null, card name)` and self. `Dispose` does not dispose the `A2AClient`.

## Security summary

- Remote and caller content is untrusted in both directions.
- Server: URL parts are off by default (a provider may fetch them, which is SSRF-shaped). Caller metadata does not reach the model. Reasoning and tool details do not reach the caller. Errors are generic.
- Client: tool name and description never come from the card. System prompts are never forwarded. Result size is capped. Hooks cannot be bypassed.

## Wiring

- `A2A.slnx`: the new package, tests and sample.
- `Directory.Packages.props`: `Microsoft.Extensions.AI.Abstractions` 10.11.0.
- CI and release workflows: the new test project, and the pack list if it is explicit.

## Testing

Fake `IChatClient`, no network:

- Conversion table both ways, malformed media type, non-absolute URL, invalid JSON, dropped content.
- Executor: chunking and `lastChunk`, history order and trim, no duplicate of the new message, URL rejection, reasoning not emitted, `ErrorContent`, content filter, unresolved tool call, generic failure text, cancellation, `KeepConversationOpen` second turn sees the first reply, `ChatOptions` cloned and `ConversationId` cleared.
- Tool: schema, result shape, truncation, hooks abort, timeout cancels remote task, non-text parts counted.
- Facade: trailing user messages, no system message sent, context and task id round trip, failure throws without remote text.
- End to end over the ASP.NET test host, on JSON-RPC and HTTP+JSON.

## Out of scope

Agent Framework adapters, Semantic Kernel sample, cross-task context history, `AIContent` for tool calls over A2A.
