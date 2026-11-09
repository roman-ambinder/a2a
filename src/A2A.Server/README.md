# RomanAmbinder.A2A.Server

Transport-agnostic A2A (Agent2Agent) server core: the agent executor model, task lifecycle, per-caller task scoping, stores, event streaming, and push notifications with an SSRF guard. Host it with `RomanAmbinder.A2A.Server.AspNetCore`.

```csharp
services.AddA2AServer<MyAgent>(o => o.AgentCard = card);
```

Retention is **opt-in**: without `services.AddA2ARetention()` nothing is purged, `InterruptedTimeout` does nothing and dead executions are not recovered. `services.AddA2ARetention()` adds a background sweep (one instance at a time) that deletes terminal tasks after `TaskRetention` (7 days) with their events, push configs and deliveries, removes orphan contexts, cancels tasks waiting for input longer than `InterruptedTimeout` (24 hours), and fails tasks whose execution died with its instance. `A2ATaskAdmin.DeleteTaskAsync` deletes one task the same way.

Artifact storage is opt-in: register an `ArtifactObjectStore` (`InMemoryArtifactObjectStore` for tests) and set `A2AServerOptions.ArtifactStorage.PublicBaseUrl`, and a raw part over 256 KiB becomes a `url` part whose bytes live in the store. `TaskUpdater.AddArtifactFromStreamAsync` uploads a large stream. `MapA2AArtifacts()` (in `RomanAmbinder.A2A.Server.AspNetCore`) serves the download with the caller's normal credentials, and `A2AArtifactDownloader` (in `RomanAmbinder.A2A.Client`, .NET 10 only) fetches it. Inbound uploads are opt-in too (`ArtifactStorage.Uploads.Enabled`): `MapA2AUploads()` lets a client upload a large file first and send it as a `url` part, `A2AFileUploader` is the client helper, and the agent reads it with `AgentRequestContext.OpenUploadAsync`. `MaxTaskBytes` (64 MiB) and `MaxArtifactsPerTask` (1024) cap every task. See the [artifact storage guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/artifact-storage.md).

Proactive continuation: an agent that implements `IContinuableAgentExecutor` can continue an existing task with no inbound request. The host calls `TaskExecutionEngine.ContinueTaskAsync(TaskKey, metadata)` from a timer or callback. It returns a status (`Started`, `TaskNotFound`, `TaskTerminal`, `InterruptedTimedOut`, `AlreadyRunning`, `NotSupported`). The run is exclusive across instances. The API does not check the caller: authenticate any endpoint that exposes it.

Push delivery is a durable outbox sent by `PushDeliveryService` (`A2AServerOptions.PushDelivery`); failed deliveries are listed and re-driven with `A2APushAdmin`. Webhooks can be signed (Standard Webhooks): register the push config with scheme `webhook-signature` and a `whsec_` secret, and verify with `PushNotificationReceiver.VerifySignature` from `RomanAmbinder.A2A.Client`.

Many agents in one host: `AddTenantCardProvider<T>()` and `AddAgentExecutorResolver<T>()` pick the card and executor per tenant; the `IAgentCardProvider` completes each tenant's card (kept per card instance), so cache in your provider if building a card is expensive.

Upgrading: `IPushNotificationSender` is gone, `MaxAttempts` and `InitialBackoff` moved to `PushDelivery`, and custom stores have new abstract members (`TaskStore.CommitAsync` and more), `InMemoryTaskStore` and `PostgresTaskStore` have new constructors, and webhooks go through the keyed `HttpMessageInvoker` `PushDeliveryService.InvokerServiceKey` (same value as before). See the repository README, "Upgrading from 0.x / P0".

Tracing: spans on the `A2A.Server` `ActivitySource` (one per operation, agent run and webhook delivery); see [Observability](https://github.com/roman-ambinder/a2a#observability).

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
