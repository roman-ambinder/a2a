# RomanAmbinder.A2A.Server

Transport-agnostic A2A (Agent2Agent) server core: the agent executor model, task lifecycle, per-caller task scoping, stores, event streaming, and push notifications with an SSRF guard. Host it with `RomanAmbinder.A2A.Server.AspNetCore`.

```csharp
services.AddA2AServer<MyAgent>(o => o.AgentCard = card);
```

Retention is **opt-in**: without `services.AddA2ARetention()` nothing is purged, `InterruptedTimeout` does nothing and dead executions are not recovered. `services.AddA2ARetention()` adds a background sweep (one instance at a time) that deletes terminal tasks after `TaskRetention` (7 days) with their events, push configs and deliveries, removes orphan contexts, cancels tasks waiting for input longer than `InterruptedTimeout` (24 hours), and fails tasks whose execution died with its instance. `A2ATaskAdmin.DeleteTaskAsync` deletes one task the same way.

Push delivery is a durable outbox sent by `PushDeliveryService` (`A2AServerOptions.PushDelivery`); failed deliveries are listed and re-driven with `A2APushAdmin`. Webhooks can be signed (Standard Webhooks): register the push config with scheme `webhook-signature` and a `whsec_` secret, and verify with `PushNotificationReceiver.VerifySignature` from `RomanAmbinder.A2A.Client`.

Many agents in one host: `AddAgentCardProvider<T>()` and `AddAgentExecutorResolver<T>()` pick the card and executor per tenant. The SDK does not cache cards across requests, so cache in your provider.

Upgrading: `IPushNotificationSender` is gone, `MaxAttempts` and `InitialBackoff` moved to `PushDelivery`, and custom stores have new abstract members (`TaskStore.CommitAsync` and more). See the repository README, "Upgrading from 0.x / P0".

Tracing: spans on the `A2A.Server` `ActivitySource` (one per operation, agent run and webhook delivery); see [Observability](https://github.com/roman-ambinder/a2a#observability).

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
