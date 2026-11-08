# RomanAmbinder.A2A.Client

A2A (Agent2Agent) client for .NET: JSON-RPC and HTTP+JSON transports, SSE streaming, Agent Card discovery with HTTP caching and signature checks. DI registration (`AddA2AClient`) is in `RomanAmbinder.A2A.Client.DependencyInjection`.

```csharp
var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), httpClient);
var reply = await client.SendMessageAsync("hello");
```

Peer identity: `ResolvedAgentCard.Identity` is a `PeerIdentity` (origin and key id), and `AgentRegistry` rejects duplicate agent names and warns on lookalikes; see [Security](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/spec-decisions.md).

Tracing: spans on the `A2A.Client` `ActivitySource`, with W3C `traceparent` sent on every call; see [Observability](https://github.com/roman-ambinder/a2a#observability).

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
