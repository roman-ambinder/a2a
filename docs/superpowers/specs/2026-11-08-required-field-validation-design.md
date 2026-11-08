# Required-field validation before send (gap D29)

Date: 2026-11-08. Status: decided, no reviewer round.
Closes D29 in `docs/2026-11-07-a2a-gap-analysis.md`. The field list comes from `docs/a2a.proto` (`field_behavior = REQUIRED`).

## 1. What exists today

Audit of `main` at `e5b2898`.

- **The brief said `required` has no matches in `src/A2A.Protocol`. It has 16 `required` members**: `Message.MessageId/Role/Parts`, `Artifact.ArtifactId/Parts`, `AgentTask.Id/Status`, `SendMessageRequest.Message`, `TaskArtifactUpdateEvent.Artifact` and the Agent Card members. The gap is the request types with ids, not the model as a whole.
- **`CallValidator` does not check fields.** It checks protocol version, authentication and extensions. Field checks are in `A2A.Validation.A2AValidator`.
- **Inbound is complete.** `A2ARequestHandler` and `TaskStreamer` call `ValidateIncoming`, `RequireId` and `ValidatePushConfig` for every operation that has a required field, in the transport-neutral handler, so all three server bindings get them. No inbound change here.
- **Outbound is partial.** `A2AClient` calls `ValidateOutgoing` on SendMessage and SendStreamingMessage only, and that checks `message`, `messageId`, `parts` and null parts. Everything else goes to the wire. A blank id on HTTP+JSON becomes `GET /tasks/`, a 404 that says nothing about the field.
- `ValidateMessage` builds a `$"{path}.parts[{i}]"` string per part on the happy path. That is an existing allocation on the send path.

## 2. Required list for outgoing requests

| Operation | Field paths in the error |
|---|---|
| SendMessage, SendStreamingMessage | `message`, `message.messageId`, `message.role`, `message.parts`, `message.parts[i]` content, `configuration.taskPushNotificationConfig.url`, `.authentication.scheme` |
| GetTask, CancelTask, SubscribeToTask | `id` |
| CreateTaskPushNotificationConfig | `config.url`, `config.taskId`, `config.authentication.scheme` |
| GetTaskPushNotificationConfig, DeleteTaskPushNotificationConfig | `taskId`, `id` |
| ListTaskPushNotificationConfigs | `taskId` |
| ListTasks, GetExtendedAgentCard | none in the proto; no validator |

Notes on the list:

- `config.taskId` is not marked `REQUIRED` in the proto, but the HTTP route binds it (`/tasks/{task_id}/pushNotificationConfigs`) and a registration without a task means nothing. The SDK server already requires it. It is not required on the config nested in SendMessage, where the task does not exist yet.
- `message.role` fails when `Role.Unspecified`. `required` on `Role` cannot catch this because the default is a valid C# value.
- Part content is the proto `oneof`. Missing means `TextPart.Text` null, `UrlPart.Url` null or `DataPart.Data` an undefined `JsonElement`. An empty string, empty bytes and JSON `null` data are all legal values.
- `messageId` fails only through a direct `ValidateOutgoing` call. `A2AClient` still fills a blank one first.

## 3. Decisions

**D1. Do not add C# `required` to more members.** Reasons:

1. It breaks the wire. `RestEndpoint.CancelTask` deserializes `CancelTaskRequest` from `{}` and sets `Id` from the route afterwards. Source-generated metadata enforces `required` on read, so a `required Id` throws on every REST cancel. gRPC maps through ProtoJSON, which omits empty fields, with the same result. Each member would need an entry in `A2AJson.OptionalOnWire`, as Agent Card already does.
2. It degrades the error. A field that does get enforced reports a `JsonException` about a type, not `id is required`, and the parity suite pins today's behaviour.
3. It catches little. It finds an omitted initializer at compile time, not `Id = ""` or a null variable, so the runtime check is needed anyway. Convenience overloads already take the id as a parameter.
4. Churn: 234 construction sites across `src` and `tests` for those seven types, for that small benefit.

Existing `required` members stay.

**D2. Extend `A2AValidator`, in a new file.** `A2AValidator` becomes `partial`; `Validation/A2AValidator.Outgoing.cs` holds one public `ValidateOutgoing` overload per request type, including the moved SendMessage one. Public because `A2A.Client` may not use internals of `A2A.Protocol`. The existing SendMessage call lines in `A2AClient` do not change, which keeps this clear of the skill-selection work on the send path.

**D3. One call site per operation, in `A2AClient`.** Each `IA2AClient` method on `A2AClient` gets one line, first in the method, before tenant fill. Convenience overloads already funnel into these. Every factory (`A2AClientFactory`, `A2AGrpcClientFactory`, `V03CompatClientFactory`) returns an `A2AClient`, so JSON-RPC, HTTP+JSON, gRPC, v0.3 and third-party `IA2ATransportFactory` transports are all covered. A decorator around the transport was rejected: it hides `IResumableTransport`, and it would re-check internal resume calls whose ids came from the server.

**D4. Transports stay raw.** `new JsonRpcTransport(...)` used directly is not validated. It is the low-level layer; the docs only show the factories, and checking there would double the work on the normal path.

**D5. Presence only.** Outgoing checks refuse only what the spec requires. They do not apply `ValidatePushConfig`'s URL-form and signing-secret rules, which are this SDK's server policy. A foreign server may accept what ours refuses, and refusing locally would be a false failure.

**D6. Failure shape.** `A2AException` with `InvalidParams`, same as the server, thrown synchronously from the call, as the existing check and `RequireCapability` do. Blank means `string.IsNullOrWhiteSpace`, same as the server. Always on; no option. `ObserveTaskAsync` moves from `ArgumentException` to the same error so it matches `SubscribeToTaskAsync`. A null request is `ArgumentNullException`.

**D7. No allocation on the happy path.** No string is built unless a check fails. `ValidateMessage` stops building the per-part path, a net saving on the send benchmark. Pinned by a test using `GC.GetAllocatedBytesForCurrentThread`.

## 4. Out of scope

Roslyn analyzer. Validating responses from agents. Changing inbound behaviour.
