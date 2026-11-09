# RomanAmbinder.A2A.Server.Grpc

gRPC hosting for A2A (Agent2Agent) agents on ASP.NET Core. Runs on the same request handler as the HTTP bindings.

```csharp
builder.Services.AddA2AGrpc();   // also registers AddA2AServerAspNetCore()
app.UseA2A();
app.MapA2AGrpc().RequireAuthorization();   // without it the app refuses to start (unless AllowAnonymous)
```

Caller identity, tenant, `A2A-Version`, `A2A-Extensions` and `Idempotency-Key` are read exactly as the HTTP bindings
read them. Like `MapA2A`, the app fails at startup if the gRPC endpoints have no authorization policy (unless
`A2AServerOptions.AllowAnonymous` is set). The `"a2a"` rate limit policy applies too; its rejections are
`RESOURCE_EXHAUSTED` with `RetryInfo`.

Extension operations (`AddExtensionOperation`) are served by a second service, `a2a.extensions.v1.A2AExtensionService`, which has
the same definition as the official A2A .NET SDK. Map it with its own call, so `RequireAuthorization()` applies to it on its own:

```csharp
app.MapA2AGrpcExtensions().RequireAuthorization();
```

The `payload` fields are UTF-8 JSON. Only the unary `InvokeExtensionOperation` is served; the streaming RPC answers `UNIMPLEMENTED`.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
