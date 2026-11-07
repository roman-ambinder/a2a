# RomanAmbinder.A2A.Client

A2A (Agent2Agent) client for .NET: JSON-RPC and HTTP+JSON transports, SSE streaming, Agent Card discovery with HTTP caching and signature checks, and DI integration.

```csharp
var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), httpClient);
var reply = await client.SendMessageAsync("hello");
```

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
