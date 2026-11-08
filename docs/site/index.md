# A2A .NET SDK

A .NET SDK for the [A2A (Agent2Agent) protocol](https://a2a-protocol.org). It has a server, a client, and three bindings: JSON-RPC, HTTP+JSON and gRPC. It targets .NET 8 and .NET 10.

## Install

```shell
dotnet add package RomanAmbinder.A2A.Server.AspNetCore
dotnet add package RomanAmbinder.A2A.Client
```

Optional packages:

```shell
dotnet add package RomanAmbinder.A2A.Server.Grpc
dotnet add package RomanAmbinder.A2A.Server.Postgres
dotnet add package RomanAmbinder.A2A.Server.Redis
dotnet add package RomanAmbinder.A2A.Client.DependencyInjection
dotnet add package RomanAmbinder.A2A.Client.Grpc
dotnet add package RomanAmbinder.A2A.Security.Http
```

## Guides

- [Getting started](guides/getting-started.md)
- [Bindings](guides/bindings.md)
- [Authentication and ownership](guides/auth-and-ownership.md)
- [Streaming and resume](guides/streaming.md)
- [Push notifications](guides/push-notifications.md)
- [Agent card signing](guides/agent-card-signing.md)
- [Multi-tenancy](guides/multi-tenancy.md)
- [Durable stores](guides/durable-stores.md)
- [Retention](guides/retention.md)
- [Observability](guides/observability.md)
- [Limits and rate limiting](guides/limits-and-rate-limiting.md)
- [Task safety](guides/task-safety.md)
- [Interop and conformance](guides/interop-and-conformance.md)
- [Spec decisions](guides/spec-decisions.md)

## API reference

See the [API reference](api/index.md).
