# RomanAmbinder.A2A.Protocol

The A2A (Agent2Agent) protocol v1.0 data model for .NET: types generated from the official `a2a.proto`, ProtoJSON serialization, error codes, and Agent Card signing and verification (JWS + RFC 8785).

```csharp
var json = A2AJson.Serialize(Message.FromUser("hello"));
```

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
