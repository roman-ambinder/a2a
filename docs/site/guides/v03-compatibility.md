# v0.3 compatibility

This SDK speaks A2A v1.0. Opt-in packages add protocol v0.3: one lets your server answer v0.3 clients, one lets your client call v0.3-only agents, and two more add v0.3 gRPC to each side. The server and client packages also cover v0.2.x peers over JSON-RPC (see [v0.2.x peers](#v02x-peers)). Read this guide if you must work with agents or clients built on the older protocol. If you only talk to v1.0 peers, skip it.

Nothing changes unless you opt in. A v1.0 method sent without the `A2A-Version` header is still refused (unless `TreatMissingVersionAsCurrent` is on, see [Version detection](#version-detection)).

## When you need it

| You have | Use | Package |
|---|---|---|
| A v1.0 agent that older clients must reach | server opt-in | `RomanAmbinder.A2A.Server.V0_3Compat` |
| A client that must call an agent that only speaks v0.3 | client factory | `RomanAmbinder.A2A.Client.V0_3Compat` |
| The same agent, for v0.3 gRPC clients too | the server opt-in, plus the v0.3 gRPC service | `RomanAmbinder.A2A.Server.Grpc.V0_3Compat` |
| A client that must call a v0.3 agent over gRPC | the client factory, plus `UseGrpcV03()` | `RomanAmbinder.A2A.Client.Grpc.V0_3Compat` |

The shared codecs live in `RomanAmbinder.A2A.V0_3Compat`. It has no public API. It comes in as a dependency of the others.

Both packages use internals of the core packages. All `RomanAmbinder.A2A.*` packages in your app must have the same version. See [What can go wrong](#what-can-go-wrong).

The server and client packages cover JSON-RPC and HTTP+JSON. v0.3 gRPC (service `a2a.v1.A2AService` of the v0.3.0 proto) needs the two gRPC packages. See [Serve v0.3 gRPC clients](#serve-v03-grpc-clients) and [Call v0.3 agents over gRPC](#call-v03-agents-over-grpc).

A runnable version of both sides is in [samples/V03Compat](https://github.com/roman-ambinder/a2a/blob/main/samples/V03Compat/README.md).

## Serve v0.3 clients

Call `EnableV03Compat` inside `MapA2A`:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#Server)]

<xref:A2A.Server.V0_3Compat.A2AV03CompatExtensions.EnableV03Compat*> sets a hook on the <xref:A2A.Server.AspNetCore.A2AEndpointOptions> and returns them. It adds three things.

- **v0.3 JSON-RPC methods on the same URL.** `message/send`, `message/stream`, `tasks/get`, `tasks/cancel`, `tasks/resubscribe` and `agent/getAuthenticatedExtendedCard`. v1.0 methods are untouched.
- **v0.3 HTTP+JSON routes under `{pattern}/v1/...`.** Each route also has a `{pattern}/{tenant}/v1/...` variant, unless the pattern already has a tenant segment.
- **A blended Agent Card** at `/.well-known/agent-card.json`, and at `/.well-known/agent.json`, the path of v0.1 to v0.2.x. See below.

| v0.3 JSON-RPC | v0.3 HTTP+JSON | Runs |
|---|---|---|
| `message/send` | `POST /v1/message:send` | SendMessage |
| `message/stream` | `POST /v1/message:stream` | SendStreamingMessage |
| `tasks/get` | `GET /v1/tasks/{id}` | GetTask |
| none | `GET /v1/tasks` | ListTasks |
| `tasks/cancel` | `POST /v1/tasks/{id}:cancel` | CancelTask |
| `tasks/resubscribe` | `GET` or `POST /v1/tasks/{id}:subscribe` | SubscribeToTask |
| `agent/getAuthenticatedExtendedCard` | `GET /v1/card` | GetExtendedAgentCard |

A tenant named `v1` on a path-tenant host is shadowed by the literal `/v1/...` routes.

`GET /v1/tasks` is the v0.3 REST "list tasks" route (the v0.3 spec lists it for gRPC and REST only, and the v0.3 proto has no such RPC). It answers the caller's tasks as a JSON array of v0.3 `Task`s, newest first, as ListTasks orders them. v0.3 defines no paging or filters for it, so it returns at most 1000 tasks. Ownership and tenancy apply as on a v1.0 ListTasks.

`MapA2AJsonRpc` and `MapA2AHttpJson` have no compat option. Use `MapA2A`.

### Authorization and limits

The routes are mapped into the group `MapA2A` returns. So `RequireAuthorization()` on that group, a policy set in the options, rate limiting and CORS cover them. You do nothing extra:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ServerPolicy)]

The route names start with `A2A `, so the fail-closed startup check from [authentication and ownership](auth-and-ownership.md) sees them. Without authorization the app does not start.

Every v0.3 call goes through the same request handler as a v1.0 call. It gets the same call context, so ownership, tenancy, filters, body size and JSON depth limits are the same code. The tenant comes only from the route. A v0.3 body has no tenant field.

A rate limiter rejection on a v0.3 HTTP+JSON route uses the v0.3 error shape (`{"code": ..., "message": ...}`, with `Retry-After`). v0.3 JSON-RPC shares the v1.0 JSON-RPC endpoint, so its rejection is a JSON-RPC error there.

### Version detection

The shape of the request picks the surface. The `A2A-Version` header can only veto.

| Request | Result |
|---|---|
| JSON-RPC method from the v0.3 table | v0.3 surface |
| HTTP path under `{pattern}/v1/...` | v0.3 surface |
| Either of the above with any `A2A-Version` other than `0.3` or `0.3.x` (header, or query parameter): `1.0`, `0.2`, or a value that does not parse | `VersionNotSupported` |
| gRPC call to the v0.3 service `a2a.v1.A2AService` (with `A2A.Server.Grpc.V0_3Compat`) | v0.3 surface |
| Anything else | v1.0 core, unchanged |

No version means 0.3 (spec v1.0.1 §3.6.2). A version the surface does not serve is refused, not served with v0.3 semantics: "If the version is not supported by the interface, agents MUST return a `VersionNotSupportedError`." A JSON-RPC request without `"jsonrpc": "2.0"` is `InvalidRequest` (`-32600`), on the v0.3 surface and on the v1.0 endpoint.

JSON-RPC shares one URL with v1.0. The wrapper reads the body once, with the normal size limit, and looks at `method`. A v0.3 method is handled. Anything else, and malformed JSON, goes to the v1.0 endpoint, so its answers do not change. A failure to read the body is handed to the v1.0 endpoint too.

`A2AServerOptions.TreatMissingVersionAsCurrent` serves a v1.0 call without the header as 1.0. That breaks the MUST of §3.6.2 ("Agents MUST interpret empty value as 0.3 version"), so outside Development the server logs a warning at startup when it is on (event 709). It does not change the v0.3 surface, which the shape of the request picks.

The method names are claimed before the v1.0 endpoint sees them. If one of your extension operations has a v0.3 method name (or `tasks/list`), `MapA2A` throws at startup and names it. Compare is case-insensitive.

`tasks/list` does not exist in v0.3 JSON-RPC. A JSON-RPC call to it gets `MethodNotFound`. Over HTTP+JSON, `GET /v1/tasks` is served (see above).

### The blended Agent Card

v0.3 has no version negotiation for the card, and v0.3 clients send no header. So the server serves one document: the v1.0 card plus the v0.3 members.

| Added | From |
|---|---|
| `protocolVersion` (`0.3.0`), `url`, `preferredTransport`, `additionalInterfaces` | the `JSONRPC` and `HTTP+JSON` interfaces, and the `GRPC` ones when v0.3 gRPC is served. The first one gives `url`. A tenant on a JSON-RPC or HTTP+JSON interface is appended to the URL |
| `security`, `supportsAuthenticatedExtendedCard` | `securityRequirements`, `capabilities.extendedAgentCard` |
| flat members on each `securitySchemes` entry (`type`, `in`, `scheme`, `flows`, ...) | the v1.0 wrapper object, which stays |
| `security` on each skill | the skill's `securityRequirements` |

`GRPC` interfaces are listed for v0.3 only when the host serves v0.3 gRPC (`AddA2AGrpcV03Compat()` was called). They keep their v1.0 address, because the v0.3 service runs next to the v1.0 one. A `GRPC` interface with a tenant is left out: a gRPC address has no path to carry it, and v0.3 has no tenant field. This holds for the blended card and the extended card. If the card has no interface that v0.3 can list, nothing is added and a warning is logged once.

`capabilities.pushNotifications` is shared by both versions. The blended card keeps the v1.0 value, and v0.3 push is served (see [Push notifications](#push-notifications)), so the value is true for both.

The legacy path `/.well-known/agent.json` (and `/{tenant}/.well-known/agent.json`) serves the same blended card, with the same ETag. Without `EnableV03Compat` it answers 404.

The extended card (`/v1/card` and `agent/getAuthenticatedExtendedCard`) is a pure v0.3 card, not blended. Its `pushNotifications` is the v1.0 value. It has no `signatures`: a signature of the v1.0 extended card covers the v1.0 content, not the v0.3 document served, so it could never verify. The drop is counted with the `field` tag `card.signatures`. `/v1/card` returns the JSON-RPC dialect of the card, as v0.3 servers do. If your extended card lists no interfaces, they are taken from the public card.

### Card signing

The signature of a v1.0 card still verifies. The signing step ignores members outside the v1.0 schema, so the added v0.3 members do not break it. The ETag covers the blended bytes.

But the added v0.3 members are not covered by the signature. A v0.3 client cannot verify the card anyway (see the client side below). See [Agent Card signing](agent-card-signing.md).

### Push notifications

The four push config methods and their HTTP+JSON routes are served, and so is a `pushNotificationConfig` in a send. They run the same core code as v1.0 push, so the URL rules (SSRF guard), ownership, the durable outbox and retries are the same.

| v0.3 JSON-RPC | v0.3 HTTP+JSON | Answer |
|---|---|---|
| `tasks/pushNotificationConfig/set` | `POST /v1/tasks/{id}/pushNotificationConfigs` (`?configId=` optional) | the config |
| `tasks/pushNotificationConfig/get` | `GET /v1/tasks/{id}/pushNotificationConfigs/{configId}` | the config. Without `pushNotificationConfigId`, the task's first config, as the v0.3 Python SDK answers |
| `tasks/pushNotificationConfig/list` | `GET /v1/tasks/{id}/pushNotificationConfigs` | JSON-RPC: every config, at most 1000 (v0.3 has no paging there). HTTP+JSON: `{"configs": [...], "nextPageToken": ...}` |
| `tasks/pushNotificationConfig/delete` | `DELETE /v1/tasks/{id}/pushNotificationConfigs/{configId}` | `null` (JSON-RPC, as the schema says) or `{}`. `pushNotificationConfigId` is required |

A config created over v0.3 is stored with `protocolVersion` `0.3`. Its webhook body is not the v1.0 event. It is the task as it is when the delivery is made, as a v0.3 JSON-RPC `Task` (`"kind": "task"`), with `Content-Type: application/json` and `A2A-Version: 0.3`. The v0.3 docs leave the body open ("The A2A protocol itself does **not** strictly define the HTTP body payload of the push notification"); this is what the official Python SDK 0.3 sends. The history is capped as on GetTask. Since each delivery carries the current task, a retried or late delivery carries a newer state, never an older one. `X-A2A-Notification-Token`, the bearer token and webhook signatures work as for v1.0. A config created by a v1.0 caller on the same task still gets v1.0 bodies.

A stored `0.3` config whose server no longer has the compat package (the encoder is registered by `EnableV03Compat`) is dead-lettered, never sent a v1.0 body.

### What the server does not support

- **JSON-RPC `tasks/list`.** Not in v0.3. The HTTP+JSON route `GET /v1/tasks` is served.
- **ListTasks over v0.3 gRPC.** The v0.3 proto has no such RPC.
- **v0.3 gRPC without `A2A.Server.Grpc.V0_3Compat`.** The v0.3 gRPC service is then not served, and `GRPC` interfaces are not listed for v0.3.
- **v0.1.** Its methods (`tasks/send`, `tasks/sendSubscribe`) do not exist here.

### v0.2.x peers

v0.3.0 only adds to v0.2.6 on the wire: its JSON-RPC methods are a superset of v0.2.x's, and the v0.2.6 to v0.3.0 schema changes are new optional members. So a v0.2.x client is served by the v0.3 surface: it reads the card at `/.well-known/agent.json` and sends no `A2A-Version` header. The v0.2.x extended card endpoint (`GET {url}/../agent/authenticatedExtendedCard`, JSON-RPC `agent/authenticatedExtendedCard`) is not served; v0.3's `agent/getAuthenticatedExtendedCard` is.

On the client, `V03CompatClientFactory` reads a card with `protocolVersion` `0.2` or `0.2.x`, and a card with no `protocolVersion` but a top-level `url` (before v0.2.5 there was none). It keeps only the JSON-RPC interfaces (a missing `preferredTransport` is JSON-RPC, as v0.2.x says), because the v0.2 HTTP+JSON wire was not defined the way v0.3 defines it; each dropped one counts `v0.2.interface`. When `/.well-known/agent-card.json` answers 404, `CreateAsync` tries `/.well-known/agent.json`, unless the URL you passed names a `.json` document. A v0.2.x agent gets the v0.3 methods; calling the extended card on it fails, since v0.2.x has another method for it.

### Errors

JSON-RPC errors keep their numeric code and `message`. The v1.0 `data` member is left out. `-32008` and `-32009` have no v0.3 meaning and are sent as they are. 401 and 403 stay HTTP statuses and keep the auth challenge. Their JSON-RPC body uses code `-32603`, as on v1.0. See [authentication and ownership](auth-and-ownership.md).

HTTP+JSON errors use the core HTTP status and the body `{"code": <number>, "message": "<text>"}`.

v0.3 gRPC errors follow the official Python SDK 0.3: a gRPC status, and a message that starts with the error name, for example `TaskNotFoundError: ...`. The name is what tells apart errors that share a status. The v1.0 errors that v0.3 has no name for get one too (`VersionNotSupportedError`, `UnauthenticatedError`, ...), with the gRPC status of v1.0.

### Blocking sends

v0.3 `configuration.blocking: false` becomes `ReturnImmediately`. If `blocking` is absent, JSON-RPC means blocking. On HTTP+JSON it means non-blocking, because ProtoJSON drops a `false` value and the v0.3 Python client sends none.

On a status update, `final` is `true` for terminal and interrupted states, because the core closes the stream on both.

## Serve v0.3 gRPC clients

`RomanAmbinder.A2A.Server.Grpc.V0_3Compat` adds the v0.3 service `a2a.v1.A2AService` (the proto of tag v0.3.0) next to the v1.0 service `lf.a2a.v1.A2AService`, on the same address. Register it in place of `AddA2AGrpc`, which it also calls:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#GrpcServerServices)]

Then map it next to the v1.0 service:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#GrpcServer)]

<xref:A2A.Server.Grpc.V0_3Compat.A2AGrpcV03CompatExtensions.MapA2AGrpcV03Compat*> throws if `AddA2AGrpcV03Compat()` was not called. Like `MapA2AGrpc`, it needs an authorization policy, or the app does not start. Keep `EnableV03Compat` on `MapA2A`: it serves the v0.3 cards, which then list the `GRPC` interfaces (see [The blended Agent Card](#the-blended-agent-card)).

- **Same request handler.** Each call is set up as on the v0.3 HTTP surface and runs through the same request handler as a v1.0 call. Authentication, ownership, limits and filters are the same. The largest message is the request body limit.
- **All ten v0.3 RPCs.** `SendMessage`, `SendStreamingMessage`, `GetTask`, `CancelTask`, `TaskSubscription`, the four push config RPCs and `GetAgentCard`. The v0.3 proto has no ListTasks.
- **Same mapping as HTTP+JSON.** Each message goes through its ProtoJSON form with the v0.3 HTTP+JSON codec: the same mapping of the same proto. So the HTTP+JSON rows of [What is lossy](#what-is-lossy) apply to gRPC too.
- **Version and extensions.** An `a2a-version` other than `0.3` or `0.3.x` is refused with `VersionNotSupportedError`. No version means 0.3. `x-a2a-extensions` metadata is read as the requested extensions, and the activated ones come back in `x-a2a-extensions` trailers.
- **`GetAgentCard`.** v0.3 gRPC has one card call. An authenticated caller gets the extended card when the tenant's agent offers one. Everyone else, and a caller who is refused the extended card, gets the tenant's public card. The proto card has no `iconUrl` and no `capabilities.stateTransitionHistory`, and its OAuth flows hold one flow, so the others are dropped and counted.
- **`historyLength` 0.** proto3 `int32` has no presence, so `0` means "not set": the whole history, as in the v0.3 Python SDK.
- **Push.** Push configs created over v0.3 gRPC get v0.3 webhook bodies, as over v0.3 HTTP (see [Push notifications](#push-notifications)). `MapA2AGrpcV03Compat` registers the encoder too.

## Call v0.3 agents

Call `V03CompatClientFactory.CreateAsync` instead of `A2AClientFactory.CreateAsync`:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#Client)]

<xref:A2A.Client.V0_3Compat.V03CompatClientFactory> returns a normal <xref:A2A.Client.A2AClient>. Use it as you use any other client. `CreateAsync` takes the same options as the standard factory, plus your <xref:A2A.Client.A2ACardResolverOptions>. It copies them and does not change your object:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ClientResolver)]

If you already have the card, use `Create`. It needs no network call:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ClientFromCard)]

`A2AClientFactory` and a plain `A2ACardResolver` do not read a card older than 1.0 (no `supportedInterfaces`, and a `protocolVersion` of `0.x`, or no `protocolVersion` but a top-level `url`). They refuse it with `InvalidAgentResponse` and an error that names `V03CompatClientFactory`.

### How it picks an interface

1. The card is fetched by the normal card resolver. HTTPS rules, key pinning and text limits apply.
2. A card with a top-level `protocolVersion` of `0.3` or `0.3.x` and no `supportedInterfaces` is converted to the v1.0 model. Its interfaces are marked `0.3`. A v0.2.x card is converted the same way, with its JSON-RPC interfaces only (see [v0.2.x peers](#v02x-peers)). Other `0.x` versions are refused. When the card is not found (404), the v0.2.x path `/.well-known/agent.json` is tried.
3. If the card offers a v1.x interface the client can use, the normal v1.0 path is taken. **A card that offers v1.0 is never downgraded.** This holds for the blended card of this SDK too.
4. Otherwise the first v0.3 `JSONRPC` or `HTTP+JSON` interface in card order is used, or a v0.3 `GRPC` one with `UseGrpcV03()` (see [Call v0.3 agents over gRPC](#call-v03-agents-over-grpc)). The binding must be in `A2AClientOptions.SupportedBindings`.

A card with v1.0 interfaces only on a binding the client cannot use, plus a usable v0.3 interface, falls back to the v0.3 interface.

The v0.3 transports share retries, headers, telemetry and SSE limits with the v1.0 transports. They send `A2A-Version: 0.3`, and `X-A2A-Extensions` in place of `A2A-Extensions`. A stream ends on `final: true` or when the connection ends, whichever comes first. A card that has `supportedInterfaces` (v1.0, or the blended card) is not converted.

### Signed cards

A v0.3-only card is read as unsigned. The `signatures` member is removed before verification, because v0.3 signatures cover v0.3 content. A v0.3 signature is never reported as verified.

So a client that requires card signatures refuses a v0.3-only agent. `RequireSignature` is on by default once you pin keys. See [Agent Card signing](agent-card-signing.md). You can turn it off for one resolver, at the price of trusting an unsigned card:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ClientUnsigned)]

### ListTasks

v0.3 JSON-RPC has no `tasks/list`, so `ListTasksAsync` over v0.3 JSON-RPC throws `UnsupportedOperation`. Over v0.3 HTTP+JSON it calls `GET /v1/tasks`, which answers every task of the caller as an array, and applies the request to that array on the client: `contextId`, `status`, `statusTimestampAfter`, `historyLength` and `includeArtifacts` filter it, and `pageSize` pages it. The next page token is an offset into the filtered array (`v03-offset:n`). A token of another form is `InvalidParams`. Each page fetches the list again, so a page may repeat or skip a task that changed in between. The agent must serve the route: this SDK does (see [Serve v0.3 clients](#serve-v03-clients)), the 0.3 Python SDK does not.

### Call v0.3 agents over gRPC

Add `RomanAmbinder.A2A.Client.Grpc.V0_3Compat` and call `UseGrpcV03()` on the options you pass to the factory:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#GrpcClient)]

<xref:A2A.Client.Grpc.V0_3Compat.A2AGrpcV03ClientOptionsExtensions.UseGrpcV03*> adds a transport factory for the v0.3 `GRPC` interfaces of a card, and adds `GRPC` to `SupportedBindings`. The factory then picks them in card order with the v0.3 JSON-RPC and HTTP+JSON ones. A card that offers a v1.x interface still takes the v1.0 path: add `UseGrpc()` for v1.0 gRPC. You can pass `GrpcChannelOptions`, for example an HTTP handler. Channels are pooled per address and channel options, as for the v1.0 gRPC transport.

- The transport sends `a2a-version: 0.3` and `x-a2a-extensions` metadata, then `A2ARequestOptions.Headers`. `A2ARequestOptions.ConfigureRequest` is not supported and throws `NotSupportedException`.
- Errors are read by their name prefix (`TaskNotFoundError: ...`), as the v0.3 Python SDK sends them. Without a known prefix, the status picks the closest A2A error.
- An idempotent call that finds the server unavailable is retried, up to `MaxRetries`. A send, and a push config create or delete, is not retried.
- A stream ends on `final: true` or when the server closes it.
- `ListTasksAsync` throws `UnsupportedOperation`: the v0.3 proto has no ListTasks.
- `GetExtendedAgentCardAsync` calls `GetAgentCard`, the one card call of v0.3 gRPC.
- `GetTaskAsync` with `HistoryLength` 0 returns no history. The server reads 0 as "not set", so the client drops what comes back.
- Cancel request `metadata` cannot be sent (the proto `CancelTaskRequest` has only the task name). It is dropped and counted.

### What the client does not support

- **ListTasks over v0.3 JSON-RPC and gRPC.** It throws `UnsupportedOperation`. Over HTTP+JSON it works (see [ListTasks](#listtasks)).
- **Receiving v0.3 push webhooks.** You can create, get, list and delete push configs on a v0.3 agent. `authentication.schemes[0]` maps to `Scheme`. But `PushNotificationReceiver.HandleAsync` reads v1.0 `StreamResponse` bodies only; a v0.3 agent sends the task (see [Push notifications](#push-notifications)).
- **v0.3 gRPC without `UseGrpcV03()`.** v0.3 `GRPC` interfaces are then never selected.
- **A v0.3 card that has a security scheme of an unknown type.** The card is refused. Dropping the scheme would make it read as open access.

## What is lossy

v0.3 cannot carry everything v1.0 can. Each drop is counted (see [Metrics](#metrics)). v0.3 gRPC uses the HTTP+JSON mapping, so the HTTP+JSON rows apply to it too.

| What | Direction | What happens | `field` tag |
|---|---|---|---|
| `DataPart` whose data is not a JSON object | v1.0 to JSON-RPC | Wrapped as `{"value": ...}` with part metadata `data_part_compat: true`. A v1.0 reader unwraps it | none |
| `DataPart` whose data is not a JSON object | v1.0 to HTTP+JSON | Wrapped as `{"value": ...}`, no marker (the proto part has no metadata). It cannot be unwrapped on read | none |
| `filename` on a text or data part | v1.0 to v0.3 | Dropped | `part.filename` |
| `mediaType` on a text or data part | v1.0 to v0.3 | Dropped, unless it is the default (`text/plain`, `application/json`) | `part.mediaType` |
| `filename` on a file part | v1.0 to HTTP+JSON | Dropped. JSON-RPC keeps it as `name` | `part.filename` |
| Part `metadata` | v1.0 to HTTP+JSON | Dropped. JSON-RPC keeps it | `part.metadata` |
| `referenceTaskIds` on a message | v1.0 to HTTP+JSON | Dropped. JSON-RPC keeps it | `message.referenceTaskIds` |
| `Tenant` on a request | v1.0 to v0.3 | Not sent. Only the URL carries the tenant | `request.tenant` |
| `metadata` of `tasks/get` and `tasks/resubscribe` params | v0.3 to v1.0 | Dropped | `tasks/get.metadata`, `tasks/resubscribe.metadata` |
| `metadata` of push config get, list and delete params | v0.3 to v1.0 | Dropped | `tasks/pushNotificationConfig.metadata` |
| `metadata` of a cancel request | v1.0 to HTTP+JSON and gRPC (client) | Dropped. JSON-RPC sends it | `tasks/cancel.metadata` |
| Interfaces other than JSON-RPC on a v0.2.x card | v0.2 to v1.0 card | Dropped | `v0.2.interface` |
| `authentication.schemes` after the first | v0.3 to v1.0 | Dropped | `pushNotificationConfig.authentication.schemes` |
| OAuth `deviceCode` flow | v1.0 to v0.3 card | Dropped | `oauth.deviceCode` |
| OAuth `pkceRequired` | v1.0 to v0.3 card | Dropped | `oauth.pkceRequired` |
| `capabilities.stateTransitionHistory` | v0.3 to v1.0 card | Dropped | `capabilities.stateTransitionHistory` |
| `signatures` | v0.3 to v1.0 card | Dropped, so the card reads as unsigned | `card.signatures` |
| `signatures` | v1.0 to v0.3 extended card | Dropped: they cover the v1.0 content, not the v0.3 card | `card.signatures` |
| `iconUrl` | v0.3 card to gRPC `GetAgentCard` | Dropped: the proto card has none | `card.iconUrl` |
| `capabilities.stateTransitionHistory` | v0.3 card to gRPC `GetAgentCard` | Dropped: the proto card has none | `capabilities.stateTransitionHistory` |
| OAuth flows after the first | v0.3 card to gRPC `GetAgentCard` | Dropped: the proto `OAuthFlows` holds one flow | `oauth.flows` |

Card security is not lost. All five scheme types, OAuth flows, scopes, `oauth2MetadataUrl` and the `security` lists of the card and of each skill cross over, in both directions. A scheme left with no flow stays listed with empty `flows`, and its `security` entry stays. A v0.3 client then cannot authenticate and fails. It never reads as anonymous access. The one exception is a v1.0 scheme of an unknown type (from a newer protocol version). It cannot be written as v0.3, so it is left out of the v0.3 members and a warning is logged.

The two v0.3 dialects also look different on the wire. Both are handled for you:

| | JSON-RPC | HTTP+JSON |
|---|---|---|
| Parts | `{"kind": "text", "text": ...}` | `{"text": ...}` |
| States | `input-required`, `canceled` | `TASK_STATE_INPUT_REQUIRED`, `TASK_STATE_CANCELLED` |
| Roles | `user`, `agent` | `ROLE_USER`, `ROLE_AGENT` |
| Unary result | the tagged object | `{"task": ...}` or `{"message": ...}` |

Readers accept both `CANCELED` and `CANCELLED`. A JSON-RPC part with no `kind` is inferred from its members. The state `unknown` reads as `Unspecified`. An unknown state or part kind is an error.

## Metrics

Both counters are on the meter named `A2A`.

| Counter | Tags | Counts |
|---|---|---|
| `a2a.compat.v0_3.dropped` | `field` (see the table above) | values dropped because v0.3 cannot carry them. Counted on the server and on the client |
| `a2a.compat.v0_3.requests` | `binding` (`JSONRPC`, `HTTP+JSON` or `GRPC`), `operation` | requests served on the v0.3 surface. Counted on the server only |

`operation` is the v0.3 method for JSON-RPC (`message/send`). For HTTP+JSON it is `message:send`, `message:stream`, `tasks/get`, `tasks/list`, `tasks/cancel`, `tasks/resubscribe`, `card` or `tasks/pushNotificationConfig`. For gRPC it is the RPC name (`SendMessage`, `TaskSubscription`, `GetAgentCard`, ...).

Watch `requests` to see when v0.3 traffic stops. Then you can remove the opt-in.

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#Metrics)]

Spec A.2 asks for a warning when legacy objects are received, so these are logged at Warning, each once:

- each dropped field (event 1600);
- each card conversion problem, on first use, not at startup (event 1601);
- each v0.3 operation the server answers, per binding (event 1602);
- each v0.3 agent URL a client talks to (event 1603).

The server logs through its logger factory. A client logs only after `V03CompatClientFactory.UseLogger(logger)`. The first logger set in a process is used for all of these.

## What can go wrong

- **`EnableV03Compat` or `V03CompatClientFactory` throws "needs the same version of ...".** The compat packages use core internals. Update all `RomanAmbinder.A2A.*` packages to one version. The check runs at startup on the server, and on every `Create` and `CreateAsync` on the client.
- **`MapA2A` throws and names an extension operation.** Its name is a v0.3 method name or `tasks/list`. Rename it, or do not enable compat.
- **A v0.3 client gets `VersionNotSupported`.** It sent an `A2A-Version` other than `0.3` or `0.3.x` (for example `1.0` or `0.2`) to a v0.3 method or route. Send `0.3`, or no version.
- **A client gets `-32600` "Invalid Request".** Its JSON-RPC body has no `"jsonrpc": "2.0"`.
- **A v0.3 webhook receives a task, not an event.** That is the v0.3 body (see [Push notifications](#push-notifications)). A config made by a v1.0 caller gets v1.0 events.
- **The client throws `UnsupportedOperation` with "No supported interface".** The card has no v1.x interface and no v0.3 `JSONRPC` or `HTTP+JSON` interface that is in `SupportedBindings`. For a v0.3 `GRPC` interface, call `UseGrpcV03()`.
- **`A2AClientFactory` refuses a card as "older than 1.0".** It is a v0.2 or v0.3 card. Use `V03CompatClientFactory`.
- **`ListTasksAsync` throws `UnsupportedOperation` on a v0.3 agent.** The client picked JSON-RPC or gRPC, which have no ListTasks in v0.3. Over HTTP+JSON it works if the agent serves `GET /v1/tasks`.
- **`MapA2AGrpcV03Compat` throws "Call services.AddA2AGrpcV03Compat() ...".** Register the service first.
- **The v0.3 card lists no `GRPC` interface.** `AddA2AGrpcV03Compat()` was not called, or the interface has a tenant.
- **The server logs warning 709 at startup.** `TreatMissingVersionAsCurrent` is on outside Development. Turn it off there.
- **The client refuses a v0.3 card as unsigned.** Expected when you require signatures. A v0.3 card can never be verified.
- **The client refuses a card with "Only 0.2.x, 0.3.x and 1.x are supported".** The agent speaks v0.1 or another 0.x version.
- **A file name or part metadata is missing on the v0.3 side.** You used HTTP+JSON, which cannot carry it. Check `a2a.compat.v0_3.dropped`.
- **A v0.3 client cannot authenticate.** The card has a scheme that v0.3 cannot express, for example only a `deviceCode` OAuth flow. This is on purpose.
- **A tenant named `v1` stops working.** The `/v1/...` routes shadow it.

## See also

- [Bindings](bindings.md)
- [v0.3 compat sample](https://github.com/roman-ambinder/a2a/blob/main/samples/V03Compat/README.md)
- [Agent Card signing](agent-card-signing.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Observability](observability.md)
- [Interop and conformance](interop-and-conformance.md)
- [Design spec](https://github.com/roman-ambinder/a2a/blob/main/docs/superpowers/specs/2026-11-08-v03-compat-design.md)
- [v0.3 interop tests](https://github.com/roman-ambinder/a2a/blob/main/interop/v03/README.md)
