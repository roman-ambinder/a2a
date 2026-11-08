# OpenTelemetry A2A semantic conventions (gap D31)

**Status:** design, 2026-11-08. Gap: D31 in `docs/2026-11-07-a2a-gap-analysis.md`.

## What the conventions say today

The GenAI conventions moved out of `open-telemetry/semantic-conventions` into
[`open-telemetry/semantic-conventions-genai`](https://github.com/open-telemetry/semantic-conventions-genai).
They now have a page for this protocol: `docs/gen-ai/a2a.md`, read at commit `da2b5736d7a3` (2026-11-05).
Its status is **Development**, so names can still change. The repo has no releases. Neither `a2a.md` nor
`client-invoke-agent.md` mentions `OTEL_SEMCONV_STABILITY_OPT_IN`.

Key rules for A2A spans:

- Span name SHOULD be `{a2a.method.name}`. Kind is `CLIENT` or `SERVER`.
- "A2A instrumentations report spans for A2A requests only and SHOULD NOT report telemetry describing higher level GenAI
  agent operations." So **no `gen_ai.operation.name`, no `invoke_agent` span name and no `gen_ai.provider.name`**. The agent
  framework the host runs reports those. If the SDK reported them too, each run would be counted twice.
- Required: `a2a.method.name`.
- Conditionally required: `a2a.message.id`, `a2a.task.id`, `a2a.task.state`, `a2a.tenant`, `error.type`,
  `gen_ai.agent.name`, `gen_ai.agent.description`, `gen_ai.agent.version`, `gen_ai.conversation.id`.
- Recommended: `a2a.message.reference_task_ids`, `a2a.protocol.version`, `server.address`, `server.port`, and on the server
  `client.address`, `client.port`.
- `a2a.task.state` values MUST be the proto names (`TASK_STATE_COMPLETED`). Omit the attribute when a stream carried more
  than one distinct state.
- `error.type` SHOULD be the A2A `ErrorInfo` reason (`TASK_NOT_FOUND`), otherwise the exception's canonical name.
- `gen_ai.conversation.id` is set from the A2A `contextId`.
- `gen_ai.agent.id` is not on the A2A spans.
- The A2A page defines no metrics. `gen_ai.client.invoke_agent.duration` belongs to the higher-level operation.

## Decisions

Nothing is published on NuGet, so names change with no dual emission and no opt-in switch.

| # | Decision | Why |
|---|---|---|
| D1 | Operation span name becomes the bare method (`SendMessage`), client and server. Remove `SpanNames.OperationPrefix` and `SpanNames.Operation`. Extension operations use their method name (a custom value is allowed). | Convention. Also removes the server's precomputed name table. |
| D2 | `Tags.Operation` becomes `a2a.method.name`, on spans and on the `a2a.server.requests` counter. Remove `Tags.RpcSystem` (`rpc.system`). | One name per concept. `a2a` was never a registered `rpc.system`, and the A2A page does not use it. |
| D3 | `Tags.ContextId` becomes `gen_ai.conversation.id`, everywhere `contextId` is tagged. | Convention. |
| D4 | `a2a.task.state` uses proto names on spans and stream events. | MUST in the convention. |
| D5 | `error.type` on A2A errors is `ReasonOverride ?? A2AErrors.Reason(code)`, on spans and the counter. `a2a.error.code` keeps the enum name. Exception names are unchanged. `TimeLimit` and `TaskFailed` stay on run spans. | Convention. `ReasonOverride` is only ever one of two SDK constants, so a peer cannot inject a value. |
| D6 | Add `a2a.message.id` and `a2a.message.reference_task_ids` on `SendMessage` and `SendStreamingMessage`, and set `a2a.task.id` and `gen_ai.conversation.id` from the request message. | Conditionally required / recommended. Ids only. |
| D7 | Add `a2a.protocol.version`: `A2AClientOptions.ProtocolVersion` on the client, `ServerCallContext.ProtocolVersion` on the server when set. | Recommended. |
| D8 | Add `gen_ai.agent.name`, `.description`, `.version` from the Agent Card. Client: `A2AClient.AgentCard`, when present. Server: the card `RequestCards.ResolveAsync` resolved, set from `ServerCallContext.ResolvedCard` when the operation span stops. | Conditionally required. `RequestCards` is the one place that knows the tenant's card, and it works for per-tenant cards. A call refused before the card resolves has no agent tags. |
| D9 | A stream span sets `a2a.task.state` only while one distinct state has been seen. It removes the tag on the second distinct state. Per-event `a2a.task.state` on `a2a.stream.event` stays. | Convention. The final state of a stream is on its last span event. |
| D10 | Keep every `a2a.*` metric name. Keep `a2a.binding`, `a2a.error.code`, `a2a.canceled`, `a2a.outcome`, `a2a.stream.*`, `a2a.push.*`, `a2a.execution.*`, the `a2a.agent.execute` and `a2a.push.deliver` span names. | No convention replaces them. |
| D11 | Keep the C# constant names (`Tags.Operation`, `Tags.ContextId`). Only their values change. | A later convention change touches one string. Smaller diff. |
| D12 | Keep the nested operation span under ASP.NET Core's request span (kind Internal). Do not enrich the transport span. | Deviation from a SHOULD. `A2A.Server` knows no transport, and the operation span also covers the pipeline and the whole stream. Documented. |
| D13 | Do not add `server.address`, `server.port`, `client.address`, `client.port`. | Recommended only. `A2AClient` has no URL, and the HTTP and gRPC child or parent spans carry them. |
| D14 | Do not add `gen_ai.agent.id`. | Not on A2A spans. A card has no stable agent id. |
| D15 | Never tag message text, parts or metadata. Card description is the public card's text, not message content. | Task rule and existing rule. |

## Cost

All new tags run only with a sampled span (`IsAllDataRequested`). With no listener, the client and server paths still return
early at `HasListeners()`. The enum `ToString()` calls become a
dictionary lookup on the proto name, so a listened-to run allocates less.

## Risk

The convention is in Development. All names sit in `A2ATelemetry`, and the docs state the commit they follow.
