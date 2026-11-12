# RomanAmbinder.A2A.Server.AspNetCore

ASP.NET Core hosting for A2A (Agent2Agent) agents: JSON-RPC and HTTP+JSON endpoints, SSE streaming, and
`/.well-known/agent-card.json` with optional JWS signing and JWKS. This is the package to add when you host an agent.

```bash
dotnet add package RomanAmbinder.A2A.Server.AspNetCore
```

```csharp
builder.Services.AddA2A(a => a.AddAgentExecutor<MyAgent>().AddAgentCard(card));
// ...
app.UseA2A();                               // rate limiting; the app refuses to start without it
app.MapA2A("/a2a").RequireAuthorization();  // also maps the Agent Card at /.well-known/agent-card.json
```

Without `RequireAuthorization()` (or `A2AServerOptions.AllowAnonymous`) the app refuses to start.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a),
implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 10.
See the [getting started guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/getting-started.md) and the
[repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
