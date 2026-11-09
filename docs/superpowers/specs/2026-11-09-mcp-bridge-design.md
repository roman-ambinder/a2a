# A2A.Mcp: bridge between A2A and MCP

Date: 2026-11-09. Status: approved for implementation.

## 1. Goal

A new package, `A2A.Mcp`, with two directions:

- **Expose:** a remote A2A agent's skills become MCP tools, served by any MCP server.
- **Call:** an A2A agent runs MCP tools.

## 2. SDK facts (checked 2026-11-09 against the NuGet packages)

- `ModelContextProtocol.Core` 2.4.0 is the latest stable. It has targets for net8, net9, net10 and netstandard2.0. It pulls `Microsoft.Extensions.AI.Abstractions` 10.11 and System.Text.Json 10.
- `McpServerTool` is abstract: `ProtocolTool`, `Metadata`, `InvokeAsync(RequestContext<CallToolRequestParams>, CancellationToken)`.
- `McpClient.CallToolAsync(name, args, IProgress<ProgressNotificationValue>, RequestOptions, ct)` returns `CallToolResult` (`Content`, `StructuredContent`, `IsError`).
- SDK guidance: tool failures go in `IsError = true`, not in a JSON-RPC error. `McpProtocolException` is for protocol problems.
- MCP Tasks are no longer in the core SDK. They live in `ModelContextProtocol.Extensions.Tasks`. Most MCP clients do not speak them yet.
- The July 2026 protocol has multi-round-trip input (`InputRequiredResult`). The SDK client resolves it through its own elicitation and sampling handlers.

## 3. Decisions

**D1. Package.** `src/A2A.Mcp`, namespace `A2A.Mcp`, NuGet id `RomanAmbinder.A2A.Mcp`. References `A2A.Client`, `A2A.Server`, `ModelContextProtocol.Core` (not the hosting package), and `Microsoft.Extensions.DependencyInjection.Abstractions`. net8 and net10. AOT clean. Tests go in a new `tests/A2A.Mcp.Tests`, for the same JSON-stack reason as `A2A.Extensions.AI.Tests`.

**D2. Not using MCP Tasks.** A returned task handle plus companion tools works with every MCP client. The Tasks extension is a later option (see section 7).

**D3. Remote text never becomes tool metadata by default.** An Agent Card or an MCP tool list is remote text, and tool descriptions go straight to a model. A default description is fixed text. A one-line callback opts in to using the remote text.

**D4. Credentials are not forwarded.** The bridge never reads an incoming `Authorization` header or other caller credential. Each side uses the credentials its own client was built with. Forwarding needs explicit host code (D7).

**D5. A shared identity needs its own task check.** All MCP callers share the bridge's A2A identity, so the A2A server cannot tell them apart. The bridge remembers which owner it issued a task or context id to, and refuses others (D8).

## 4. Expose: A2A skills as MCP tools

```csharp
IReadOnlyList<McpServerTool> tools = a2aClient.AsMcpTools(options, card /* optional, default client.AgentCard */);
```

`A2AClientMcpExtensions.AsMcpTools(this A2AClient client, A2AMcpToolOptions? options = null, AgentCard? card = null)`. No card anywhere: `InvalidOperationException`. The tools are internal subclasses of `McpServerTool`. The host adds them to its MCP server, for example with `WithTools(tools)`.

`A2AMcpToolOptions`:

| Option | Default | Meaning |
|---|---|---|
| `ToolNamePrefix` | `""` | Added to every tool name, including task tools. |
| `SkillFilter` | all | `Func<AgentSkill, bool>`. Which skills become tools. |
| `SkillDescription` | fixed text | `Func<AgentSkill, string>`. Use `s => s.Description` to trust the card. |
| `IncludeTaskTools` | true | Adds `get_task` and `cancel_task`. |
| `WaitTimeout` | 30 s | How long a call waits before returning a handle. Common MCP clients time out at 60 s. Infinite allowed. |
| `PollInterval` | 1 s | At least 100 ms. |
| `MaxResultCharacters` | 32000 | At least 1. Longer remote text is cut. |
| `MaxBinaryBytes` | 1 MiB | At least 0. Largest total of inline binary content. |
| `UntrustedContentNotice` | as in `A2AAgentFunctionOptions` | In every result. |
| `CreateRequestOptions` | null | `Func<RequestContext<CallToolRequestParams>, A2ARequestOptions?>`. Must return a new object each call: `SelectSkill` changes it. |
| `ValidateTaskOwnership` | true | D8. |
| `TaskOwner` | see D8 | `Func<RequestContext<CallToolRequestParams>, string?>`. |
| `MaxTrackedTasks` | 10000 | At least 1. Oldest entries are dropped first. |

Tool names: skill id with every character outside `[A-Za-z0-9_.-]` replaced by `_`, cut so the prefixed name is at most 128 characters. Collisions get `_2`, `_3`. Order is card order. A skill id that is empty is skipped.

Tool schema for every skill: `message` (required string), `contextId`, `taskId` (optional strings), `additionalProperties: false`. A2A skills have no input schema. `OutputSchema` declares the result object below. Annotations: `OpenWorldHint = true`. Other hints stay unset because the card says nothing about them.

Call flow:

1. Missing or blank `message`: tool error (`IsError = true`).
2. If `taskId` or `contextId` is given, check ownership (D8).
3. Build the message. `SelectSkill` sets the skill. A plain A2A peer ignores it, so the guide says skill routing is best effort.
4. `SendMessageAsync` with `ReturnImmediately = true`.
5. Poll `GetTaskAsync` every `PollInterval` until the task is terminal or interrupted, or `WaitTimeout` passes. If the MCP caller sent a progress token, send `notifications/progress` when the state or status text changes. `Progress` is a counter of changes; there is no `Total`.
6. Build the result. Only artifacts new in this turn are included (same rule as `A2AAgentFunction`).

### Result

`StructuredContent` and `Content[0]` (as JSON text) carry the same object, as the MCP spec advises:

```json
{ "state": "completed", "taskId": "...", "contextId": "...", "remoteContent": "...", "truncated": false, "omittedParts": 0, "notice": "..." }
```

Further `Content` blocks carry non-text parts: `RawPart` becomes `ImageContentBlock`, `AudioContentBlock` or an `EmbeddedResourceBlock` with a blob (uri `a2a://part/{index}`). `UrlPart` becomes `ResourceLinkBlock`. `omittedParts` counts parts cut by `MaxBinaryBytes` or not convertible.

### State mapping

| A2A | `state` | `IsError` | Notes |
|---|---|---|---|
| Message reply | `completed` | false | |
| completed | `completed` | false | Artifacts, else status message. |
| submitted, working at timeout | same | false | A handle. Text says to call `get_task`. Task keeps running. |
| input-required | `input-required` | false | Question is `remoteContent`. Call again with `taskId`. |
| auth-required | `auth-required` | true | Task stays open. Text says authorization happens outside MCP. Remote status text is not included, because it may carry a link for a person, and the model should not follow it. |
| failed, rejected, canceled | same | true | Status text if any. |

Other errors, all `IsError = true` with fixed text: `A2AException` (text includes the A2A error name, not the remote message); send timed out before a task id was known (`state` `unknown`; peers that ignore `ReturnImmediately` can hit this); network error. Exception details are not returned. `OperationCanceledException` from the MCP caller's token: best-effort `CancelTaskAsync` (5 s budget) if a task id is known, then rethrow. Timeouts do not cancel.

### Task tools

`{prefix}get_task(taskId)` returns the same result object from a snapshot and includes all the task's artifacts. `{prefix}cancel_task(taskId)` calls `CancelTaskAsync` and returns the object. Both check D8.

### D8. Task ownership

Bounded in-memory map per `AsMcpTools` call: id (task and context, in separate key spaces) to owner. Default owner: the caller's `NameIdentifier` claim, else `Identity.Name`, else the MCP session id, else `""` (stdio has one client). Unknown id and someone else's id give the same error: `Unknown or expired task.`

An owner of `""` on stateless HTTP means all anonymous callers share tasks: the guide says to authenticate. The map is per process: with several replicas or after a restart, handles are refused. `ValidateTaskOwnership = false` turns the check off, and the guide states the risk.

## 5. Call: an A2A agent runs MCP tools

```csharp
public sealed class McpToolAgentExecutor : IAgentExecutor
    public McpToolAgentExecutor(McpClient client, McpToolAgentOptions options, ILogger<McpToolAgentExecutor>? logger = null)
```

The host owns the `McpClient` and its transport credentials. Nothing from `AgentRequestContext.Call` reaches it. Registration: `A2AServerBuilder.AddMcpToolAgent(Action<McpToolAgentOptions> configure, object? serviceKey = null)`, scoped, resolves `McpClient` from DI like `AddChatClientAgent`.

`McpToolAgentOptions`:

| Member | Default | Meaning |
|---|---|---|
| `AllowTool(string name, string? textArgument = null)` | none | Allowlist. No tools at construction: `ArgumentException`. `textArgument` names the argument that receives plain text. |
| `DefaultTool` | null | Used when no skill is selected. With one allowed tool, that tool is the default. |
| `ToolTimeout` | 5 min | Infinite allowed. |
| `ArtifactName` | `"result"` | |
| `ProgressInterval` | 1 s | At least 100 ms. Durable stores write on every event, so progress is throttled. `Timeout.InfiniteTimeSpan` turns forwarding off. |
| `IncludeToolErrorText` | false | Copy MCP error text into the failed status. Otherwise a fixed text is sent and the detail is logged. |

Flow:

1. Tool: `context.SelectedSkill…` (the executor reads the same selection property that S35 added), else the default. Not allowed or none: `RejectAsync` with fixed text. The caller's tool name is not echoed.
2. Arguments: exactly one `DataPart` holding a JSON object, or text parts when the tool has `textArgument`. Any other mix, including file and URL parts: `RejectAsync`.
3. `StartWorkAsync`. Call the tool with a linked token and `ToolTimeout`. `IProgress` writes to a single-slot channel; a pump sends `Working` status updates with the MCP progress message, at most once per `ProgressInterval`. The pump is stopped before the final state.
4. Outcome:

| MCP | A2A |
|---|---|
| Success | Artifact with converted parts, then `CompleteAsync`. Empty content: complete with no artifact. |
| `IsError = true` | `FailAsync`, fixed text unless `IncludeToolErrorText`. Detail logged. |
| `McpProtocolException` with `InvalidParams` | `RejectAsync("The tool rejected the arguments.")` |
| `UrlElicitationRequiredException` | `FailAsync`, fixed text: authorization outside this bridge is needed. URL is never forwarded. |
| `ToolTimeout` passed | `FailAsync("The tool did not finish in time.")` The SDK sends MCP cancel. |
| Caller cancel, shutdown | Token cancelled; the engine marks CANCELED; the SDK sends MCP cancel. |
| Other exception | Rethrow; the engine marks FAILED with its generic text and logs it. |

Content mapping:

| MCP | A2A |
|---|---|
| Text | `TextPart` |
| Image, audio | `RawPart` with media type |
| Embedded text resource | `TextPart` with media type |
| Embedded blob | `RawPart` with media type |
| Resource link, http or https | `UrlPart` with media type and name as filename |
| Resource link, other scheme | dropped |
| `StructuredContent` | One `DataPart`. A text block that is the same JSON is dropped. |
| Tool-use and tool-result blocks | dropped |

Public conversions: `A2AMcpContentExtensions.ToPart(this ContentBlock)` (nullable), `ToContentBlock(this Part)` (nullable), `ToParts(this CallToolResult)`. Custom executors use them.

`McpToolSkills.ToAgentSkills(IEnumerable<McpClientTool> tools, Func<McpClientTool, string>? describe = null)`: id and name are the tool name, description is fixed text unless `describe` is given, one tag `mcp`, input mode `application/json`. Only tools you pass are listed, so filter to the allowlist first.

LLM route: `McpClientTool` is already an `AIFunction`, so a `ChatClientAgentExecutor` can use MCP tools with `ChatOptions.Tools`. No bridge code needed. The guide shows it, and warns that tool descriptions and results are untrusted.

## 6. What does not map

Stated in the guide.

- A2A skills have no input schema, so tools take free text.
- A2A `Part` and `Artifact` metadata, artifact names, status timestamps, A2A extensions.
- Streaming artifact chunks: MCP gets the final result only. Progress carries status text.
- Push notifications: the MCP side polls.
- MCP elicitation and sampling (`Call` direction): the host owns the client. A2A `input-required` ends a turn, while MCP elicitation keeps a request open, so they cannot be joined without holding a call across turns. Handlers are not set by this package. MCP servers needing them fail the tool call.
- MCP resources, prompts, roots, logging: only tools are bridged.
- `tools/list_changed`: `AsMcpTools` is a snapshot of the card. Skills added later need a new call.
- URL-mode elicitation: refused (section 5).
- `A2A` skill-level security requirements are not checked by the bridge. The A2A server enforces them against the bridge's identity.

## 7. Later

- Map a structured S38 elicitation to MCP elicitation in the Expose direction when the caller supports it.
- MCP Tasks through `ModelContextProtocol.Extensions.Tasks` when clients adopt it.
- Input schemas, if the typed-skills work adds them to the card.

## 8. Tests (`tests/A2A.Mcp.Tests`)

Real MCP client and server over in-memory pipes (`StreamClientTransport`, `StreamServerTransport`), and a fake `IA2AClient` or real A2A `TestServer`.

Expose: names, sanitizing, collisions, filter; description defaults to fixed text; every state in the table; timeout returns a handle and does not cancel; MCP cancel does cancel; progress sent; get and cancel tools; ownership: other owner refused with the same text as unknown, eviction; truncation; binary limit; part mapping; errors have no exception text.

Call: allowlist, reject without echo; args from data and text; rejects mixes; every outcome in the table; progress throttle; pump stopped before final state; timeout; caller cancel reaches MCP; content mapping; structured dedupe.

Credentials, both directions, over real HTTP on loopback, asserting what the downstream server received: the caller's `Authorization` header and cookie do not appear; the downstream sees only the bridge's own credential.

## 9. Changes made during implementation

- No progress notification for a final or interrupted state: the result itself says so, and a notification just before it can arrive after the call ended.
- `cancel_task` returning `canceled` is not an error, because the caller asked for it.
- A remote `TaskNotFound` gives the same text as an unknown task.
- URL parts convert only for `http` and `https`, in both directions.
- Plain errors have one fixed text block and no structured content.
- When a caller answers a question, `ReturnImmediately` can return a snapshot that still shows that question. The call compares status message ids and keeps waiting, and at timeout it reports `working`, never the old question.
- A failed poll after the task exists returns the handle, not an error.
