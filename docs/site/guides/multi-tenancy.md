# Multi-tenancy

A tenant is a name in the URL, such as `/sales/a2a`. Each tenant can have its own Agent Card and its own agent. One host then serves many agents. You need this when several agents share one process and one database.

## Tenants in routes

Put a `{tenant}` segment in the route pattern. The route value becomes the tenant of the request.

[!code-csharp[](../../../samples/TenantAgent/Program.cs#Map)]

A runnable version, with two tenants and a client for each, is in [samples/TenantAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/TenantAgent/README.md).

- The default <xref:A2A.Server.AspNetCore.ITenantResolver> (<xref:A2A.Server.AspNetCore.RouteTenantResolver>) reads the `tenant` route value. With no such value the tenant is `""`, the default tenant. Tenants compare ordinally, so `Sales` and `sales` differ.
- With a pattern that has no `{tenant}`, JSON-RPC and HTTP+JSON still get a variant with the tenant after the pattern: `POST /a2a/{tenant}` and `/a2a/{tenant}/message:send`.
- Code reads the tenant from <xref:A2A.Server.ServerCallContext.Tenant>. An executor gets it through `context.Call`:

[!code-csharp[](../../../samples/Snippets/MultiTenancy.cs#ReadTenant)]

- A request body can carry a `tenant` field. With no tenant from the route, the server adopts it. With a tenant from the route, a different body tenant is refused with `InvalidParams`.
- To find the tenant some other way (a header, a host name), register your own `ITenantResolver` before `AddA2AServerAspNetCore`.

## Tenants and ownership

The tenant is part of every storage key. A task is addressed by tenant, owner and task id, so one tenant never reads another tenant's tasks, even with the same owner. Contexts and idempotency keys are scoped by tenant too. See [Authentication and ownership](auth-and-ownership.md) for owners.

A tenant in the URL is routing, not authorization. Your authorization handler receives the `ServerCallContext`, so it can check who may use which tenant.

## Several agents in one host

By default one card (`A2AServerOptions.AgentCard`) and one executor serve every tenant. For one agent per tenant, register a <xref:A2A.Server.Agents.TenantCardProvider> and an <xref:A2A.Server.Agents.AgentExecutorResolver>.

[!code-csharp[](../../../samples/Snippets/MultiTenancy.cs#Register)]

The card provider returns a card per tenant, or null when no agent serves the tenant:

[!code-csharp[](../../../samples/TenantAgent/Program.cs#Cards)]

The resolver returns the executor for a tenant:

[!code-csharp[](../../../samples/TenantAgent/Program.cs#Agents)]

Both register as singletons. Register them with <xref:A2A.Server.A2AServerBuilder.AddTenantCardProvider*> and <xref:A2A.Server.A2AServerBuilder.AddAgentExecutorResolver*>. A later call replaces an earlier one.

- The resolver is asked once per execution (each message, each CancelTask). The `services` argument is the scope of that execution, so scoped executors resolve from it. A null result refuses the message with `UnsupportedOperation` before anything is stored. A resolver that throws fails the execution.
- The card provider is asked once per request, and once per execution when the request did not ask already. The retention sweep asks once per status change it commits. It is never asked once per event.
- Background work knows only the tenant: retention, cancel requests from other instances, and push delivery. The resolver and the provider get an <xref:A2A.Server.Agents.AgentRequestScope> with an empty `RouteValues` there. So the tenant alone must identify the agent. Do not rely on route values other than `tenant`.

## Cards per tenant

<xref:A2A.Server.AspNetCore.A2AEndpointRouteBuilderExtensions.MapAgentCard*> serves `/.well-known/agent-card.json` and `/{tenant}/.well-known/agent-card.json`. `MapA2A` calls it for you on the application root. Keys are served at `/.well-known/jwks.json` and `/{tenant}/.well-known/jwks.json` (the path is `A2AHttpOptions.JwksPath`).

- **Signing key.** `GetSigningKeyAsync(tenant)` returns the key that signs the tenant's card. Null means the host key (`A2AHttpOptions.SigningKey`), or no signature when there is none. With no tenant key and no host key, the JWKS path answers 404. See [Agent Card signing](agent-card-signing.md).
- **Extended card.** `GetExtendedCardAsync` is asked only when the tenant's public card sets `capabilities.extendedAgentCard`. Null answers `ExtendedAgentCardNotConfigured`.
- **Interfaces.** The server completes each card the provider returns. It adds extension entries. It fills `SupportedInterfaces` from the mapped bindings only when the card lists none. Bindings mapped at a pattern with a `{` are never listed, so a card for `/{tenant}/a2a` must list its own interfaces, as the sample does. If a card ends up with no interfaces, the card endpoint throws `InvalidOperationException`.
- **Caching.** The server keeps the completed copy, the encoded body and the entity tag while the card instance lives. A provider that returns the same instance per tenant is completed and encoded once. A provider that builds a new card on each call pays that cost each time, so cache inside the provider. The card endpoint sends `Cache-Control: public, max-age=300` (`A2AHttpOptions.AgentCardMaxAge`) and an `ETag`, and answers 304 for a matching `If-None-Match`.
- **Capability checks.** Streaming, push notifications, extensions and the extended card are checked against the card of the request's tenant.

## An unknown tenant

An unknown tenant is one whose card is null.

| Request | Answer |
|---|---|
| Public card | HTTP 404 |
| SendMessage, SendStreamingMessage, ListTasks, GetExtendedAgentCard | `UnsupportedOperation`, "No agent is configured for this tenant." |
| GetTask, CancelTask, SubscribeToTask, push configs | `TaskNotFound`, as for a missing task |

A resolver that returns null for a tenant whose card is not null refuses messages with `UnsupportedOperation`. Keep the two in step.

## What can go wrong

- **Card with no interfaces.** Cards for `{tenant}` routes must list `SupportedInterfaces`. See above.
- **Provider and resolver disagree.** The card says an agent exists, the resolver returns null. Messages are refused.
- **Expensive card building.** Without caching in the provider, every request builds and encodes a card.

## See also

- [Authentication and ownership](auth-and-ownership.md)
- [Agent Card signing](agent-card-signing.md)
- [Retention](retention.md): the sweep that acts on every tenant
