# RomanAmbinder.A2A.Server

Transport-agnostic A2A (Agent2Agent) server core: the agent executor model, task lifecycle, per-caller task scoping, stores, event streaming, and push notifications with an SSRF guard. Host it with `RomanAmbinder.A2A.Server.AspNetCore`.

```csharp
services.AddA2AServer<MyAgent>(o => o.AgentCard = card);
```

Retention: `services.AddA2ARetention()` adds a background sweep (one instance at a time) that deletes terminal tasks after `TaskRetention` (7 days) with their events, push configs and deliveries, removes orphan contexts, cancels tasks waiting for input longer than `InterruptedTimeout` (24 hours), and fails tasks whose execution died with its instance. `A2ATaskAdmin.DeleteTaskAsync` deletes one task the same way.

Tracing: spans on the `A2A.Server` `ActivitySource` (one per operation, agent run and webhook delivery); see [Observability](https://github.com/roman-ambinder/a2a#observability).

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
