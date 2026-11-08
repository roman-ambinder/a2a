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

<xref:A2A.Client.A2AClientServiceCollectionExtensions.AddBearerToken*> sets `Authorization: Bearer` on every request. The token provider runs per request, so cache inside it. On an `AddA2AClient` builder it refuses to send the token to an origin other than the `AgentUrl` origin.

To let the client fetch tokens and attach API keys or client certificates from the card's security schemes, see [Client credentials](client-credentials.md).

A2A clients do not follow redirects. `AddA2AClient` turns off redirects and cookies. A client that follows a redirect can send your credential headers to another host. If a response arrives through a redirect, the client refuses it with `InvalidAgentResponse`. That check runs after the response, so it cannot undo what a redirecting handler already sent. If you pass your own `HttpClient`, turn redirects off:

[!code-csharp[](../snippets/AuthAndOwnership.cs#NoRedirects)]

## Delegation

An agent often calls another agent for the caller. Two things help: the actor chain says who the agent acts for, and token exchange gives the next agent a narrow token. This follows [A2A#2028](https://github.com/a2aproject/A2A/issues/2028). The issue is open, so the shape may change. The types are in `A2A.Delegation`.

### The actor chain

`metadata["actorChain"]` on a message says who the agent acts for. It has the party the work is for (`origin`) and the agents that passed it on (`actors`, first hop first):

```json
{
  "actorChain": {
    "origin": { "sub": "user:alice@example.com" },
    "actors": [
      { "sub": "agent:coordinator", "scopes": ["repo:read", "ci:trigger"] },
      { "sub": "agent:rebase-worker", "scopes": ["repo:read"] }
    ]
  }
}
```

Each entry has a required `sub`, and an optional `iss` and `scopes`. Fields the SDK does not know, such as `proof_ref`, are kept when you read and write the chain.

Read it with `GetActorChain()`. It returns `null` when there is no chain or the shape is wrong. Start a chain with `ActorChain.Start(origin)`, add your hop with `AppendHop`, and set it on the next message with `SetActorChain`. `AppendHop` returns a new chain. It shares the earlier hops and does not change them.

- The chain is audit, not authorization. The caller writes it, so it proves nothing. Do not grant access because of it. Treat its text as untrusted when you log it or put it in a prompt.
- A hop is an entry in `actors`. The server refuses a chain with more than <xref:A2A.Server.A2ALimits.MaxActorChainHops> hops (default 8) with `InvalidRequest`, before the agent runs and with nothing stored. It counts whenever `actors` is an array. This only stops loops of honest agents. It is a loop brake, not a security boundary: a caller can drop the chain.
- A well-formed chain is echoed on `Task.metadata`: when the task is created, and again when a follow-up message carries one (the latest wins). A follow-up without a chain leaves the echo as it is. A value with the wrong shape is ignored and not echoed.
- The chain is an optional extension, `ActorChain.ExtensionUri`. `ActorChain.CreateExtension()` gives the entry for your card. The server does not add it for you. If the caller activated the extension, a chain with the wrong shape is refused with `InvalidParams`.

### The inbound token

Agent code never gets the caller's token. `ServerCallContext.InboundBearerToken` can be read only inside `A2A.Server`. The server keeps the token only when an <xref:A2A.Delegation.ITokenExchanger> is registered, the caller is authenticated and the header is `Authorization: Bearer <token>`.

- The token lives in memory for the agent run. It is not stored or logged. The run can outlive the HTTP request, and the token can expire before the run ends. A run recovered on another instance has no token.
- To call the next agent, exchange the token and use the result. `ExchangeTokenAsync(audience, scopes)` on the agent context does this. The audience is required. `CanExchangeToken` tells you if it can run. When it is false, the call throws `InvalidOperationException`.
- Do not copy the `Authorization` header from your own `HttpContext`. That forwards the caller's token to the next agent. The client has no access to the inbound token and does not forward it.

### Register an exchanger

[!code-csharp[](../snippets/AuthAndOwnership.cs#Exchanger)]

<xref:A2A.Client.Delegation.OAuthTokenExchanger> never follows redirects. A redirect (a 307 or 308 re-posts the form) would send the client secret and the subject token to another host. The constructor with only options uses a built-in handler that does not follow redirects. To use your own handler (a proxy, or `IHttpMessageHandlerFactory.CreateHandler`), pass it to `new OAuthTokenExchanger(handler, options)`. The constructor throws when a `SocketsHttpHandler` or `HttpClientHandler` in the chain has `AllowAutoRedirect = true`. It cannot check a custom terminal handler, so that one must not follow redirects. A 3xx response is refused.

The exchanger needs an HTTPS token endpoint (loopback may use HTTP) and reads at most 64 KiB of response. A refused exchange throws `TokenExchangeException`. Its message and its `Error` property (the RFC 6749 `error` code) never contain a token or the server's text.

Warning: the server sends the caller's `Authorization: Bearer` value to the exchanger whenever any scheme authenticated the caller. Register an exchanger only when a Bearer handler validates that header on these endpoints. On a host that authenticates with cookies, API keys or mTLS, that header is not checked.

### Call the next agent

[!code-csharp[](../snippets/AuthAndOwnership.cs#Delegate)]

`UseBearerToken(token, workerUrl)` needs the URL of the agent the token was issued for. It throws `A2AException` before sending if the call goes to another origin. It throws `InvalidOperationException` if the token type is not `Bearer`. It cannot stop a redirect, so the client must not follow redirects: `A2AClientFactory.CreateAsync` with credential providers and `AddA2AClient` already guarantee that. If you build the `HttpClient` yourself, use `AllowAutoRedirect = false`.

`AddBearerToken` on a DI client overwrites `Authorization`, so it replaces a token set with `UseBearerToken`. A credential provider that sets `Authorization` (for example the OAuth2 one) does the same. Do not combine them on one client.

## What can go wrong

- **The app does not start with "A2A endpoints have no authorization policy".** Add `RequireAuthorization()`, set a policy in `MapA2A`, or set `AllowAnonymous`.
- **Two users see each other's tasks.** Both resolve to the same owner id. Check the claims your scheme issues.
- **Every call is 401 although the token is valid.** The user has neither a `NameIdentifier` nor a `sub` claim. Use a custom resolver.
- **Anonymous callers share one rate limit bucket.** Behind a proxy, see [limits](limits-and-rate-limiting.md).

## See also

- [Client credentials](client-credentials.md)
- [Multi-tenancy](multi-tenancy.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Task safety](task-safety.md)
