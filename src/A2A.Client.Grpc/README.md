# RomanAmbinder.A2A.Client.Grpc

gRPC transport for the A2A (Agent2Agent) .NET client.

Let the Agent Card choose among JSON-RPC, HTTP+JSON and gRPC (the card's interface order wins):

```csharp
var options = new A2AClientOptions().UseGrpc();
var client = await A2AClientFactory.CreateAsync(agentUrl, httpClient, options);
```

Or require gRPC (fails when the card has no gRPC interface):

```csharp
var client = await A2AGrpcClientFactory.CreateAsync(agentUrl, httpClient);  // fetches the card
var client2 = A2AGrpcClientFactory.Create(agentCard);                        // card already in hand
```

Per-call `A2ARequestOptions` headers, extensions, idempotency key, timeout (as the per-attempt gRPC deadline) and
`MaxRetries` apply, with the same retry rules as the HTTP transports. `ConfigureRequest` is HTTP-only: a call that sets
it fails with `NotSupportedException`. Stream resume is not supported on gRPC.

Clients made by `UseGrpc()` or `A2AGrpcClientFactory` share one channel per agent address and channel options. A call
holds its channel while it runs. A channel no call holds is disposed after 5 minutes idle, or when more than 64 are idle,
and the next call opens a new one. A host that calls many agents does not keep a channel per agent forever.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a),
implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 10.
See the [bindings guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/bindings.md) and the
[repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
