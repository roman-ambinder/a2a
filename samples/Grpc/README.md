# Grpc sample

One agent served over gRPC and JSON-RPC, called by three clients. The agent answers with the binding the request came
in on (`AgentRequestContext.Call.Binding`), so the sample checks which binding each client chose. It listens on
`127.0.0.1` only and needs no keys.

| Port | Protocol | Serves |
|---|---|---|
| 5310 | HTTP/1.1 | the Agent Card and JSON-RPC (`MapAgentCard`, `MapA2A("/a2a")`) |
| 5311 | HTTP/2, cleartext | gRPC (`AddA2AGrpc`, `MapA2AGrpc`) |

gRPC needs HTTP/2. Without TLS it gets a port of its own. The card lists the gRPC interface first.

| Client | Uses |
|---|---|
| `A2AGrpcClientFactory.CreateAsync` | gRPC, or it throws `UnsupportedOperation` |
| `A2AClientFactory.CreateAsync` with `new A2AClientOptions().UseGrpc()` | gRPC, because the card lists it first |
| `A2AClientFactory.CreateAsync` | JSON-RPC: without `UseGrpc()` the factory never picks gRPC |

A fourth call streams over gRPC with the `A2AGrpcClientFactory` client. gRPC streams carry no event ids, so they do
not resume with `Last-Event-ID`.

```bash
dotnet run --project samples/Grpc
```

Expected output ends with:

```text
A2AGrpcClientFactory: GRPC
A2AClientFactory + UseGrpc(): GRPC
A2AClientFactory: JSONRPC
  chunk: over 
  chunk: GRPC
gRPC stream ends as: Completed
OK: the gRPC clients used gRPC, the plain client used JSON-RPC, and a gRPC stream completed.
```

Anonymous access is on because the sample is loopback only. A real agent needs authentication and
`RequireAuthorization()` on `MapA2A` and `MapA2AGrpc`. See the [bindings guide](../../docs/site/guides/bindings.md).
