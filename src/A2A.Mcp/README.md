# A2A.Mcp

Bridge between A2A and the Model Context Protocol (MCP). NuGet ID: `RomanAmbinder.A2A.Mcp`. Use it to let an MCP
client (such as a coding assistant) call an A2A agent, or to put MCP tools behind an A2A agent.

```bash
dotnet add package RomanAmbinder.A2A.Mcp
```

It works in two directions:

- **Expose.** The skills of a remote A2A agent become MCP tools, served by any MCP server.
- **Call.** An A2A agent runs MCP tools.

It uses `ModelContextProtocol.Core` only (not the hosting package). It is AOT compatible.

See the [MCP bridge guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/mcp-bridge.md) for the full
story, and the [McpBridge sample](https://github.com/roman-ambinder/a2a/blob/main/samples/McpBridge/Program.cs) for a
runnable one.

## Expose a remote A2A agent as MCP tools

```csharp
IReadOnlyList<McpServerTool> tools = a2aClient.AsMcpTools(new A2AMcpToolOptions { ToolNamePrefix = "billing_" });
builder.Services.AddMcpServer().WithHttpTransport().WithTools(tools);
```

Each skill becomes a tool that takes `message` (required), `contextId` and `taskId`. `get_task` and `cancel_task` are
added too. The result is one JSON object (also in `structuredContent`) with `state`, `taskId`, `contextId`,
`remoteContent`, `truncated`, `omittedParts` and `notice`. Bytes and links come as extra content blocks.
`ToolNamePrefix` is put before every tool name, `get_task` and `cancel_task` included; set `IncludeTaskTools = false` to
leave those two out.

- A call waits up to `WaitTimeout` (30 s). If the task is still running, the result is a handle: call `get_task` later.
  A timeout never cancels the task. An MCP cancel does.
- `input-required` is not an error: call the tool again with the `taskId`. `auth-required`, `failed`, `rejected` and
  `canceled` are tool errors.
- Tool descriptions are fixed text. The Agent Card is remote text, so use `SkillDescription = s => s.Description` only
  for a card you trust.
- Credentials are never forwarded. The A2A client uses its own credentials. Use `CreateRequestOptions` if you must pass
  something on.
- All MCP callers share the identity of the A2A client, so the bridge keeps its own check: a task id or context id works
  only for the caller it was issued to. The check is in memory and per process. Authenticate your MCP callers, or all
  anonymous callers share tasks. Turn the check off with `ValidateTaskOwnership = false` only when everyone may see
  every task.
- Skill selection is best effort: a plain A2A peer ignores it.

## Run MCP tools as an A2A agent

```csharp
builder.Services.AddSingleton(mcpClient);   // the host owns the client and its credentials
builder.Services.AddA2A(a => a
    .AddAgentCard(card)
    .AddMcpToolAgent(o => o.AllowTool("search", textArgument: "query")));
```

Only allowed tools run. The caller picks one with skill selection (the skill id is the tool name), or the default tool
runs (with one allowed tool, that is the default). The arguments are one JSON data part with an object, or plain text
for a tool with a `textArgument`. Anything else is rejected, and the tool name the caller sent is never echoed.

A tool error fails the task with a fixed text (`IncludeToolErrorText` copies the MCP text). Progress is forwarded as
status updates, at most one per `ProgressInterval`. A URL elicitation request from the MCP server fails the task: the
URL is not forwarded. Use `McpToolSkills.ToAgentSkills` to describe the allowed tools on the card.

`A2AMcpContentExtensions` has `ToPart`, `ToContentBlock` and `ToParts` for your own executors. Only `http` and `https`
links are converted.

## Does not map

A2A skills have no input schema. A2A metadata, artifact names and streaming chunks are lost. MCP elicitation and
sampling are not bridged. Only tools are bridged, not resources or prompts.

## Dependencies

The package is built on `ModelContextProtocol.Core` 2.4.0. It brings the 10.x `Microsoft.Extensions.*` abstractions.
