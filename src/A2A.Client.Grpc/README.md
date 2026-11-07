# RomanAmbinder.A2A.Client.Grpc

gRPC transport for the A2A (Agent2Agent) .NET client.

Let the Agent Card choose among JSON-RPC, HTTP+JSON and gRPC (the card's interface order wins):

```csharp
var options = new A2AClientOptions().UseGrpc();
var client = await A2AClientFactory.CreateAsync(agentUrl, httpClient, options);
```

Or require gRPC:

```csharp
var client = A2AGrpcClientFactory.Create(agentCard);
```

Per-call `A2ARequestOptions` headers, extensions, idempotency key and timeout (as the gRPC deadline) apply.
`MaxRetries` and `ConfigureRequest` are HTTP-only; configure gRPC retries with a service config on the channel.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
