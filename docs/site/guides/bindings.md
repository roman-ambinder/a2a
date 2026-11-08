# Bindings

A binding is a way to carry the A2A operations over the network. The SDK has three: JSON-RPC, HTTP+JSON and gRPC. All three run on the same request handler (<xref:A2A.Server.IA2ARequestHandler>), so ownership, tenants, limits, idempotency and errors behave the same. Read this guide to choose a binding and to map it.

## Which binding

| You need | Use |
|---|---|
| The simplest setup, and any A2A client | `MapA2A`: JSON-RPC and HTTP+JSON under one URL |
| One HTTP style only | `MapA2AJsonRpc` or `MapA2AHttpJson` |
| HTTP/2, protobuf messages, gRPC tooling | `MapA2AGrpc` (add it next to the others) |
| Stream resume with `Last-Event-ID` | JSON-RPC or HTTP+JSON. Not gRPC. |
| A header hook on each request (`ConfigureRequest`) | JSON-RPC or HTTP+JSON. gRPC throws. |

You can map all three on one host. The Agent Card lists them, and clients choose.

## Map JSON-RPC and HTTP+JSON

<xref:A2A.Server.AspNetCore.A2AEndpointRouteBuilderExtensions.MapA2A*> maps both HTTP bindings under one base URL:

[!code-csharp[](../snippets/Bindings.cs#MapBoth)]

To map them separately, use `MapA2AJsonRpc` and `MapA2AHttpJson`. These two do not map the Agent Card. Call `MapAgentCard()` on the application root yourself:

[!code-csharp[](../snippets/Bindings.cs#MapSeparately)]

Routes for `MapA2A("/a2a")`:

| Operation | JSON-RPC | HTTP+JSON |
|---|---|---|
| SendMessage | `POST /a2a` | `POST /a2a/message:send` |
| SendStreamingMessage | `POST /a2a` | `POST /a2a/message:stream` |
| ListTasks | `POST /a2a` | `GET /a2a/tasks` |
| GetTask | `POST /a2a` | `GET /a2a/tasks/{id}` |
| CancelTask | `POST /a2a` | `POST /a2a/tasks/{id}:cancel` |
| SubscribeToTask | `POST /a2a` | `GET` or `POST /a2a/tasks/{id}:subscribe` |
| Push config create, list | `POST /a2a` | `POST`, `GET /a2a/tasks/{id}/pushNotificationConfigs` |
| Push config get, delete | `POST /a2a` | `GET`, `DELETE /a2a/tasks/{id}/pushNotificationConfigs/{configId}` |
| GetExtendedAgentCard | `POST /a2a` | `GET /a2a/extendedAgentCard` |

In JSON-RPC the operation is the `method` member of the body. The server does not accept batch requests. Each route also has a `/{tenant}` variant, unless the pattern has a `{tenant}` segment. See [Multi-tenancy](multi-tenancy.md). Extension operations add `POST /a2a/ext/{method}` to HTTP+JSON, and a JSON-RPC method. On gRPC they have their own service, `a2a.extensions.v1.A2AExtensionService` (see [Map gRPC](#map-grpc)).

`MapA2A` takes an <xref:A2A.Server.AspNetCore.A2AEndpointOptions> with `AuthorizationPolicy` and `MapAgentCard`. All Map methods need `AddA2AServerAspNetCore()` (which `AddA2A` calls) and fail at startup without authorization, as described in [Authentication and ownership](auth-and-ownership.md).

## Map gRPC

<xref:A2A.Server.Grpc.A2AGrpcExtensions.AddA2AGrpc*> adds the gRPC services and also calls `AddA2AServerAspNetCore()`. Call it after `AddA2AServer`. <xref:A2A.Server.Grpc.A2AGrpcExtensions.MapA2AGrpc*> maps `lf.a2a.v1.A2AService`. It has no pattern argument.

[!code-csharp[](../snippets/Bindings.cs#GrpcServer)]

`MapA2AGrpc` has the same fail-closed authorization check and the same `"a2a"` rate limit as `MapA2A`. A rate limit rejection is `RESOURCE_EXHAUSTED` with `RetryInfo`.

To serve extension operations over gRPC, also call <xref:A2A.Server.Grpc.A2AGrpcExtensions.MapA2AGrpcExtensions*>. It maps `a2a.extensions.v1.A2AExtensionService`, the same service definition as the official A2A .NET SDK. It is a separate call, so `RequireAuthorization()` on its result applies to this service alone. It has the same fail-closed check and rate limit as `MapA2AGrpc`. The unary `InvokeExtensionOperation` takes `operation_id` (the operation's method) and a `payload` of UTF-8 JSON, and returns a `payload` of UTF-8 JSON. An empty payload means `{}`. An unknown `operation_id` is `MethodNotFound`; a payload that is not JSON is `InvalidParams`. Authorization, errors and the extensions echo work as on HTTP+JSON. The streaming RPC `InvokeStreamingExtensionOperation` is not served and answers UNIMPLEMENTED. `A2AGrpcExtensionService` and `MapA2AGrpcExtensions` are marked `[Experimental]` (`A2AEXP004`), because this wire shape is not in the A2A spec. To use them, add `<NoWarn>A2AEXP004</NoWarn>` to your project. See [Versioning](versioning.md#a2aexp004).

gRPC needs HTTP/2. Over TLS, Kestrel can serve HTTP/1.1 and HTTP/2 on one port. Without TLS, give gRPC a port that allows HTTP/2 only:

[!code-csharp[](../snippets/Bindings.cs#Http2)]

In the card, a gRPC interface URL is the server address, with no path. If the card lists no interfaces, the URL is completed from <xref:A2A.Server.AspNetCore.A2AHttpOptions.PublicBaseUrl>.

## How the client picks

The client reads `SupportedInterfaces` in the card's order. It takes the first entry whose binding it supports and whose protocol major version matches. The card's order wins. So a card that lists gRPC first is a gRPC card for clients that support it:

[!code-csharp[](../snippets/Bindings.cs#CardInterfaces)]

By default, <xref:A2A.Client.A2AClientFactory> chooses between JSON-RPC and HTTP+JSON only. To let gRPC join the choice, call <xref:A2A.Client.Grpc.A2AGrpcClientOptionsExtensions.UseGrpc*> from the `A2A.Client.Grpc` package:

[!code-csharp[](../snippets/Bindings.cs#ClientPicksFromCard)]

To require gRPC, use <xref:A2A.Client.Grpc.A2AGrpcClientFactory>. It throws `UnsupportedOperation` if the card has no usable gRPC interface:

[!code-csharp[](../snippets/Bindings.cs#ClientRequiresGrpc)]

`A2AGrpcClientFactory.Create(card)` does the same for a card you already have. The `HttpClient` is used only to fetch the card. The gRPC channel has its own options.

## What differs on gRPC

| | JSON-RPC and HTTP+JSON | gRPC |
|---|---|---|
| Errors | JSON-RPC error object (HTTP 200 for most errors), or a `google.rpc.Status` body over HTTP+JSON | `google.rpc.Status` with an `ErrorInfo` detail. `retryable` and `retryAfter` are in its metadata. `RetryInfo` is added on rate limits. |
| Request headers | `A2A-Version`, `A2A-Extensions`, `Idempotency-Key` | `a2a-version`, `a2a-extensions`, `idempotency-key` metadata |
| Extensions echo | `A2A-Extensions` response header, also on errors | `a2a-extensions` initial metadata on success; in the trailers when the call fails |
| Extension operations | `POST /ext/{method}`, or a JSON-RPC method | `a2a.extensions.v1.A2AExtensionService`, unary only. Map it with `MapA2AGrpcExtensions`. |
| Tenant | Route segment, or the `tenant` field of the request | The `tenant` field of the request |
| Stream resume | `Last-Event-ID` | Not supported. See [Streaming and resume](streaming.md). |
| `ConfigureRequest` | Supported | Throws `NotSupportedException` |
| Request size | HTTP 413 above `MaxRequestBodyBytes` | `RESOURCE_EXHAUSTED` from the gRPC layer. The client reports it as `InvalidRequest` with reason `PAYLOAD_TOO_LARGE`. |
| Protocol | HTTP/1.1 or HTTP/2 | HTTP/2 |

On gRPC, `A2ARequestOptions.Headers` become metadata. Keys are lowercase on the wire. `Timeout` is the deadline of each unary attempt (default 100 seconds). Streams have no deadline. Retries follow the same rules as the HTTP transports:

[!code-csharp[](../snippets/Bindings.cs#RequestOptions)]

## What can go wrong

- The client uses JSON-RPC although the server maps gRPC: you did not call `UseGrpc()`, or the card lists JSON-RPC first.
- The completed card has no gRPC interface: you mapped it on a route group, or the card already lists interfaces (only an empty list is completed). Declare the gRPC interface in the card yourself.
- `A2AGrpcClientFactory` throws `UnsupportedOperation`: the card declares no GRPC interface for the client's protocol version.
- `MapA2AJsonRpc` or `MapA2AHttpJson` without `MapAgentCard()`: clients cannot fetch the card.

## See also

- [Getting started](getting-started.md)
- [Streaming and resume](streaming.md)
- [Multi-tenancy](multi-tenancy.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Interop and conformance](interop-and-conformance.md)
