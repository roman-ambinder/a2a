# Custom operations: finish the extension operation feature

Date: 2026-11-09.

## What exists

`A2AServerBuilder.AddExtensionOperation` already registers a named operation. It is served as a JSON-RPC method, as
`POST /ext/{method}`, and over gRPC by `a2a.extensions.v1.A2AExtensionService`. Calls run the operation pipeline (tenant bind,
telemetry, filters, authorization, deny by default). Routes sit in the rate-limited, authorized group. Built-in names are refused.

This work does not build a second API. The official SDK's handles, registry and binding builders solve a problem we do not
have: one call here registers and maps an operation on every binding.

## Gaps

1. **Not tied to negotiation.** A call works without the extension in `A2A-Extensions`. The official SDK refuses it.
2. **Tenant rules are incomplete with per-tenant cards.** `CallValidator` leaves extension checks to the operation, but
   `InvokeExtensionAsync` never resolves the card. So required extensions are not enforced, nothing is activated, and a tenant
   no agent serves still reaches the handler.
3. **`rpc.` names are allowed.** JSON-RPC 2.0 reserves them.
4. **No client helper.**
5. **No typed handler.** Handlers must work with raw `JsonElement`.
6. **No guide.** Only a paragraph in `spec-decisions.md`.

## Decisions

**Activation is required, with no opt-out.** The operation belongs to its extension. After authorization (so a denied caller
learns nothing) and inside the pipeline, the core does this:

1. `RequestCards.RequireAgentAsync(call, ct)`. A tenant no agent serves is `UnsupportedOperation`. With per-tenant cards this also
   enforces required extensions and fills `ActivatedExtensions`.
2. If `call.ActivatedExtensions` lacks the operation's URI: `ExtensionSupportRequired`, metadata `extension = uri`.

The wrapper is built once per operation in the `A2ARequestHandler` constructor, so all bindings get it, gRPC included. The card
lists the URI by itself (`AgentCardProvider`). A host with its own `IAgentCardProvider` must declare it, or the operation cannot be
activated. This fails closed and the guide says so. It changes behaviour of an SDK that has not shipped; add a CHANGELOG line.

**Reserved names live in one place.** Add `A2AProtocol.Methods.IsReserved(string method)`: true for the 11 built-in names
(ignoring case) and for names that start with `rpc.` (ignoring case). netstandard2.0 safe. Use it from
`A2AExtensionOperation.Validate`, `A2AOperationTable` (covers a record registered by hand) and the client helper.

**Typed server overload.** `AddExtensionOperation<TRequest, TResult>(method, extensionUri, JsonTypeInfo<TRequest>,
JsonTypeInfo<TResult>, handler, authorizationPolicy, allow)`, with handler
`Func<A2AOperationContext, TRequest, CancellationToken, ValueTask<TResult>>`. It wraps into the raw handler. Params that do not bind,
or JSON `null`, are `InvalidParams`. `JsonTypeInfo` only, so it stays AOT safe.

**Client.** `IA2AClient` gets no new member (netstandard2.0 has no default interface methods, and it would break third-party
transports). Instead:

- New public interface `IA2AExtensionOperationClient` in `A2A.Client`:
  `Task<JsonElement> InvokeExtensionOperationAsync(string method, JsonElement parameters, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)`.
- `A2AClient`, `JsonRpcTransport`, `HttpJsonTransport` and `GrpcTransport` implement it. `A2AClient` delegates with
  `ClientTelemetry.TraceAsync` and throws `UnsupportedOperation` when its transport does not implement it.
- Static class `ExtensionOperationClientExtensions` on `IA2AClient`:
  - `InvokeExtensionOperationAsync(method, extensionUri, JsonElement parameters, options, ct)`.
  - `InvokeExtensionOperationAsync<TRequest, TResult>(method, extensionUri, request, JsonTypeInfo<TRequest>, JsonTypeInfo<TResult>, options, ct)`.
  - Both refuse a reserved name (`ArgumentException`) and add `extensionUri` to `options.Extensions` once, as `UseElicitation` does,
    documented the same way.
- Retries: `RetryMode.UnsentOnly`. The SDK cannot know the operation is idempotent. `Options.MaxRetries` applies as usual.
- Tenant: the `tenant` string in the params wins, else `Options.Tenant`. HTTP+JSON puts it in the path
  (`Path(tenant, "/ext/" + Esc(method))`). JSON-RPC and gRPC add a `tenant` member to an object that lacks it. A tenant with params
  that are not a JSON object is `InvalidParams` on those two bindings (a silent wrong tenant is worse). Put the shared code in one
  internal helper in `A2A.Client`.

**gRPC: yes, unary.** The server service already exists and inherits the activation check. `GrpcTransport` uses the generated
`A2AExtensionService.A2AExtensionServiceClient` on the same call invoker, sending JSON in `payload`, with its usual headers, retry and error
mapping. An empty response payload is the empty object.

**Streaming: not in this change.** Operations are unary on every binding, as before. The PR says so.

**Owner.** A custom operation has no task of its own. A handler that touches tasks keys them with
`new TaskKey(context.Call.Tenant, context.Call.Principal, taskId)`. The guide shows this; a test proves the context carries both.

## Tests

No fixed delays. Run on net8.0 and net10.0.

- Server, JSON-RPC and HTTP+JSON: no header is `ExtensionSupportRequired` (-32008 / 400) and the handler does not run; header sent runs
  and echoes `A2A-Extensions`; a caller who is denied gets the denial, not the extension error.
- Per-tenant cards: activation comes from the tenant's card; an unserved tenant is `UnsupportedOperation` and the handler does not run; a
  required extension of that tenant is enforced.
- Rate limit: over the limit a custom operation is refused in that binding's format, on JSON-RPC and on HTTP+JSON.
- Names: `rpc.x` and built-ins refused by the builder and by the table; `IsReserved` theory.
- Typed overload: round trip; bad params and `null` are `InvalidParams`.
- gRPC: no header is `ExtensionSupportRequired`; existing tests send the header.
- Client: helper round trip on JSON-RPC, HTTP+JSON and gRPC against a real test host; header added once; tenant on each binding; non-object
  params with a tenant; reserved name; unsupported transport; server error mapped to `A2AException`.

## Docs

New guide `docs/site/guides/custom-operations.md` (in `toc.yml`), code in a `docs/site/snippets` region. Cover: registering raw and typed,
what a call goes through, activation, tenant and owner, names, routes on each binding, gRPC setup, client helper, unary only. Shorten the
`spec-decisions.md` paragraph to link to it. Update `A2AExtensionOperation` remarks.
