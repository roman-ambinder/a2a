# RomanAmbinder.A2A.Server.AspNetCore

ASP.NET Core hosting for A2A (Agent2Agent) agents: JSON-RPC and HTTP+JSON endpoints, SSE streaming, and `/.well-known/agent-card.json` with optional JWS signing and JWKS.

```csharp
builder.Services.AddA2AServer<MyAgent>(o => o.AgentCard = card);
app.MapAgentCard();
app.MapA2A("/a2a");
```

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
