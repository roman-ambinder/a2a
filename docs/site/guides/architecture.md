# Architecture

How the SDK is split into packages, and what the server is made of. Read it to choose packages, or before you replace a part of the server. Each package is a separate NuGet package (ID: `RomanAmbinder.` + the name below).

## Layers

| Layer | Packages | Role |
|---|---|---|
| Protocol | `A2A.Protocol` | The data model (from `a2a.proto`), ProtoJSON serialization, errors, JCS (RFC 8785) and JWS card signing and verification |
| Security | `A2A.Security.Http` | An SSRF-safe outbound HTTP handler (SSRF: a peer makes your server call an internal address; the handler blocks private addresses), guarded `jku` key-set fetching and `SafeHttp` for URLs a peer chooses. The client (on .NET 10) and the push sender share it |
| Server core | `A2A.Server` | Transport-agnostic: the executor model, task lifecycle, stores, event bus, durable push delivery (outbox), execution coordination, retention, tracing and metrics |
| Server bindings | `A2A.Server.AspNetCore`, `A2A.Grpc`, `A2A.Server.Grpc` | JSON-RPC, REST, SSE, the Agent Card and JWKS endpoints, and the gRPC service on ASP.NET Core. `A2A.Grpc` holds the generated protobuf types, which the gRPC client uses too |
| Client | `A2A.Client`, `A2A.Client.DependencyInjection`, `A2A.Client.Grpc`, `A2A.Client.Identity.Azure`, `A2A.Client.Identity.Aws` | `A2AClient`, the JSON-RPC and REST transports, the SSE reader, the card resolver, DI and auth handlers, the gRPC transport, and Azure and AWS caller identities. See [Identity providers](identity-providers.md) |
| Persistence and scale-out | `A2A.Server.Postgres`, `A2A.Server.Redis`, `A2A.Client.Postgres` | Durable stores, a Redis event bus and execution coordinator, and a durable client outbox |
| Artifact storage | `A2A.Server.Storage.S3`, `A2A.Server.Storage.AzureBlob` | Keep large artifact bytes outside the task store |
| Integrations | `A2A.Extensions.AI`, `A2A.Mcp`, `A2A.Server.TypedSkills` | `Microsoft.Extensions.AI`, Model Context Protocol, and typed skills (experimental) |
| Compatibility | `A2A.V0_3Compat`, `A2A.Server.V0_3Compat`, `A2A.Client.V0_3Compat`, `A2A.Server.Grpc.V0_3Compat`, `A2A.Client.Grpc.V0_3Compat` | Let v0.3 (and v0.2.x) clients call a v1.0 agent, and let a client call v0.3 agents, over JSON-RPC, HTTP+JSON and gRPC |
| Tooling | `A2A.Cli`, `A2A.TestKit`, `A2A.Templates` | The `a2a` command line tool, test tools, and the `dotnet new` templates |

All `RomanAmbinder.A2A.*` packages that you use together must have the same version.

## Which packages do I install

| Goal | Packages |
|---|---|
| Host an agent over JSON-RPC and HTTP+JSON | `A2A.Server.AspNetCore` |
| Also host over gRPC | add `A2A.Server.Grpc` |
| Call agents | `A2A.Client` (add `A2A.Client.DependencyInjection` for DI and auth handlers, `A2A.Client.Grpc` for gRPC) |
| Call agents with Azure managed identity or AWS IAM | add `A2A.Client.Identity.Azure` or `A2A.Client.Identity.Aws`. See [Identity providers](identity-providers.md) |
| Host an `IChatClient` as an agent, or use an agent as an AI tool | `A2A.Extensions.AI`. See [Microsoft.Extensions.AI](extensions-ai.md) |
| Bridge to MCP | `A2A.Mcp`. See [MCP bridge](mcp-bridge.md) |
| Give skills .NET input and output types (experimental) | `A2A.Server.TypedSkills`. See [Typed skills](typed-skills.md) |
| Run in production | add `A2A.Server.Postgres`, and `A2A.Server.Redis` for several instances. See [Durable stores](durable-stores.md) |
| Keep large artifacts in object storage | `A2A.Server.Storage.S3` or `A2A.Server.Storage.AzureBlob`. See [Artifact storage](artifact-storage.md) |
| Keep a durable list of the messages your agent sends to peers | `A2A.Client.Postgres`. See [Client outbox](client-outbox.md) |
| Talk to v0.3 agents or clients | `A2A.Client.V0_3Compat` or `A2A.Server.V0_3Compat`; for v0.3 gRPC also `A2A.Client.Grpc.V0_3Compat` or `A2A.Server.Grpc.V0_3Compat`. See [v0.3 compatibility](v03-compatibility.md) |
| Test an agent | `A2A.TestKit`. See [Testing your agent](testing.md) |
| Use the command line | the `RomanAmbinder.A2A.Cli` dotnet tool. See [Command line tool](cli.md) |

## Inside the server

The server core has these parts. Each is a class or a DI registration you can replace. The defaults are safe and in memory.

- **Request handler** (`IA2ARequestHandler`): the one set of operations every binding calls. The JSON-RPC, REST and gRPC bindings are thin adapters over it, so ownership, tenants, limits, idempotency and errors behave the same on all three. A binding of your own resolves it from DI.
- **Operation pipeline**: wraps each operation in filters. See [How a request flows](request-lifecycle.md).
- **Agent executor** (<xref:A2A.Server.IAgentExecutor>): your code. It gets a request context and a <xref:A2A.Server.TaskUpdater> and reports progress through the updater.
- **Execution engine** (`TaskExecutionEngine`): runs executors. It applies each event an agent produces to the task, commits it, and publishes it.
- **Stores**: `TaskStore`, `EventStore`, `PushConfigStore`, `PushOutbox` and `IdempotencyStore`. `TaskStore.CommitAsync` writes the task, its event and its push deliveries as one unit.
- **Event bus** (`TaskEventBus`): fans stored events out to live subscribers.
- **Execution coordinator** (`ExecutionCoordinator`): knows which tasks are running on which instance, delivers cancel requests, and keeps a lease per running execution.
- **Push delivery** (`PushDeliveryService`): a hosted service that sends the push outbox to webhooks.
- **Retention** (`A2ARetentionService`, opt-in): purges old tasks, times out interrupted tasks and fails the tasks of dead executions.
- **Agent Card provider** (`IAgentCardProvider`): supplies the public card and each tenant's completed card.

## Extension seams

Every seam is a DI registration or a builder call. The main ones:

| Seam | Use it to |
|---|---|
| `IA2AOperationFilter` (with `A2AFilterStage`) | Wrap every operation, before or after authorization |
| `A2AAuthorizationHandler` | Decide per operation who may do what |
| `IPrincipalResolver` | Change how a caller is identified for ownership |
| Stores and `ExecutionCoordinator` | Replace in-memory defaults with durable ones |
| `IAgentExecutorFactory`, `AgentExecutorResolver`, `TenantCardProvider` | Choose the executor and the card per execution or tenant |
| `AddExtensionOperation` | Add operations of your own. See [Custom operations](custom-operations.md) |
| `IA2ARejectionWriter` | Write rate limit rejections in a binding's own format |
| Telemetry | Listen to the `A2A.Server` and `A2A.Client` `ActivitySource` and the `A2A.Server` `Meter`. See [Observability](observability.md) |

## Client side

`A2AClient` talks to one agent through a transport: JSON-RPC, REST or gRPC. A factory fetches the Agent Card, checks it, and picks the first interface in the card's order that the client supports. Credential providers are chosen by the security schemes the card requires, and they send credentials only to the origin the card came from, never across a redirect. Unary calls retry on 503, 429, errors marked retryable and transport errors. See [Client credentials](client-credentials.md) and [Client resilience](client-resilience.md).

## Next

[How a request flows](request-lifecycle.md) follows one call through these parts.
