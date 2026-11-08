# A2A v0.3 compatibility layer

Date: 2026-11-08. Status: approved for implementation.

## Goal

Opt-in v0.3 support, so that:

- a server built with this SDK can also serve v0.3 clients;
- the client can talk to v0.3-only agents.

Nothing changes unless the host or caller opts in. A missing `A2A-Version` header on a v1.0 method is still refused.

## Scope

In scope: JSON-RPC and HTTP+JSON, on both server and client.

Out of scope:

- **gRPC.** v0.3 uses a separate proto package and service. Not cheap. v0.3 gRPC interfaces are never advertised and never selected.
- **v0.3 push on the server.** v0.3 webhooks expect a `Task` body; the delivery pipeline sends a v1.0 `StreamResponse` and has no per-config format. Sending the wrong shape silently is worse than refusing. The server answers `-32003` (see Agent Card for what the card says). This matches the official .NET compat layer. Follow-up.
- **Parsing v0.3 webhook bodies on the client.** The client can manage push configs on a v0.3 agent, but `PushNotificationReceiver` still reads v1.0 bodies. Follow-up.
- **v0.2 and older.** Only `0.3.x` is accepted.
- **Verifying signatures on v0.3-only cards.** See Security.

## Package layout

Mirrors `A2A.Grpc` / `A2A.Client.Grpc` / `A2A.Server.Grpc`. Client apps do not pull in ASP.NET Core.

| Project | Package | Holds | References |
|---|---|---|---|
| `src/A2A.V0_3Compat` | `RomanAmbinder.A2A.V0_3Compat` | Wire codecs, card conversion, lossy reporting | `A2A.Protocol` |
| `src/A2A.Client.V0_3Compat` | `RomanAmbinder.A2A.Client.V0_3Compat` | v0.3 transports, client factory | `A2A.V0_3Compat`, `A2A.Client` |
| `src/A2A.Server.V0_3Compat` | `RomanAmbinder.A2A.Server.V0_3Compat` | Endpoints, JSON-RPC dispatch, blended card | `A2A.V0_3Compat`, `A2A.Server.AspNetCore` |

Tests go in `tests/A2A.V0_3Compat.Tests` (net8.0 and net10.0, like the other test projects).

### Codecs

No v0.3 model classes. Each dialect is a static codec between v1.0 models and `JsonNode`. All new code must stay AOT-safe (`IsAotCompatible` is on).

There are **two v0.3 dialects**, and they differ:

| | JSON-RPC (`V03JsonCodec`) | HTTP+JSON (`V03ProtoJsonCodec`) |
|---|---|---|
| Shape origin | v0.3 JSON schema | v0.3 proto, as ProtoJSON |
| Text part | `{"kind":"text","text":…}` | `{"text":…}` |
| File part | `{"kind":"file","file":{"uri"\|"bytes","mimeType","name"}}` | `{"file":{"fileWithUri"\|"fileWithBytes","mimeType"}}` (no name) |
| Data part | `{"kind":"data","data":{…}}` | `{"data":{"data":{…}}}` |
| State | `input-required`, `canceled`, … | `TASK_STATE_INPUT_REQUIRED`, `TASK_STATE_CANCELLED` (two Ls) |
| Role | `user`, `agent` | `ROLE_USER`, `ROLE_AGENT` |
| Task / message | `kind` tag on each | no `kind` |
| Unary result | the object, tagged | `{"task":…}` or `{"message":…}` |
| Stream event | flat, tagged `status-update`, `artifact-update`, wrapped in a JSON-RPC response | `{"task"\|"message"\|"statusUpdate"\|"artifactUpdate":…}`, no envelope |

Readers accept both `TASK_STATE_CANCELED` and `TASK_STATE_CANCELLED`. A JSON-RPC part with no `kind` is inferred from its members; an unknown `kind` is an invalid-params error. State `unknown` reads as `TaskState.Unspecified`; an unrecognized state is an error.

`status-update` carries `final`. Writers set it `true` for terminal and interrupted states, because the core closes the stream on both. Client readers stop on `final: true` or on stream end, whichever comes first.

## Server

### Opting in

One call, inside `MapA2A`:

```csharp
app.MapA2A("/", o => o.EnableV03Compat()).RequireAuthorization();
```

`EnableV03Compat` is an extension method on `A2AEndpointOptions`. Compat routes are mapped **into the group `MapA2A` returns**, so authorization, rate limiting and CORS on that group cover them with no extra step.

`MapA2AJsonRpc` and `MapA2AHttpJson` do not support compat.

### Core changes (the only ones)

All `internal`, reached through `InternalsVisibleTo`. No new public API in core packages.

- `A2AEndpointOptions`: internal `ProtocolAdapter` property.
- `MapA2A`: when set, wrap the JSON-RPC delegate and let the adapter map extra routes into the group.
- `HttpPlumbing.CreateCallContext`: optional version override.
- `AgentCardEndpoint`: optional body transform (held on `A2AEndpointRegistry`), applied to both the unsigned and signed bodies, so the ETag covers the blended bytes.
- `A2ACardResolverOptions`: internal pre-parse card normalizer.
- `InternalsVisibleTo` lines for the compat assemblies and test project.

Because compat uses internals, `EnableV03Compat` and the client factory compare assembly versions at startup and throw a clear error on a mismatch.

### Version detection

The request's shape picks the surface. The header can only veto.

| Request | Result |
|---|---|
| JSON-RPC method in the v0.3 table (`message/send`, …) | v0.3 surface |
| HTTP path under `{pattern}/v1/…` | v0.3 surface |
| Either of the above with `A2A-Version` major ≥ 1 | `VersionNotSupported` (-32009 / HTTP 400) |
| Anything else | Core, unchanged |

JSON-RPC shares a URL with v1.0, so the wrapper reads the body once (through the existing bounded `ReadBodyAsync`), finds `method`, and either handles it or passes the buffered body to the core endpoint. Malformed JSON goes to the core so its error is unchanged. Only hosts that opt in pay for this.

On the v0.3 surface, the call context gets `ProtocolVersion = "1.0"`, because the converted request really is a v1.0 call. `ValidateCall` still runs, so authentication and required-extension checks apply. `X-A2A-Extensions` is copied to `A2A-Extensions` when that header is absent.

A tenant named `v1` on a path-tenant host is shadowed by the literal `/v1/…` routes when compat is on. Documented.

### Operations

| v0.3 JSON-RPC | v0.3 REST | Core call |
|---|---|---|
| `message/send` | `POST /v1/message:send` | SendMessage |
| `message/stream` | `POST /v1/message:stream` | SendStreamingMessage |
| `tasks/get` | `GET /v1/tasks/{id}` | GetTask |
| `tasks/cancel` | `POST /v1/tasks/{id}:cancel` | CancelTask |
| `tasks/resubscribe` | `GET\|POST /v1/tasks/{id}:subscribe` | SubscribeToTask |
| `agent/getAuthenticatedExtendedCard` | `GET /v1/card` | GetExtendedAgentCard |
| `tasks/pushNotificationConfig/{set,get,list,delete}` | `/v1/tasks/{id}/pushNotificationConfigs…` | Refused: `-32003` |

- `configuration.blocking == false` becomes `ReturnImmediately = true`. Absent means blocking.
- `configuration.pushNotificationConfig` on a send is refused with `-32003`.
- `/v1/card` returns the JSON-RPC-dialect card, not ProtoJSON. v0.3 servers do this; a2a-python #1418 is the bug from assuming otherwise.
- `tasks/list` does not exist in v0.3: `-32601`.
- Each route also gets a `/{tenant}` variant unless the pattern has a tenant segment, as `RestEndpoint` does.

### Errors

JSON-RPC: same numeric codes, `message` kept, v1.0 `data` (ErrorInfo array) omitted. `-32008` and `-32009` have no v0.3 equivalent and are sent as they are. 401/403 stay HTTP statuses and keep the auth challenge.

REST: core HTTP status, body `{"code":<number>,"message":<text>}`. The client reads `code` when present and falls back to the status.

### Agent Card: one blended document

`/.well-known/agent-card.json` has no version negotiation in v0.3, and v0.3 clients send no header. So compat serves one document: the v1.0 card plus the v0.3 fields. The same approach is used by a2a-python and by default in a2a-dotnet.

Added at the top level: `protocolVersion` (`"0.3.0"`), `url`, `preferredTransport`, `additionalInterfaces`, `security`, `supportsAuthenticatedExtendedCard`. Each `securitySchemes` entry gains the flat v0.3 members next to the v1.0 wrapper. Each skill gains `security`.

`capabilities.pushNotifications` is shared by both versions, and a blended document cannot hold two values. The v1.0 value stays, so a host with v1.0 push still says `true`. A v0.3 client that tries push gets `-32003`. `/v1/card` and `agent/getAuthenticatedExtendedCard` are v0.3-only responses, so they say `false`. Documented.

Interfaces: every v1.x `JSONRPC` or `HTTP+JSON` interface maps to a v0.3 entry at the same URL (with `/{tenant}` appended when the interface has a tenant). `GRPC` is never listed. `url` and `preferredTransport` come from the first, and `additionalInterfaces` lists all. No such interface means no v0.3 fields are added, and a warning is logged.

**Card conversion must not lose security** (a2a-go #556, a2a-python #1418). Both the public card and the extended card go through the same converter, in both directions:

- all five scheme types with descriptions, OAuth flows, `oauth2MetadataUrl`, and scopes;
- `security` at card and skill level.

`deviceCode` has no v0.3 form. It is dropped and reported. A scheme left with no flows **stays listed with empty `flows`**, and the `security` entry naming it stays. A v0.3 client then cannot authenticate and fails. It must never read as anonymous access.

## Client

```csharp
var client = await V03CompatClientFactory.CreateAsync(agentUrl, httpClient, options, resolverOptions, ct);
```

Returns a normal `A2AClient`. Calling this factory is the opt-in; `A2AClientFactory` is unchanged.

Selection:

1. Resolve the card through `A2ACardResolver`, with the normalizer set on a copy of the caller's resolver options.
2. A card with a top-level `protocolVersion` of `0.3.x` and no `supportedInterfaces` is converted to a v1.0 model, with interfaces marked `0.3`. Other `0.x` versions are refused.
3. If `A2AClientFactory.SelectInterface` finds a v1.x interface, use the normal v1.0 path. **A card that offers v1.0 is never downgraded.**
4. Otherwise pick the first v0.3 `JSONRPC` or `HTTP+JSON` interface in the supported bindings, in card order.

Transports derive from `HttpTransportBase`, so retries, headers, telemetry and SSE limits are shared. They send `A2A-Version: 0.3` and `X-A2A-Extensions`.

`ListTasks` throws `UnsupportedOperation`. Push config operations convert: `authentication.schemes[0]` maps to `Scheme`.

## Lossy mappings

| Field | Direction | What happens |
|---|---|---|
| Non-object `DataPart.data` | v1 → v0.3 | Wrapped as `{"value":…}` with metadata `data_part_compat: true`; unwrapped on read (same as a2a-python) |
| `filename`/`mediaType` on a text or data part | v1 → v0.3 | Dropped |
| `filename` on a file part | v1 → REST v0.3 | Dropped (no such proto field) |
| `metadata` on `tasks/get`, resubscribe | v0.3 → v1 | Dropped |
| `authentication.schemes` beyond the first | v0.3 → v1 | Dropped |
| OAuth `deviceCode`, `pkceRequired` | v1 → v0.3 | Dropped |
| `stateTransitionHistory` | v0.3 → v1 | Dropped |
| `Tenant` on a request | v1 → v0.3 | Not sent; only the URL carries it |
| `ListTasks` | both | Unavailable |

Reporting: a `V03LossReporter` increments the counter `a2a.compat.v0_3.dropped` (tag `field`, meter `A2A`) and logs once per field at Debug. Card conversion problems log at Warning once at startup. Requests are counted by `a2a.compat.v0_3.requests` (tags `binding`, `operation`) so operators can see when v0.3 traffic has stopped.

## Security

The compat path must not be weaker than v1.0.

- Routes sit in the `MapA2A` group, so the authorization policy, rate limits and startup check cover them. Names keep the `A2A ` prefix the startup check looks for. A test asserts an unauthenticated v0.3 call gets 401 on every route.
- Every operation goes through `IA2ARequestHandler` with a context from `CreateCallContext`, so ownership, tenancy and filters are the same code.
- A tenant comes only from the route. v0.3 bodies have no tenant field, and none is read.
- Body size and JSON depth limits use the same plumbing. v0.3 parsing uses `A2AJson.MaxJsonDepth`.
- Client: same resolver, so HTTPS, pinning and text limits apply. No downgrade when v1.0 is offered.
- **Signatures.** The canonicalizer drops members outside the v1.0 schema, so the blended card still verifies for v1.0 clients. The added v0.3 fields are not covered by the signature. On a v0.3-only card the `signatures` member is removed before verification, so the card reads as unsigned, and a client that requires signatures refuses it. v0.3 signatures are never reported as verified.

## Testing

Unit (`tests/A2A.V0_3Compat.Tests`):

- Round trips for both dialects: every part type, states, roles, tasks, messages, events.
- Card conversion both ways, with each scheme type, each flow, card and skill `security`; deviceCode-only stays listed; blended card parses in the v1.0 model and its signature still verifies.
- Golden JSON for v0.3 wire shapes, taken from the v0.3 schema.
- Server via `TestHost`: every method and route, stream shapes, version veto, push refusal, v1.0 methods unchanged with compat on and off, tenant routes, unauthenticated 401 on every route, body limit.
- Client: selection, no downgrade, transports against a fake v0.3 server, `final` handling, signed v0.3 card refused when signatures are required.

Interop (`interop/v03/run.sh`, separate venv, `a2a-sdk==0.3.26`), JSON-RPC and HTTP+JSON, same scenarios as `interop/run.sh`:

- Python 0.3 client → .NET agent with compat.
- .NET compat client → Python 0.3 agent.
- Python 1.x client → the same compat agent (blended card and v1.0 path still work).

`interop/run.sh` is not changed and stays green.

## Sources

- v0.3 schema and proto: `a2aproject/A2A` tag `v0.3.0`.
- v1.0 changes: a2a-protocol.org "What's new in v1".
- Compat layers: `a2a-python` `src/a2a/compat/v0_3`, `a2a-dotnet` `src/A2A.V0_3Compat`.
- Pitfalls: a2a-go #556, a2a-python #1418, a2a-samples #476.
