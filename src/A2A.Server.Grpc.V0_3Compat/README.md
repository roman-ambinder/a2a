# RomanAmbinder.A2A.Server.Grpc.V0_3Compat

Serve A2A protocol v0.3 gRPC clients from an A2A v1.0 agent. It adds the v0.3 service `a2a.v1.A2AService` (the proto of
tag v0.3.0) next to the v1.0 service `lf.a2a.v1.A2AService`, on the same address:

```csharp
builder.Services.AddA2AGrpcV03Compat();      // also registers the v1.0 gRPC services, as AddA2AGrpc does
// ...
app.MapA2AGrpc().RequireAuthorization();
app.MapA2AGrpcV03Compat().RequireAuthorization();
```

Every call runs through the same request handler as a v1.0 call, so authentication, ownership, limits and filters are the
same. All ten v0.3 RPCs are served, push configs included (a config created over v0.3 gets v0.3 webhook bodies). Errors are
a gRPC status with a message that starts with the error name (`TaskNotFoundError: ...`), as the official Python SDK 0.3
sends them. An `a2a-version` other than `0.3` is refused with `VersionNotSupportedError`. Activated extensions come back in
`x-a2a-extensions` trailers.

With `EnableV03Compat` on `MapA2A`, the v0.3 Agent Cards list the card's `GRPC` interfaces for v0.3 too.

The package uses internals of the core packages, so all `RomanAmbinder.A2A.*` packages must have the same version.

See the [v0.3 compatibility guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/v03-compatibility.md).
Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a). Targets .NET 10.
