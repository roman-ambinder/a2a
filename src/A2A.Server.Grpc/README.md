# RomanAmbinder.A2A.Server.Grpc

gRPC hosting for A2A (Agent2Agent) agents on ASP.NET Core. Runs on the same request handler as the HTTP bindings.

```csharp
builder.Services.AddA2AGrpc();
app.MapA2AGrpc();
```

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
