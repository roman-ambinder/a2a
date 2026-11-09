# A2A .NET SDK

A .NET SDK for the [A2A (Agent2Agent) protocol](https://a2a-protocol.org). It has a server, a client, and three bindings: JSON-RPC, HTTP+JSON and gRPC. It targets .NET 10. The protocol and client packages also target netstandard2.0.

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
dotnet add package RomanAmbinder.A2A.Server.TypedSkills
dotnet add package RomanAmbinder.A2A.Client.DependencyInjection
dotnet add package RomanAmbinder.A2A.Client.Grpc
dotnet add package RomanAmbinder.A2A.Client.Postgres
dotnet add package RomanAmbinder.A2A.Security.Http
dotnet add package RomanAmbinder.A2A.Extensions.AI
dotnet add package RomanAmbinder.A2A.Mcp
dotnet add package RomanAmbinder.A2A.Server.V0_3Compat
dotnet add package RomanAmbinder.A2A.Client.V0_3Compat
```

To test your agent:

```shell
dotnet add package RomanAmbinder.A2A.TestKit
```

## Guides

**Understand the product** (high level to detailed)

1. [Product overview](guides/overview.md): what it is, who it is for, a map of the features
2. [Core concepts](guides/concepts.md): agents, cards, messages, tasks, artifacts
3. [Architecture](guides/architecture.md): packages, server parts, extension seams
4. [How a request flows](guides/request-lifecycle.md): one call, step by step
5. [Production checklist](guides/production-checklist.md): what to decide before you go live

**Build**

- [Getting started](guides/getting-started.md)
- [Bindings](guides/bindings.md)
- [Authentication and ownership](guides/auth-and-ownership.md)
- [Client credentials](guides/client-credentials.md)
- [Streaming and resume](guides/streaming.md)
- [Push notifications](guides/push-notifications.md)
- [Proactive continuation](guides/proactive-continuation.md)
- [Client outbox](guides/client-outbox.md)
- [Agent card signing](guides/agent-card-signing.md)
- [Peer identity and registry](guides/peer-identity.md)
- [Skill selection](guides/skill-selection.md)
- [Typed skills](guides/typed-skills.md)
- [Skill authorization](guides/skill-authorization.md)
- [Custom operations](guides/custom-operations.md)
- [Microsoft.Extensions.AI](guides/extensions-ai.md)
- [Conversation memory](guides/conversation-memory.md)
- [MCP bridge](guides/mcp-bridge.md)
- [v0.3 compatibility](guides/v03-compatibility.md)

**Operate**

- [Multi-tenancy](guides/multi-tenancy.md)
- [Durable stores](guides/durable-stores.md)
- [Retention](guides/retention.md)
- [Artifact storage](guides/artifact-storage.md)
- [Observability](guides/observability.md)
- [Audit trail](guides/audit.md)
- [Limits and rate limiting](guides/limits-and-rate-limiting.md)
- [Execution limits and budgets](guides/execution-limits.md)
- [Client resilience](guides/client-resilience.md)
- [Task safety](guides/task-safety.md)

**Verify and reference**

- [Interop and conformance](guides/interop-and-conformance.md)
- [Testing your agent](guides/testing.md)
- [Spec decisions](guides/spec-decisions.md)
- [Command line tool](guides/cli.md)
- [Versioning](guides/versioning.md)

## API reference

See the [API reference](api/index.md).
