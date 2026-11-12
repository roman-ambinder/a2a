# Product overview

This page is the high-level view of the A2A .NET SDK: what it is, what you get, and where to read next. The pages after it go from concepts to architecture to the internals of one request.

## What it is

A .NET SDK for the [Agent2Agent (A2A) protocol](https://a2a-protocol.org/latest/specification/). A2A is the protocol that lets independent AI agents find each other, send each other work, and get results back, without knowing how the other agent is built.

The SDK has a **server** (host an agent), a **client** (call agents) and three **bindings**: JSON-RPC, HTTP+JSON and gRPC. It also has streaming over SSE (Server-Sent Events), push notifications (webhooks the server calls when a task changes), and Agent Card signing.

It targets .NET 10. `A2A.Protocol` and `A2A.Client` also target netstandard2.0 (.NET Framework 4.7.2+). An app on an older .NET gets that build, which has fewer protections: no default Agent Card endpoint policy, no guarded `jku` fetch (`jku` is the key-set URL in a card signature) and no artifact download or upload helpers.

NuGet IDs carry the `RomanAmbinder.` prefix so they never clash with the official `A2A` packages. Assemblies and namespaces are `A2A.*`. On .NET 10 the core packages depend only on `Microsoft.Extensions.*` (the netstandard2.0 build of `A2A.Protocol` and `A2A.Client` adds `System.Text.Json` and `Microsoft.Bcl.*` back-ports); the gRPC packages add `Google.Protobuf` and `Grpc.*`.

## Who it is for

- **You want to expose an agent to other agents or apps.** You write one class that implements <xref:A2A.Server.IAgentExecutor> and register it. The SDK serves the Agent Card, the endpoints, task state, streaming and push. See [Getting started](getting-started.md).
- **You want to call remote agents.** You use `A2AClient`. It picks a binding from the Agent Card, retries safely and keeps credentials on the card's origin. See [Getting started](getting-started.md) and [Client credentials](client-credentials.md).
- **You want to put a model behind an agent, or an agent behind a model.** See [Microsoft.Extensions.AI](extensions-ai.md), [Microsoft Agent Framework](agent-framework.md) and the [MCP bridge](mcp-bridge.md).
- **You run agents in production.** Read [Production checklist](production-checklist.md) first.

## Design priorities

The SDK ranks its goals in this order: **safe > easy > performant > extendable > modular**. When two collide, the higher one wins: secure defaults before a short hello world, a short hello world before speed, and so on. The reasoning, the rules and the non-goals are in the [design principles spec](https://github.com/roman-ambinder/a2a/blob/main/docs/superpowers/specs/2026-11-07-sdk-design-principles-design.md).

What that means in practice, with nothing configured (details in [Getting started](getting-started.md#what-the-defaults-give-you)):

- Anonymous callers are refused. The app fails at startup unless every A2A endpoint has authorization, or <xref:A2A.Server.A2AServerOptions.AllowAnonymous> is true.
- Request size, parts per message, JSON depth, history and page size are capped. Calls are rate limited per tenant and caller (an anonymous caller is counted by its IP address).
- A task belongs to the caller that created it. Other callers get `TaskNotFound`.
- Webhook URLs are checked against private and internal networks, at registration and again at connect time.
- Stores are in memory, which is fine for development and not for production.

## Capability map

| You want to | Read |
|---|---|
| Host and call an agent in minutes | [Getting started](getting-started.md) |
| Choose a transport | [Bindings](bindings.md) |
| Authenticate callers, scope tasks to owners, delegate to other agents | [Authentication and ownership](auth-and-ownership.md), [Client credentials](client-credentials.md) |
| Stream results, and resume a dropped stream | [Streaming and resume](streaming.md) |
| Notify a webhook when a task changes | [Push notifications](push-notifications.md) |
| Let an agent keep working after the request ends | [Proactive continuation](proactive-continuation.md) |
| Prove who published an Agent Card, and who a peer is | [Agent Card signing](agent-card-signing.md), [Peer identity and registry](peer-identity.md) |
| Route to, type and authorize skills | [Skill selection](skill-selection.md), [Typed skills](typed-skills.md), [Skill authorization](skill-authorization.md) |
| Add your own operations or integrate with AI libraries | [Custom operations](custom-operations.md), [Microsoft.Extensions.AI](extensions-ai.md), [Microsoft Agent Framework](agent-framework.md), [MCP bridge](mcp-bridge.md) |
| Serve many agents from one host | [Multi-tenancy](multi-tenancy.md) |
| Survive restarts and run several instances | [Durable stores](durable-stores.md), [Retention](retention.md), [Artifact storage](artifact-storage.md) |
| See what the system is doing | [Observability](observability.md), [Audit trail](audit.md) |
| Bound what a task or a caller can use | [Limits and rate limiting](limits-and-rate-limiting.md), [Execution limits and budgets](execution-limits.md), [Task safety](task-safety.md) |
| Test your agent | [Testing your agent](testing.md) |
| Talk to older or other-vendor peers | [v0.3 compatibility](v03-compatibility.md), [Interop and conformance](interop-and-conformance.md) |

## Reading path

1. [Product overview](overview.md) (this page)
2. [Core concepts](concepts.md): the vocabulary of the protocol.
3. [Architecture](architecture.md): the packages and how they fit together.
4. [Getting started](getting-started.md): run something.
5. The feature guides above, as you need them.
6. [How a request flows](request-lifecycle.md): the deep dive.
7. [Production checklist](production-checklist.md): before you go live.
