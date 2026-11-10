# v0.3 compatibility

This SDK speaks A2A v1.0. Two opt-in packages add protocol v0.3: one lets your server answer v0.3 clients, the other lets your client call v0.3-only agents. Read this guide if you must work with agents or clients built on the older protocol. If you only talk to v1.0 peers, skip it.

Nothing changes unless you opt in. A v1.0 method sent without the `A2A-Version` header is still refused.

## When you need it

| You have | Use | Package |
|---|---|---|
| A v1.0 agent that older clients must reach | server opt-in | `RomanAmbinder.A2A.Server.V0_3Compat` |
| A client that must call an agent that only speaks v0.3 | client factory | `RomanAmbinder.A2A.Client.V0_3Compat` |

The shared codecs live in `RomanAmbinder.A2A.V0_3Compat`. It has no public API. It comes in as a dependency of the other two.

Both packages use internals of the core packages. All `RomanAmbinder.A2A.*` packages in your app must have the same version. See [What can go wrong](#what-can-go-wrong).

Only JSON-RPC and HTTP+JSON are supported. gRPC is not.

## Serve v0.3 clients

Call `EnableV03Compat` inside `MapA2A`:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#Server)]

<xref:A2A.Server.V0_3Compat.A2AV03CompatExtensions.EnableV03Compat*> sets a hook on the <xref:A2A.Server.AspNetCore.A2AEndpointOptions> and returns them. It adds three things.

- **v0.3 JSON-RPC methods on the same URL.** `message/send`, `message/stream`, `tasks/get`, `tasks/cancel`, `tasks/resubscribe` and `agent/getAuthenticatedExtendedCard`. v1.0 methods are untouched.
- **v0.3 HTTP+JSON routes under `{pattern}/v1/...`.** Each route also has a `{pattern}/{tenant}/v1/...` variant, unless the pattern already has a tenant segment.
- **A blended Agent Card** at `/.well-known/agent-card.json`. See below.

| v0.3 JSON-RPC | v0.3 HTTP+JSON | Runs |
|---|---|---|
| `message/send` | `POST /v1/message:send` | SendMessage |
| `message/stream` | `POST /v1/message:stream` | SendStreamingMessage |
| `tasks/get` | `GET /v1/tasks/{id}` | GetTask |
| `tasks/cancel` | `POST /v1/tasks/{id}:cancel` | CancelTask |
| `tasks/resubscribe` | `GET` or `POST /v1/tasks/{id}:subscribe` | SubscribeToTask |
| `agent/getAuthenticatedExtendedCard` | `GET /v1/card` | GetExtendedAgentCard |

A tenant named `v1` on a path-tenant host is shadowed by the literal `/v1/...` routes.

`MapA2AJsonRpc` and `MapA2AHttpJson` have no compat option. Use `MapA2A`.

### Authorization and limits

The routes are mapped into the group `MapA2A` returns. So `RequireAuthorization()` on that group, a policy set in the options, rate limiting and CORS cover them. You do nothing extra:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ServerPolicy)]

The route names start with `A2A `, so the fail-closed startup check from [authentication and ownership](auth-and-ownership.md) sees them. Without authorization the app does not start.

Every v0.3 call goes through the same request handler as a v1.0 call. It gets the same call context, so ownership, tenancy, filters, body size and JSON depth limits are the same code. The tenant comes only from the route. A v0.3 body has no tenant field.

A rate limiter rejection on a v0.3 route uses the v1.0 error shape, because the limiter picks its format by endpoint name.

### Version detection

The shape of the request picks the surface. The `A2A-Version` header can only veto.

| Request | Result |
|---|---|
| JSON-RPC method from the v0.3 table | v0.3 surface |
| HTTP path under `{pattern}/v1/...` | v0.3 surface |
| Either of the above with `A2A-Version` major 1 or higher (header, or query parameter on HTTP+JSON) | `VersionNotSupported` |
| Anything else | v1.0 core, unchanged |

JSON-RPC shares one URL with v1.0. The wrapper reads the body once, with the normal size limit, and looks at `method`. A v0.3 method is handled. Anything else, and malformed JSON, goes to the v1.0 endpoint, so its answers do not change. A failure to read the body is handed to the v1.0 endpoint too.

The method names are claimed before the v1.0 endpoint sees them. If one of your extension operations has a v0.3 method name (or `tasks/list`), `MapA2A` throws at startup and names it. Compare is case-insensitive.

`tasks/list` does not exist in v0.3. A JSON-RPC call to it gets `MethodNotFound`.

### The blended Agent Card

v0.3 has no version negotiation for the card, and v0.3 clients send no header. So the server serves one document: the v1.0 card plus the v0.3 members.

| Added | From |
|---|---|
| `protocolVersion` (`0.3.0`), `url`, `preferredTransport`, `additionalInterfaces` | the `JSONRPC` and `HTTP+JSON` interfaces. The first one gives `url`. A tenant on an interface is appended to the URL |
| `security`, `supportsAuthenticatedExtendedCard` | `securityRequirements`, `capabilities.extendedAgentCard` |
| flat members on each `securitySchemes` entry (`type`, `in`, `scheme`, `flows`, ...) | the v1.0 wrapper object, which stays |
| `security` on each skill | the skill's `securityRequirements` |

`GRPC` interfaces are never listed for v0.3. If the card has no `JSONRPC` or `HTTP+JSON` interface, nothing is added and a warning is logged once.

`capabilities.pushNotifications` is shared by both versions. The blended card keeps the v1.0 value. So a host with v1.0 push still says `true`, and a v0.3 client that tries push gets an error (see below).

The extended card (`/v1/card` and `agent/getAuthenticatedExtendedCard`) is a pure v0.3 card, not blended. It says `pushNotifications: false`. `/v1/card` returns the JSON-RPC dialect of the card, as v0.3 servers do. If your extended card lists no interfaces, they are taken from the public card.

### Card signing

The signature of a v1.0 card still verifies. The signing step ignores members outside the v1.0 schema, so the added v0.3 members do not break it. The ETag covers the blended bytes.

But the added v0.3 members are not covered by the signature. A v0.3 client cannot verify the card anyway (see the client side below). See [Agent Card signing](agent-card-signing.md).

### What the server does not support

- **Push notifications.** The four `tasks/pushNotificationConfig/*` methods and routes answer `PushNotificationNotSupported` (`-32003`). So does a send with a `pushNotificationConfig`. The push pipeline sends v1.0 bodies, and a v0.3 webhook expects a different shape. Refusing is safer than sending the wrong shape.
- **`tasks/list`.** Not in v0.3.
- **gRPC.** Not served and not advertised.
- **Anything older than v0.3.**

### Errors

JSON-RPC errors keep their numeric code and `message`. The v1.0 `data` member is left out. `-32008` and `-32009` have no v0.3 meaning and are sent as they are. 401 and 403 stay HTTP statuses and keep the auth challenge. Their JSON-RPC body uses code `-32603`, as on v1.0. See [authentication and ownership](auth-and-ownership.md).

HTTP+JSON errors use the core HTTP status and the body `{"code": <number>, "message": "<text>"}`.

### Blocking sends

v0.3 `configuration.blocking: false` becomes `ReturnImmediately`. If `blocking` is absent, JSON-RPC means blocking. On HTTP+JSON it means non-blocking, because ProtoJSON drops a `false` value and the v0.3 Python client sends none.

On a status update, `final` is `true` for terminal and interrupted states, because the core closes the stream on both.

## Call v0.3 agents

Call `V03CompatClientFactory.CreateAsync` instead of `A2AClientFactory.CreateAsync`:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#Client)]

<xref:A2A.Client.V0_3Compat.V03CompatClientFactory> returns a normal <xref:A2A.Client.A2AClient>. Use it as you use any other client. `CreateAsync` takes the same options as the standard factory, plus your <xref:A2A.Client.A2ACardResolverOptions>. It copies them and does not change your object:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ClientResolver)]

If you already have the card, use `Create`. It needs no network call:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ClientFromCard)]

### How it picks an interface

1. The card is fetched by the normal card resolver. HTTPS rules, key pinning and text limits apply.
2. A card with a top-level `protocolVersion` of `0.3` or `0.3.x` and no `supportedInterfaces` is converted to the v1.0 model. Its interfaces are marked `0.3`. Other `0.x` versions are refused.
3. If the card offers a v1.x interface the client can use, the normal v1.0 path is taken. **A card that offers v1.0 is never downgraded.** This holds for the blended card of this SDK too.
4. Otherwise the first v0.3 `JSONRPC` or `HTTP+JSON` interface in card order is used. The binding must be in `A2AClientOptions.SupportedBindings`.

A card with v1.0 interfaces only on a binding the client cannot use, plus a usable v0.3 interface, falls back to the v0.3 interface.

The v0.3 transports share retries, headers, telemetry and SSE limits with the v1.0 transports. They send `A2A-Version: 0.3`, and `X-A2A-Extensions` in place of `A2A-Extensions`. A stream ends on `final: true` or when the connection ends, whichever comes first. A card that has `supportedInterfaces` (v1.0, or the blended card) is not converted.

### Signed cards

A v0.3-only card is read as unsigned. The `signatures` member is removed before verification, because v0.3 signatures cover v0.3 content. A v0.3 signature is never reported as verified.

So a client that requires card signatures refuses a v0.3-only agent. `RequireSignature` is on by default once you pin keys. See [Agent Card signing](agent-card-signing.md). You can turn it off for one resolver, at the price of trusting an unsigned card:

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#ClientUnsigned)]

### What the client does not support

- **`ListTasks`.** It throws `UnsupportedOperation` on both bindings.
- **Receiving v0.3 push webhooks.** You can create, get, list and delete push configs on a v0.3 agent. `authentication.schemes[0]` maps to `Scheme`. But `PushNotificationReceiver` reads v1.0 bodies only.
- **gRPC.** v0.3 gRPC interfaces are never selected.
- **A v0.3 card that has a security scheme of an unknown type.** The card is refused. Dropping the scheme would make it read as open access.

## What is lossy

v0.3 cannot carry everything v1.0 can. Each drop is counted (see [Metrics](#metrics)).

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
| `authentication.schemes` after the first | v0.3 to v1.0 | Dropped | `pushNotificationConfig.authentication.schemes` |
| OAuth `deviceCode` flow | v1.0 to v0.3 card | Dropped | `oauth.deviceCode` |
| OAuth `pkceRequired` | v1.0 to v0.3 card | Dropped | `oauth.pkceRequired` |
| `capabilities.stateTransitionHistory` | v0.3 to v1.0 card | Dropped | `capabilities.stateTransitionHistory` |
| `signatures` | v0.3 to v1.0 card | Dropped, so the card reads as unsigned | `card.signatures` |

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
| `a2a.compat.v0_3.requests` | `binding` (`JSONRPC` or `HTTP+JSON`), `operation` | requests served on the v0.3 surface. Counted on the server only |

`operation` is the v0.3 method for JSON-RPC (`message/send`). For HTTP+JSON it is `message:send`, `message:stream`, `tasks/get`, `tasks/cancel`, `tasks/resubscribe`, `card` or `tasks/pushNotificationConfig`.

Watch `requests` to see when v0.3 traffic stops. Then you can remove the opt-in.

[!code-csharp[](../../../samples/Snippets/V03Compatibility.cs#Metrics)]

Each dropped field is also logged once at Debug on the server. Card conversion problems are logged once each at Warning, on first use, not at startup.

## What can go wrong

- **`EnableV03Compat` or `V03CompatClientFactory` throws "needs the same version of ...".** The compat packages use core internals. Update all `RomanAmbinder.A2A.*` packages to one version. The check runs at startup on the server, and on every `Create` and `CreateAsync` on the client.
- **`MapA2A` throws and names an extension operation.** Its name is a v0.3 method name or `tasks/list`. Rename it, or do not enable compat.
- **A v0.3 client gets `VersionNotSupported`.** It sent `A2A-Version` 1.0 or later to a v0.3 method or route.
- **A v0.3 client gets `-32003` when it uses push.** v0.3 push is not supported. The blended card may still say `pushNotifications: true`, because it keeps the v1.0 value.
- **The client throws `UnsupportedOperation` with "No supported interface".** The card has no v1.x interface and no v0.3 `JSONRPC` or `HTTP+JSON` interface that is in `SupportedBindings`.
- **The client refuses a v0.3 card as unsigned.** Expected when you require signatures. A v0.3 card can never be verified.
- **The client refuses a card with "Only 0.3.x and 1.x are supported".** The agent speaks v0.2 or older.
- **A file name or part metadata is missing on the v0.3 side.** You used HTTP+JSON, which cannot carry it. Check `a2a.compat.v0_3.dropped`.
- **A v0.3 client cannot authenticate.** The card has a scheme that v0.3 cannot express, for example only a `deviceCode` OAuth flow. This is on purpose.
- **A tenant named `v1` stops working.** The `/v1/...` routes shadow it.

## See also

- [Bindings](bindings.md)
- [Agent Card signing](agent-card-signing.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Observability](observability.md)
- [Interop and conformance](interop-and-conformance.md)
- [Design spec](https://github.com/roman-ambinder/a2a/blob/main/docs/superpowers/specs/2026-11-08-v03-compat-design.md)
- [v0.3 interop tests](https://github.com/roman-ambinder/a2a/blob/main/interop/v03/README.md)
