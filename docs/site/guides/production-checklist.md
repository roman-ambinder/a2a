# Production checklist

The defaults are safe but in memory. This page lists what to decide before you run an agent for real: read it before your first deployment. Each item links to the guide with the details.

## Identity and access

- [ ] **Authentication is configured**, and `RequireAuthorization()` (or an authorization policy) is on the result of `MapA2A`, or the A2A builder calls `AddApiKeyAuthentication()` or `AddBearerAuthentication()`. Without one of these, the app refuses to start unless <xref:A2A.Server.A2AServerOptions.AllowAnonymous> is true. Anonymous callers share one owner. See [Authentication and ownership](auth-and-ownership.md).
- [ ] **Callers have distinct ids.** The default `IPrincipalResolver` reads the `NameIdentifier` claim, then `sub`. Tasks are bound to that id and the tenant.
- [ ] **Behind a reverse proxy**, call `app.UseForwardedHeaders()` before `UseAuthentication()`, with the proxy in `KnownProxies` or `KnownNetworks`. Otherwise all anonymous callers share the proxy's rate limit budget. See [Limits and rate limiting](limits-and-rate-limiting.md).
- [ ] **`PublicBaseUrl` is set** (`A2AHttpOptions.PublicBaseUrl`) so the Agent Card lists your public URL, not the listen address.
- [ ] **Agent Card signing** if peers must verify who published your card. See [Agent Card signing](agent-card-signing.md).
- [ ] **Client credentials** go through `AddA2AClient` or credential providers, not a plain `HttpClient`. See [Client credentials](client-credentials.md).

## State that survives restarts

- [ ] **Durable stores**: `AddPostgresStores(...)` replaces the in-memory stores. In-memory stores are lost on restart and work for one instance only. See [Durable stores](durable-stores.md).
- [ ] **Several instances**: add `AddRedisEventBus(...)` so live events reach every instance. It also registers the Redis execution coordinator. Without Redis, `AddPostgresStores` registers the Postgres one. Either way the leases of running executions live outside the process, so a crash does not lose them and they expire.
- [ ] **Retention is on**: `services.AddA2ARetention()`. It is opt-in. Without it nothing is purged, `InterruptedTimeout` does nothing, and executions whose instance died are never recovered (their tasks stay `WORKING`). With a durable task store and no retention, startup logs a warning. See [Retention](retention.md).
- [ ] **Push secrets can be decrypted by every instance.** The default protector is per process. Register `AddDataProtection()` with a persisted key ring shared by every instance, then `AddA2ADataProtectionPushSecrets()`. With a durable push outbox and the default protector, startup logs an error, because push configs with a token or credentials would be dead-lettered after a restart or when another instance sends them.
- [ ] **Clocks are in sync (NTP).** The Postgres stores use the database clock for deadlines, but task timestamps come from the instance that wrote them, and large skew weakens the dead-execution guard.
- [ ] **Large artifacts** go to object storage (S3 or Azure Blob) instead of the task store. See [Artifact storage](artifact-storage.md).

## Bounds

- [ ] **Limits fit your traffic.** Defaults: body 10 MB, 100 parts per message, JSON depth 32, history 100, page size 100, 64 MiB and 1024 artifacts per stored task, rate limit burst 20 and 60 per minute. See [Limits and rate limiting](limits-and-rate-limiting.md).
- [ ] **Task time limit and turn cap**: `MaxTaskDuration` and `MaxTurnsPerTask` are off by default. See [Task safety](task-safety.md).
- [ ] **Budgets and execution capacity** for agents that call models or tools. See [Execution limits and budgets](execution-limits.md).
- [ ] **Skill authorization** if some callers may use only some skills. See [Skill authorization](skill-authorization.md).

## Operations

- [ ] **Tracing and metrics are exported.** Add the sources `A2A.Server` and `A2A.Client` and the meter `A2A.Server`. Client-side counters (such as `a2a.card.changed` and `a2a.client.endpoint.refused`) are on the meter `A2A`. See [Observability](observability.md).
- [ ] **Alerts** on the push dead-letter counter (`a2a.server.push.dead_letters`) and on security events (`a2a.server.security.events`).
- [ ] **An admin endpoint of your own**, if you need to delete a task or redrive push dead letters. `A2ATaskAdmin` and `A2APushAdmin` are plain services, not exposed over A2A, so protect that endpoint.
- [ ] **Audit trail** if you must record who did what. See [Audit trail](audit.md).
- [ ] **Startup log is clean.** The SDK logs warnings and errors at startup for risky combinations, for example a durable store without retention, a durable store with the in-memory coordinator, or a durable push outbox with the per-process secret protector. Fix them rather than silence them.

## Before you ship

- [ ] **Tests** with `A2A.TestKit`. See [Testing your agent](testing.md).
- [ ] **Interop** with the peers you expect. See [Interop and conformance](interop-and-conformance.md).
- [ ] **All `RomanAmbinder.A2A.*` packages have the same version.**
- [ ] **A runnable example** of two instances with Postgres and Redis is in [samples/MultiInstance](https://github.com/roman-ambinder/a2a/blob/main/samples/MultiInstance/README.md).
