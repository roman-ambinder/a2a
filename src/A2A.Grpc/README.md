# RomanAmbinder.A2A.Grpc

A2A (Agent2Agent) gRPC binding types: generated messages and stubs for `lf.a2a.v1.A2AService`, plus mapping to the A2A .NET model and gRPC error mapping. Used by `RomanAmbinder.A2A.Server.Grpc` and `RomanAmbinder.A2A.Client.Grpc`.

```csharp
var proto = ProtoMapper.ToProto<Lf.A2a.V1.Task>(task);
```

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
