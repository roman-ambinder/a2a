# A2A .NET SDK

A .NET SDK for the [A2A (Agent2Agent) protocol](https://a2a-protocol.org). It has a server, a client, and three bindings: JSON-RPC, HTTP+JSON and gRPC. It targets .NET 10. The protocol and client packages also target netstandard2.0.

## Install

```shell
dotnet add package RomanAmbinder.A2A.Server.AspNetCore
dotnet add package RomanAmbinder.A2A.Client
```

The first package hosts an agent, the second calls agents. For gRPC, durable stores, several instances, Azure or AWS identity, object storage, MCP, Microsoft.Extensions.AI and v0.3, see [Which packages do I install](guides/architecture.md#which-packages-do-i-install).

To test your agent:

```shell
dotnet add package RomanAmbinder.A2A.TestKit
```

## Guides

New here? Read the first five in order. The other groups can be read in any order, when you need them. The same groups are in the menu on the left.

**Start here** (high level to detailed)

1. [Product overview](guides/overview.md): what it is, who it is for, a map of the features
2. [Core concepts](guides/concepts.md): agents, cards, messages, tasks, artifacts
3. [Architecture](guides/architecture.md): packages, server parts, extension points
4. [How a request flows](guides/request-lifecycle.md): one call, step by step
5. [Getting started](guides/getting-started.md): host an agent and call it

**Protocol features**

- [Bindings](guides/bindings.md): JSON-RPC, HTTP+JSON and gRPC, and how a client picks one
- [Streaming and resume](guides/streaming.md)
- [Push notifications](guides/push-notifications.md)
- [Proactive continuation](guides/proactive-continuation.md)
- [Client outbox](guides/client-outbox.md)
- [Custom operations](guides/custom-operations.md)
- [Metadata keys](guides/metadata.md): every metadata key the SDK reads or writes

**Security and identity**

- [Authentication and ownership](guides/auth-and-ownership.md)
- [Client credentials](guides/client-credentials.md)
- [Identity providers](guides/identity-providers.md): Azure managed identity, AWS SigV4
- [Agent card signing](guides/agent-card-signing.md)
- [Peer identity and registry](guides/peer-identity.md)

**Skills**

- [Skill selection](guides/skill-selection.md)
- [Typed skills](guides/typed-skills.md)
- [Skill authorization](guides/skill-authorization.md)

**Integrations**

- [Microsoft.Extensions.AI](guides/extensions-ai.md)
- [Conversation memory](guides/conversation-memory.md)
- [MCP bridge](guides/mcp-bridge.md)
- [v0.3 compatibility](guides/v03-compatibility.md)

**Run in production**

- [Production checklist](guides/production-checklist.md): what to decide before you go live
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

**Test and reference**

- [Testing your agent](guides/testing.md)
- [Interop and conformance](guides/interop-and-conformance.md)
- [Command line tool](guides/cli.md)
- [Spec decisions](guides/spec-decisions.md)
- [Versioning](guides/versioning.md)

## API reference

See the [API reference](api/index.md).
