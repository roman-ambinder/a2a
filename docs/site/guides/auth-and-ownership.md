# Authentication and ownership

The A2A server does not log callers in. Your ASP.NET Core app does that. The server then ties every task to the caller it sees and to the tenant of the request. Read this guide before you put an agent on a network.

## Protect the endpoints

[!code-csharp[](../snippets/AuthAndOwnership.cs#Protect)]

`MapA2A` returns a route group. Call `.RequireAuthorization()` on it, or set <xref:A2A.Server.AspNetCore.A2AEndpointOptions.AuthorizationPolicy> as above. The Agent Card endpoints are mapped outside the group and stay anonymous.

If A2A endpoints have no authorization, the app does not start. The check is fail closed:

- Every endpoint whose display name starts with `A2A ` (the Agent Card endpoints are skipped) must carry authorization metadata and no `AllowAnonymous` metadata.
- `MapA2A` throws if `AddA2AServerAspNetCore()` was not called, so the check cannot be skipped by mistake.
- If no endpoint exists yet when the host starts, the check runs again on `ApplicationStarted` and stops the app on failure.
- <xref:A2A.Server.A2ARequestHandler> also refuses unauthenticated callers on its own, as a backstop.

## Allow anonymous callers

[!code-csharp[](../snippets/AuthAndOwnership.cs#Anonymous)]

Set <xref:A2A.Server.A2AServerOptions.AllowAnonymous> to skip the check. A warning is logged once at startup. All anonymous callers share one owner, `anonymous`, so they can read each other's tasks. Do not use this in production.

The id `anonymous` is reserved. If an authenticated user resolves to it, the request is refused as unauthenticated.

## How a task is bound to a caller

A task is stored under three values: the tenant, the owner and the task id.

- **Owner.** <xref:A2A.Server.IPrincipalResolver> turns the authenticated user into an owner id. The default, <xref:A2A.Server.DefaultPrincipalResolver>, reads the `NameIdentifier` claim first, then `sub`. If both are empty, the caller is refused as unauthenticated. Anonymous callers get `anonymous`.
- **Tenant.** <xref:A2A.Server.AspNetCore.ITenantResolver> reads the `tenant` route value, or `""` (the default tenant) when the route has none. If the body also has a `tenant` and it differs from the route, the call fails with `InvalidParams`. See [multi-tenancy](multi-tenancy.md).

The tenant never comes from a claim. To use another claim as the owner id, register your own resolver:

[!code-csharp[](../snippets/AuthAndOwnership.cs#Resolver)]

Contexts, idempotency keys and push configs are scoped by the same tenant and owner.

## What other callers see

A caller can only reach its own tasks. For any other task id, the server answers `TaskNotFound` ("The specified task ID does not exist or is not accessible"). It does not say whether the task exists. This also holds when an authorization handler denies a task-scoped call.

## Extra rules: A2AAuthorizationHandler

<xref:A2A.Server.Authorization.A2AAuthorizationHandler> runs in every operation, on every binding, after the tenant is known. Filters at the default stage run after it. Only `A2AFilterStage.BeforeAuthorization` filters run before it. It gets the call (user, owner, tenant), an <xref:A2A.Server.Pipeline.A2AOperationDescriptor> and the request object. Return `false` to deny.

[!code-csharp[](../snippets/AuthAndOwnership.cs#Handler)]

[!code-csharp[](../snippets/AuthAndOwnership.cs#Register)]

- `Kind` is an <xref:A2A.Server.Authorization.A2AOperation>: the 11 built-in operations, or `Extension`. `Name` is the operation name, or the registered name of an extension operation.
- A denied call whose request names a task (GetTask, CancelTask, SubscribeToTask, push config calls, and SendMessage with a `taskId`) is reported as `TaskNotFound`.
- Any other denial (ListTasks, GetExtendedAgentCard, SendMessage without a task, extension operations) is `PermissionDenied`.
- The base class allows every built-in operation and denies extension operations. The handler the server registers by default also allows an extension operation registered with `allow: true`, or whose ASP.NET Core policy succeeded. A handler of your own replaces it, so decide extension operations yourself.
- Open streams call the handler again every <xref:A2A.Server.A2ALimits.StreamReauthInterval> (default 5 minutes). On denial the stream ends. See [limits](limits-and-rate-limiting.md).

## Errors on the wire

| Case | HTTP status | Body |
|---|---|---|
| Not authenticated | 401, with `WWW-Authenticate` | error `UNAUTHENTICATED` |
| Denied | 403 | error `PERMISSION_DENIED` |
| Task not found or not yours | 404 on HTTP+JSON, 200 on JSON-RPC | error `TASK_NOT_FOUND` |

`WWW-Authenticate` carries the first HTTP auth scheme of the Agent Card for the tenant, or `Bearer` when there is none.

HTTP+JSON bodies look like `{"error": {"code": 401, "status": "UNAUTHENTICATED", "message": "...", "details": [...]}}`. JSON-RPC has no error number for auth, so the body uses `-32603` and the `ErrorInfo` reason carries the type:

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "error": {
    "code": -32603,
    "message": "Authentication required",
    "data": [{
      "@type": "type.googleapis.com/google.rpc.ErrorInfo",
      "reason": "UNAUTHENTICATED",
      "domain": "a2a-protocol.org",
      "metadata": { "retryable": "false" }
    }]
  }
}
```

When you use `RequireAuthorization()`, ASP.NET Core's own middleware answers first, with whatever your scheme does on a challenge. The A2A errors above come from the A2A layer: the handler, the backstop and extension policies.

## The client side

With DI, <xref:A2A.Client.A2AClientServiceCollectionExtensions.AddA2AClient*> returns an `IHttpClientBuilder`. Add a handler for credentials:

[!code-csharp[](../snippets/AuthAndOwnership.cs#Client)]

<xref:A2A.Client.A2AClientServiceCollectionExtensions.AddBearerToken*> sets `Authorization: Bearer` on every request. The token provider runs per request, so cache inside it.

A2A clients do not follow redirects. `AddA2AClient` turns off redirects and cookies. A client that follows a redirect can send your credential headers to another host. If a response arrives through a redirect, the client refuses it with `InvalidAgentResponse`. That check runs after the response, so it cannot undo what a redirecting handler already sent. If you pass your own `HttpClient`, turn redirects off:

[!code-csharp[](../snippets/AuthAndOwnership.cs#NoRedirects)]

## What can go wrong

- **The app does not start with "A2A endpoints have no authorization policy".** Add `RequireAuthorization()`, set a policy in `MapA2A`, or set `AllowAnonymous`.
- **Two users see each other's tasks.** Both resolve to the same owner id. Check the claims your scheme issues.
- **Every call is 401 although the token is valid.** The user has neither a `NameIdentifier` nor a `sub` claim. Use a custom resolver.
- **Anonymous callers share one rate limit bucket.** Behind a proxy, see [limits](limits-and-rate-limiting.md).

## See also

- [Multi-tenancy](multi-tenancy.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Task safety](task-safety.md)
