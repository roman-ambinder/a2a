# Custom operations

A custom operation is a method of your own that an agent serves next to the 11 A2A operations. It belongs to an extension. Read this if your agent needs an operation that A2A does not define, or if you call such an agent.

A custom operation is called an extension operation in the API (`AddExtensionOperation`). It is served on every binding with one registration. It is unary: one request, one response. Streaming custom operations are not supported.

## Register an operation

Raw, with the params and the result as `JsonElement`:

[!code-csharp[](../snippets/CustomOperations.cs#Raw)]

Typed, with your own request and result types. Pass `JsonTypeInfo` from a `JsonSerializerContext`, so the call is safe for trimming and native AOT:

[!code-csharp[](../snippets/CustomOperations.cs#Types)]

[!code-csharp[](../snippets/CustomOperations.cs#Typed)]

With the typed overload, params that do not bind to the request type are `InvalidParams`. So is JSON `null` on HTTP+JSON and gRPC. JSON-RPC treats `"params": null` as no params, which is `{}`.

The arguments of <xref:A2A.Server.A2AServerBuilder.AddExtensionOperation*>:

- `method`: the name. Letters, digits, `.`, `_` and `-`.
- `extensionUri`: the absolute URI of the extension the operation belongs to.
- `handler`: runs the call.
- `authorizationPolicy` and `allow`: who may call it. See [Authentication and ownership](auth-and-ownership.md).

## What a call goes through

A call runs the same operation pipeline as a built-in operation: tenant binding, telemetry, your filters, then authorization. Authorization denies by default. The default handler allows a call only with `allow: true`, or when the ASP.NET Core policy succeeds. The routes sit in the same authorized and rate-limited group as the built-in ones.

After authorization, and before your handler, the core does two checks:

1. It resolves the card of the call's tenant. A tenant that no agent serves is `UnsupportedOperation`. With per-tenant cards, this also enforces the required extensions of that tenant.
2. It requires that the call activated the extension of the operation. If it did not, the call fails with `ExtensionSupportRequired` (HTTP 400, JSON-RPC -32008) and the handler does not run.

A caller who is denied by authorization sees the denial, never the extension error, so it learns nothing about the operation.

## Activation

The extension is active for a call when the client sends its URI in `A2A-Extensions` (metadata `a2a-extensions` on gRPC) and the tenant's card lists it. There is no way to turn this off.

The default card provider lists the URI of every registered operation by itself. If you register your own <xref:A2A.Server.IAgentCardProvider>, your card must declare the URI. If it does not, the operation can never be activated and every call fails. This fails closed.

## Tenant and owner

The tenant comes from the route (`/{tenant}`) or from a string `tenant` member in the params. The handler reads it from `context.Call.Tenant`. The caller is `context.Call.Principal`.

A custom operation has no task of its own. A handler that touches tasks keys them with the tenant and the principal of the call:

[!code-csharp[](../snippets/CustomOperations.cs#Owner)]

## Names

A name may not be one of the 11 built-in method names, and may not start with `rpc.`, because JSON-RPC 2.0 reserves that prefix. Both are compared ignoring case. `AddExtensionOperation`, the operation table and the client helper all use <xref:A2A.A2AProtocol.Methods.IsReserved*>. A name is also unique ignoring case, because HTTP routes ignore case.

## Routes on each binding

| Binding | How it is served |
| --- | --- |
| JSON-RPC | the method name, case-sensitive |
| HTTP+JSON | `POST {pattern}/ext/{method}`, with a `/{tenant}` variant |
| gRPC | `a2a.extensions.v1.A2AExtensionService.InvokeExtensionOperation`, with the method as `operation_id` |

An empty body or payload means `{}`.

### gRPC setup

Map the extension service in addition to `MapA2AGrpc`. It is a separate call, so `RequireAuthorization()` applies to it alone. The service is `[Experimental]` (`A2AEXP004`), so add `<NoWarn>A2AEXP004</NoWarn>` to your project. See [Versioning](versioning.md#a2aexp004).

[!code-csharp[](../snippets/CustomOperations.cs#MapGrpc)]

Only the unary call is served. The streaming call answers UNIMPLEMENTED.

## Call an operation from a client

`A2AClient` and the three built-in transports implement <xref:A2A.Client.IA2AExtensionOperationClient>. The helpers in <xref:A2A.Client.ExtensionOperationClientExtensions> add the extension URI to the call options, once, and refuse a reserved name with an `ArgumentException`:

[!code-csharp[](../snippets/CustomOperations.cs#ClientTyped)]

[!code-csharp[](../snippets/CustomOperations.cs#ClientRaw)]

Rules for the client:

- The call options you pass are changed: the URI is added to `Extensions`, like `UseElicitation` does.
- The tenant is the string `tenant` member of the params, else the `Tenant` of the client options. On HTTP+JSON it goes in the path. On JSON-RPC and gRPC the client adds a `tenant` member to the params. If a tenant is set and the params are not a JSON object, the call fails with `InvalidParams` before it is sent.
- The call is retried only when the request was never sent, because the SDK cannot know that your operation is idempotent. `MaxRetries` applies as usual.
- If the client or transport does not implement `IA2AExtensionOperationClient`, the call fails with `UnsupportedOperation`.
- An error of the agent is an <xref:A2A.A2AException>, with the same code on every binding.
- On gRPC, an empty result payload is the empty object.

## See also

- [Bindings](bindings.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Multi-tenancy](multi-tenancy.md)
- [Spec decisions](spec-decisions.md)
