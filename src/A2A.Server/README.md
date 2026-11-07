# RomanAmbinder.A2A.Server

Transport-agnostic A2A (Agent2Agent) server core: the agent executor model, task lifecycle, per-caller task scoping, stores, event streaming, and push notifications with an SSRF guard. Host it with `RomanAmbinder.A2A.Server.AspNetCore`.

```csharp
services.AddA2AServer<MyAgent>(o => o.AgentCard = card);
```

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
