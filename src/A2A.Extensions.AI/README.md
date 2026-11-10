# A2A.Extensions.AI

`Microsoft.Extensions.AI` (M.E.AI) support for A2A. NuGet ID: `RomanAmbinder.A2A.Extensions.AI`.

It does three things:

- Host any `IChatClient` as an A2A agent.
- Let any `IChatClient` call a remote A2A agent as a tool.
- Use a remote A2A agent through the `IChatClient` interface.

The package uses the M.E.AI abstractions only. It has no model provider and no `FunctionInvokingChatClient`. It is AOT
compatible.

## Host a chat client as an agent

```csharp
builder.Services.AddSingleton<IChatClient>(myChatClient);   // any provider
builder.Services.AddA2A(a => a
    .AddAgentCard(card)
    .AddChatClientAgent(o => o.Instructions = "You are a helpful agent."));
```

The reply streams to the caller as chunks of one artifact named `response`. Pass a `serviceKey` to use a keyed
`IChatClient`.

Options (`ChatClientAgentOptions`):

| Option                 | Default    | Meaning                                                       |
|------------------------|------------|---------------------------------------------------------------|
| `Instructions`         | none       | First system message of every run                             |
| `ChatOptions`          | none       | Cloned for each run. `ConversationId` is always cleared       |
| `ArtifactName`         | `response` | Name of the reply artifact                                    |
| `MaxHistoryMessages`   | 100        | Newest history messages sent to the model (at least 1)        |
| `StreamChunkSize`      | 64         | Text characters collected before a chunk is sent (at least 1) |
| `AllowUrlParts`        | false      | See Security                                                  |
| `KeepConversationOpen` | false      | End each turn in `INPUT_REQUIRED` instead of `COMPLETED`      |

Tools work through your own pipeline: use `UseFunctionInvocation` or `ChatOptions.Tools`. Reasoning, usage and tool
details are never sent to the caller.

A model error, a content filter stop, or a tool call that nothing ran fails the task. The caller sees the fixed text
`The agent failed to process the request.` The real error is logged on the server.

### History limit and `KeepConversationOpen`

History is per task. A new task in the same `contextId` does not see earlier tasks. The model gets at most the newest
`MaxHistoryMessages` messages of the task (100 by default). If the trim would leave an agent reply first, the prompt
starts at the next user message. Callers who read the task back get at most `A2ALimits.MaxHistoryLengthReturned`
messages (also 100 by default).

For a multi-turn chat, set `KeepConversationOpen = true`. Each turn then ends in `INPUT_REQUIRED` and the full reply is
stored as a status message. The caller sends the next message with the same `taskId`, and the model sees the earlier
turns.

## Call a remote agent as a tool

```csharp
var tool = client.AsAIFunction("ask_billing", "Asks the billing agent about invoices.");
var options = new ChatOptions { Tools = [tool] };
```

`name` and `description` are required. They are never taken from the Agent Card, because the card is remote text and a
tool description goes straight to the model.

The tool takes `message` (required), `contextId` and `taskId` (optional). It returns a JSON object:

```json
{"state":"completed","taskId":"...","contextId":"...","remoteContent":"...","truncated":false,"nonTextParts":0,"notice":"..."}
```

- `remoteContent` depends on the state. For `input-required` and `auth-required` it is the status message text (the
  agent's question), even when artifacts exist. For other states it is the text and JSON of the artifacts, or the status
  message text if there are none. For a direct reply, it is the message text.
- When you continue a task with `taskId`, only artifacts that are new in this turn count, for `remoteContent` and for
  `OnArtifactReceived`. The tool looks the task up first. If the lookup fails, it is ignored. The `Timeout` covers the
  lookup.
- The result is JSON with non-ASCII text written as is (not as `\uXXXX` escapes).
- `MaxResultCharacters` (default 32000) cuts long text and sets `truncated`.
- Non-text parts (bytes, URLs) are counted in `nonTextParts` and never sent to the model.
- Failed, rejected, canceled, input-required and auth-required tasks are results with their `state`, not exceptions.
- `Timeout` (default 5 minutes) covers the whole call. On timeout or cancel, the remote task is canceled (best effort)
  and a `TimeoutException` is thrown for a timeout.

## Use a remote agent as an `IChatClient`

```csharp
IChatClient chat = client.AsChatClient();
var response = await chat.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")]);
```

- Only the trailing run of user messages is sent, merged into one A2A message. Earlier messages are not resent, because
  A2A keeps the conversation on the server. System and tool messages are never sent. `ChatOptions.Tools` are ignored.
- `ChatOptions.ConversationId` is the A2A `contextId`. The response `ConversationId` is the context id.
- The task id travels in `AdditionalProperties` under `A2AChatKeys.TaskId`, in both directions. `A2AChatKeys.TaskState`
  has the state on responses. Use them to continue an `INPUT_REQUIRED` task.
- Failed, rejected or canceled tasks throw `InvalidOperationException` with the state only, not the remote text.
- When you continue a task (task id set), the client looks the task up first. Unary calls and streams then return only
  artifacts you have not seen, and skip the old `INPUT_REQUIRED` question that a stream can replay. Updates still carry
  the task id and state. When the status message of an `INPUT_REQUIRED` task only repeats the artifact text (what
  `KeepConversationOpen` produces), it is not returned twice.
- `AdditionalProperties` of messages and contents are never sent to the remote agent (they are caller side data). The
  public `ToA2AMessage` and `ToPart` still copy them.
- Disposing the facade does not dispose the `A2AClient`.

## Conversions

`A2AContentExtensions` has `Part.ToAIContent()`, `AIContent.ToPart()` (can return null), `Message.ToChatMessage()`,
`ChatMessage.ToA2AMessage()`, `Artifact.ToChatMessage()` and `ChatMessage.ToA2AArtifact()`.

| A2A        | M.E.AI                     | Notes                                                           |
|------------|----------------------------|-----------------------------------------------------------------|
| `TextPart` | `TextContent`              |                                                                 |
| `RawPart`  | `DataContent`              | `Filename` is `Name`. No media type: `application/octet-stream` |
| `UrlPart`  | `UriContent`               | No media type: `application/octet-stream`                       |
| `DataPart` | `DataContent` (JSON bytes) | Media type kept if JSON-like, else `application/json`           |

In reverse, `DataContent` with `application/json` or `+json` becomes a `DataPart`. If the bytes are not valid JSON, it
becomes a `RawPart`.

A malformed media type from a remote agent becomes `application/octet-stream`. A URL part with a relative URL becomes
plain `AIContent`, and `ToPart` returns null for it.

Roles: `Agent` is `Assistant`. `User` is `User`. `System` and `Tool` become `User` in `ToA2AMessage`.

### What is lossy

- `FunctionCallContent`, `FunctionResultContent`, `TextReasoningContent`, `ErrorContent`, `UsageContent`,
  `HostedFileContent` and unknown content have no A2A form. `ToPart` returns null and message conversions drop them.
- `TextPart.MediaType` and `Filename` are lost through `TextContent`.
- `DataPart` JSON is re-encoded. The bytes can differ. The meaning does not.
- Metadata and `AdditionalProperties` only carry JSON elements, strings, booleans and numbers. Other values are dropped.

## Security

Content from remote agents and from callers is untrusted.

- **Server.** URL parts are off by default (`AllowUrlParts = false`): a model provider may fetch the URL, which lets a
  caller make your server request addresses of its choice. A message with a URL part is rejected. Caller metadata does
  not reach the model. Reasoning and tool details do not reach the caller. Errors are generic.
- **Client.** Tool name and description never come from the card. System prompts are never forwarded to the remote
  agent. Tool results are size-capped, and bytes and URLs are never inlined.
- **Prompt injection.** The `notice` in every tool result (`UntrustedContentNotice`) tells the model the remote text is
  data, not instructions. This reduces the risk. It does not remove it. A model can still be fooled.
- For sensitive tools, do more:
    - Check every remote message with `A2AClientOptions.OnMessageReceived`.
    - Check every remote artifact with `OnArtifactReceived` (`OnMessageReceived` only sees messages, and agents mostly
      answer in artifacts). Throw to abort.
    - Require human approval before a tool with side effects runs on the strength of remote content.
