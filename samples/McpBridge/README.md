# McpBridge sample

One small web app with two modes. It shows `A2A.Mcp` in both directions. It listens on `127.0.0.1:5290` only and needs
no keys.

| Mode     | What it does                                                                                                                          |
|----------|---------------------------------------------------------------------------------------------------------------------------------------|
| `expose` | Hosts an A2A agent with two skills (`shout`, `reverse`) at `/a2a`. An MCP server at `/mcp` serves them as tools through `AsMcpTools`. |
| `call`   | Hosts an MCP server with an `add` tool at `/mcp`. An A2A agent at `/a2a` runs it through `AddMcpToolAgent`.                           |

Anonymous access is on for both endpoints because the sample is loopback only. A real host needs authentication. See
the [guide](../../docs/site/guides/mcp-bridge.md).

## Try `expose`

```bash
dotnet run --project samples/McpBridge -- expose
```

In another terminal. MCP over HTTP needs both `Accept` types. This server is stateless, so no session id is needed.

```bash
H=(-H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream')
curl -s http://127.0.0.1:5290/.well-known/agent-card.json
curl -s http://127.0.0.1:5290/mcp "${H[@]}" -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}'
curl -s http://127.0.0.1:5290/mcp "${H[@]}" -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
curl -s http://127.0.0.1:5290/mcp "${H[@]}" -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"text_shout","arguments":{"message":"hello bridge"}}}'
```

The list has `text_shout`, `text_reverse`, `text_get_task` and `text_cancel_task`. The call returns `HELLO BRIDGE` in
`remoteContent`.

## Try `call`

```bash
dotnet run --project samples/McpBridge -- call
```

```bash
curl -s http://127.0.0.1:5290/.well-known/agent-card.json
curl -s http://127.0.0.1:5290/a2a -H 'Content-Type: application/json' -H 'A2A-Version: 1.0' \
  -d '{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"data":{"a":2,"b":3}}]}}}'
```

The task completes with an artifact named `result` that holds `5`. The `add` skill is the default tool, because it is
the only one allowed.

Stop either mode with Ctrl+C.
