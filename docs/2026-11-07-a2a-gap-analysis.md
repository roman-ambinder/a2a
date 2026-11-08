# A2A .NET SDK: additions beyond the v1.0 spec

Date: 2026-11-07. Scope: what this SDK should add on top of A2A v1.0 (proto in `docs/a2a.proto`) to close known gaps in (a) the A2A specification and (b) the official `a2aproject/a2a-dotnet` SDK. Everything proposed is additive and wire-compatible: other v1.0 peers ignore the extra headers and metadata.

Severity: **High** = breaks or endangers real deployments today. **Med** = needs custom workarounds. **Low** = edge or annoyance.

---

## Part 1: Spec gaps and what the SDK adds

### 1.1 Agent Card trust

| # | Gap | Sev | Evidence | SDK addition |
|---|-----|-----|----------|--------------|
| S1 | JWS verification lets signer pick trust root: key fetched via `kid`+`jku` from signer-controlled protected header. Forged card with attacker JWKS verifies. | High | [A2A#2096](https://github.com/a2aproject/A2A/issues/2096) open; fix PR #2099 pending | `AgentCardVerifier` with explicit trust-anchor policy: `jku`/`x5u` ignored unless host allowlisted; pinned JWKS per issuer; `typ` required. Default = fail closed. |
| S2 | Canonicalization (§8.4.1) under-specified. Spec RFC 8785 summary wrong; SDKs disagree on empty values. Cross-SDK verification fails. | High | [A2A#2122](https://github.com/a2aproject/A2A/issues/2122) 48 comments, no ruling; #2249 | Real RFC 8785 canonicalizer. "Prune empty" reading (matches a2a-python 1.2.1 / a2a-js 1.3.0). Signer refuses cards with empty values. Ship cross-SDK test vectors from a2a-tck. |
| S3 | Signature proves publisher integrity only. Skills/claims unattested; signing MAY, verification SHOULD. | High | A2ABreak [arXiv 2609.10871](https://arxiv.org/html/2609.10871v1) | Card pinning: store hash per peer, alert on change. Policy hook `IAgentCardPolicy` (require signature, require known issuer, require org match). |
| S4 | No agent identity primitive (no stable ID, DID, key binding, revocation). | High | [A2A#1672](https://github.com/a2aproject/A2A/issues/1672) 658 comments open | Peer identity = `(card origin, signing kid)`, never `name`. `IPeerIdentityResolver` abstraction so DID/SPIFFE can plug in later. |
| S5 | Name is display metadata; hosts routing by name are spoofable (6/7 tested impls). | Med | [arXiv 2609.27624](https://arxiv.org/abs/2609.27624) | Client registry keyed by identity (S4); reject duplicate names; warn on homoglyphs. |
| S6 | Prompt injection via card text reaches planner prompt. | High | MAESTRO [arXiv 2504.16902](https://arxiv.org/html/2504.16902v1); Red Hat, Unit 42 | `AgentCardSanitizer`: length caps, control-char strip, optional escaping; "untrusted text" wrapper type so callers cannot accidentally interpolate raw card strings. |

Status: S1 is done (`CardTrustPolicy`: pinned keys, `jku` host allowlist per origin; a key the card points at by itself is never trusted, and signature enforcement starts once trust is configured, see `spec-decisions.md`). S4 is done (`PeerIdentity`, `IPeerIdentityResolver`). S5 is done (`AgentRegistry`: keyed by identity, duplicate names rejected, lookalike warning). Still partial: S2 (the signer warns on empty values instead of refusing), S3 (no `IAgentCardPolicy` hook or org match), S6 (no optional escaping).

### 1.2 Push notifications

| # | Gap | Sev | Evidence | SDK addition |
|---|-----|-----|----------|--------------|
| S7 | Webhook SSRF: URL validation only SHOULD; silent on redirects, DNS rebinding. Three official SDKs shipped vulnerable. | High | CVE-2026-111761 (a2a-java); a2a-go #373; epic [A2A#1988](https://github.com/a2aproject/A2A/issues/1988) | SSRF guard **on by default**: https only, resolve-then-pin IP, block loopback/RFC1918/link-local/CGNAT/ULA/metadata ranges, no redirects, operator allowlist for internal targets. |
| S8 | Get/List push config returns credentials. | High | A2A#1681 → #1988; PR #1750 | Redact `token` and `authentication.credentials` on read. Store secrets via `IDataProtector`/secret store, never plaintext in task store. |
| S9 | `token` type and carrying header unspecified. JS sends `Authorization`, Python sends `X-A2A-Notification-Token`. | Med | A2A#1250 → #1988 | Send both: `Authorization: Bearer <token>` **and** `X-A2A-Notification-Token`. Receiver accepts either. |
| S10 | No body integrity, no delivery ID. Duplicates allowed by spec. | Med | #1988; [discussion #2101](https://github.com/a2aproject/A2A/discussions/2101) | Standard-Webhooks-style headers: `X-A2A-Delivery-Id`, `X-A2A-Notification-Sequence` (already in `A2AProtocol.Headers`), `X-A2A-Signature` HMAC-SHA256 over timestamp+body with per-config secret. Receiver helper dedupes and verifies; always reconciles with `GetTask`. |
| S11 | Delivery guarantee undiscoverable; retry MAY. | Med | #2101 | `IPushNotificationSender` with pluggable retry policy (exp backoff, DLQ hook), delivery log, `IPushNotificationOutbox` for durable sends. |

Status: S7, S8, S9 and S10 are done (`PushNotifications.cs`, `PushConfigService`, `IPushSecretProtector`, `PushNotificationReceiver`; S10 uses Standard Webhooks `webhook-signature` instead of `X-A2A-Signature`, see `spec-decisions.md`). S11 is partial (outbox, dead letters and redrive exist; retry is options only, with no pluggable policy or dead-letter hook).

### 1.3 Authorization and tenancy

| # | Gap | Sev | Evidence | SDK addition |
|---|-----|-----|----------|--------------|
| S12 | Task-ID authorization undefined; default stores are flat dicts keyed by taskId. | High | CVE-2026-111764, CVE-2026-90820 (a2a-java ListTasks); a2a-dotnet PR #450 | Task store keyed `(tenant, principal, taskId)`. Every op (Get/Cancel/Subscribe/List/push-config) checks owner. Foreign task → `TaskNotFound`, never 403. |
| S13 | No context ownership: any client with a `contextId` can inject into another client's context. | High | A2ABreak finding 3; [A2A#2061](https://github.com/a2aproject/A2A/issues/2061) | Context owner recorded at creation. Messages into foreign context rejected unless `IContextSharingPolicy` allows. |
| S14 | `tenant` is opaque; isolation left to implementers. | Med | spec; bifrost PR #8873 | `ITenantResolver` from path/header/claims; tenant is mandatory partition key in all stores and caches. |
| S15 | SSE keeps delivering after auth revoked. | Med | A2ABreak finding 4 | Max stream duration + periodic re-auth check (`IStreamAuthorizationRefresher`); close with explicit event on revocation. |
| S16 | Concurrent `SendMessage` to same task: TOCTOU, no version precondition. | Med | A2ABreak finding 6; A2A#1582 | Optimistic concurrency: task store carries ETag/version; conflicting writes → `InvalidRequest` with retry hint. Per-task async lock in server core. |
| S17 | No on-behalf-of identity; intermediaries forward raw credentials. | High | A2ABreak 8-9; [A2A#2028](https://github.com/a2aproject/A2A/issues/2028) actorChain; Eon prod report | `metadata["actorChain"]` carried per #2028 shape; hop-count limit; `ITokenExchanger` (RFC 8693) to down-scope before forwarding; client never re-forwards inbound bearer by default. |
| S18 | AUTH_REQUIRED approval not bound to exact operation ("loopjacking"). Clarification on main, not in tagged release. | High | [loopjacking write-up](https://adithyanak.com/loopjacking-in-a2a-implementations/); PR #2081 | Approval token = hash of pending operation; re-verified at dispatch. Helper type `PendingApproval` in server core. |
| S19 | Circular delegation deadlock; interrupted states can hang forever. | Med | A2ABreak 10-11 | Configurable TTL for `INPUT_REQUIRED`/`AUTH_REQUIRED` → auto `CANCELED`/`FAILED` with reason; actorChain hop limit (S17). |
| S20 | Rate limiting MAY only; no size limits. | Med | a2a-serve #180 | Defaults: max body, max part, max parts/message, max history returned, max ListTasks page, max concurrent streams/task, per-principal rate limit (ASP.NET Core rate limiter integration). |

Status: S17 is done (actorChain model and echo, `ITokenExchanger`, `OAuthTokenExchanger`, no forwarding of the inbound token; design: `docs/superpowers/specs/2026-11-08-delegation-identity-design.md`). The actorChain hop limit part of S19 is done (`A2ALimits.MaxActorChainHops`); the interrupted-state timeout part was already done. S12, S14, S18 and S20 are done (`TaskKey`, `ITenantResolver`, `PendingApproval`, `A2ALimits` and `A2ARateLimiting`). Still partial: S13 (no `IContextSharingPolicy`), S15 (a revoked stream ends with no explicit event), S16 (no `InvalidRequest` with a retry hint once conflict retries run out).

### 1.4 Messaging and task semantics

| # | Gap | Sev | Evidence | SDK addition |
|---|-----|-----|----------|--------------|
| S21 | SendMessage idempotency optional; 20/20 duplicate tasks on retry in a2a-sdk; first-message hole. | High | epic [A2A#1987](https://github.com/a2aproject/A2A/issues/1987); #928, #1893 | Server: honour `Idempotency-Key` header and dedupe `messageId` per principal; key→result stored ≥24h; same result on retry. Client: persist key before send; recover via `ListTasks(contextId)`. |
| S22 | Stream resumption: no sequence, no cursor; SSE reconnect loses events. | High | epic [A2A#1986](https://github.com/a2aproject/A2A/issues/1986); PR #2411 (dev-1.1 `last_seen_generation`) | SSE `id:` = per-task monotonic sequence; honour `Last-Event-ID`; bounded replay buffer (`IEventStore`); unknown cursor → fresh Task snapshot. Client auto-reconnect with cursor, `GetTask` reconcile. |
| S23 | Which states accept follow-ups undefined in 1.0 (steering while WORKING). | High | epic #1992 closed, dev-1.1 only | Configurable `FollowUpPolicy`: `InputRequiredOnly` (strict 1.0) or `AllowSteering` (1.1 default). Explicit error code when rejected. |
| S24 | Non-blocking `returnImmediately` ignored silently; no capability flag. | High | [discussion #2277](https://github.com/a2aproject/A2A/discussions/2277); Eon report | Server always honours it or returns `UnsupportedOperation`. Client: timeout ≥ deadline; auto-fallback to stream/poll; task ID captured before awaiting result. |
| S25 | Cancel semantics: not-immediate cancel, stream behaviour. | Med | A2A#1684, #1680 | Cancel returns task with `metadata["cancelRequested"]=true` while still WORKING; CANCELED emitted on stream then stream closed; `CancellationToken` plumbed to executor. |
| S26 | Terminal-task immutability / revival unspecified. | Med | A2A#1095 | Terminal tasks immutable; follow-up to terminal task → error + hint to create new task with `referenceTaskIds`. |
| S27 | Artifact lifecycle: replace after `lastChunk`, `append=true` creating artifact, empty parts. Go errors, Python ignores. | Med | [A2A#1231](https://github.com/a2aproject/A2A/issues/1231) TSC Review | Enforce seal after `lastChunk`; `append=false` = replace; `append=true` on unknown artifact = create; optional `metadata.sha256` per chunk and final (Content Integrity Profile #1140). Status: implemented (replace after `lastChunk` is allowed, an append to a sealed artifact is rejected by `TaskUpdater` and dropped by the engine, the seal is stored as `a2a.artifact.sealed`, empty parts only for an append to an existing artifact; optional cumulative `metadata.sha256` through `TaskUpdater.AddHashedArtifactAsync` and `ArtifactVerifier`; design: `docs/superpowers/specs/2026-11-08-s27-artifact-lifecycle-integrity-design.md`). |
| S28 | Event `metadata` merge semantics undefined. | Med | A2A#1374 | Document and implement shallow-merge, last-write-wins; expose raw event list. |
| S29 | History: no timestamps, all-or-nothing `historyLength`, no paging; dev-1.1 moves to timeline. | Med | epic #1991 | Store server-side timestamps per message; `historyLength` applied consistently; internal timeline model that can serialize to 1.0 `history` now and 1.1 timeline later. |
| S30 | Error codes don't signal retryable vs permanent. | Med | Eon report; PR #1689 | `ErrorInfo.metadata["retryable"]` + `retryAfter`; client `A2AException.IsRetryable`; Polly-friendly. |
| S31 | Extension header: plain, no registry, activation echo differs per SDK. | Med | [A2A#2428](https://github.com/a2aproject/A2A/issues/2428); a2a-go #516 | Parse comma-list and repeated headers; always echo activated set (unary header, first SSE event metadata); local allowlist of known URIs; unknown non-required ignored; unknown `required` → error. |
| S32 | No task retention/TTL/delete. In-memory stores grow unbounded. | Med | spec text; awslabs OAP #87 | `TaskRetentionPolicy` (TTL by terminal state), background sweeper, non-spec `DeleteTask` admin API (not exposed on A2A endpoint). |
| S33 | 0.3/1.0 payload shapes mixed in the wild. | Med | #2277; Google compat post | Strict `A2A-Version` handling; clear `UnsupportedVersion` error; optional 0.3 read-compat shim behind flag. |
| S34 | No trace propagation defined. | Med | [A2A#2103](https://github.com/a2aproject/A2A/issues/2103) | W3C `traceparent` in HTTP headers **and** message metadata; ActivitySource spans tagged with taskId/contextId; OpenTelemetry semantic conventions for gen_ai where applicable. |

Status: S22, S23, S25, S26, S30, S32 and S33 are done (`TaskStreamer` and SSE resume, `FollowUpPolicy`, `A2AProtocol.CancelRequestedKey`, `TaskStateMachine`, `A2AException.IsRetryable`, `A2ARetentionOptions` and `A2ATaskAdmin`, `CallValidator` and the v0.3 compat packages). S31 is done (comma-list and repeated headers in `HttpPlumbing.CreateCallContext`; the Agent Card's declared extensions are the allowlist in `CallValidator.ApplyExtensions`, unknown non-required URIs are ignored, a missing required one is `ExtensionSupportRequired`; echo on unary and on streams before the first event, on all bindings and on errors). The row's "first SSE event metadata" is deliberately not built: the header is sent before the first event, A2A#2428 is unsettled, and no SDK copies the echo into events. Still partial: S21 (no client key persistence or `ListTasks` recovery), S24 (no client auto-fallback to stream or poll), S28 (metadata merge is artifact-only and undocumented), S34 (no `traceparent` in message metadata, though Part 3 item 17 says done).

### 1.5 Missing capabilities (do via extensions, not core)

| # | Gap | Sev | Evidence | Position |
|---|-----|-----|----------|----------|
| S35 | Client-directed skill selection. | Med | epic #1989 | Extension URI + `metadata["skillId"]`; helper on client. |
| S36 | Discovery/registry undefined. | Med | #2276, #2240 | Out of SDK core. Provide `IAgentCardResolver` with cache + pinning only. |
| S37 | Auth scheme declaration insufficient to obtain tokens. | Med | epic #1990 | `IA2ACredentialProvider` per scheme (OAuth2 client-credentials, API key, mTLS); never auto-follow redirects with credentials (CVE-2026-111763/5). Status: implemented (client credential providers, origin check, no redirects). |
| S38 | Structured elicitation for input-required. | Med | PR #2149 closed → extension | Optional extension package later. |
| S39 | Progress reporting, cost metadata, priorities, batch. | Low | PR #1448, #2121, #1029 | Not core. Metadata conventions documented, not enforced. |
| S40 | File size limits / large binary streaming. | Med | inferred | Enforce caps (S20); prefer `url` parts for large files with signed URLs; SSRF guard on `url` part fetch. |

Status: S35 is done (`SkillSelection`, `A2AClient.SelectSkill`, `AgentRequestContext.RequestedSkillId`; design: `docs/superpowers/specs/2026-11-08-s35-skill-selection-design.md`). S36 is done (`A2ACardResolver` cache and pinning). S40 is done (caps from `A2ALimits`, `SafeHttp.DownloadAsync` for `url` parts). S39 is open (no metadata conventions written down).

---

## Part 2: Official a2a-dotnet gaps and what this SDK does differently

State of `a2aproject/a2a-dotnet` on 2026-11-07: v1.0.0-preview4 (2026-11-02), no GA. Packages A2A, A2A.AspNetCore, A2A.Grpc, A2A.Grpc.AspNetCore, A2A.V0_3, A2A.V0_3Compat. net8.0 + net10.0, AOT-compatible, STJ source-gen. ACTS conformance 276/283. Microsoft Agent Framework pins preview4 and ships its own wrappers (`IsolationKeyScopedTaskStore`, `A2AAgentHandler`) to work around SDK gaps. ~6-month release gaps; many community PRs stale 3-7 months.

### 2.1 Production readiness (biggest differentiator)

| # | Gap in a2a-dotnet | Sev | Evidence | This SDK |
|---|-------------------|-----|----------|----------|
| D1 | Only `InMemoryTaskStore` ships. `DistributedCacheTaskStore` removed in v1; docs stale. | High | a2a-dotnet #192 closed-obsolete; docs/security.md §8 | Ship `A2A.Server.Storage.EntityFrameworkCore` (SQL), `A2A.Server.Storage.Redis`, `A2A.Server.Storage.Cosmos` as separate packages. In-memory stays for tests. |
| D2 | `ITaskStore` has no ETag/revision, no conditional write, no idempotent mutation id. Multi-replica lost updates. | High | [#502](https://github.com/a2aproject/a2a-dotnet/issues/502) open | Store contract: `Save(task, expectedVersion)` → `ConcurrencyException`; `TaskRecord.Version`; mutation id per write (ties to S16, S21). |
| D3 | Live coordination process-local: subscribe fan-out via in-memory channel, cancel via in-process dictionary, push store in-memory. Multi-pod misses events, cannot cancel. | High | [#463](https://github.com/a2aproject/a2a-dotnet/issues/463) open | `IEventBus` abstraction for task events + cancel signals; in-memory default, Redis pub/sub and Azure Service Bus providers. `IEventStore` for replay (S22). |
| D4 | Terminal-state race guard ("first persisted terminal wins") adopted but PR unmerged. | Med | #401 closed; PR #453 open since 2026-08 | Enforced in store write path via version check (D2); state machine validator rejects terminal→anything. |
| D5 | SSE: no heartbeat, no `id:`, no `Last-Event-ID`. PR open 3 months, "after v1". | Med | PR #454 open | On by default (S22). Idle timeout, max event size, keep-alive interval configurable. |
| D6 | Streaming error paths repeatedly fragile (4 separate bugs: hang, silent EOF, error ends stream silently, error after headers). | Med | #340, #495, #551, #478 | Single streaming pipeline shared by JSON-RPC/HTTP/gRPC; error always materializes as terminal error event; tests for each historical failure mode. |
| D7 | Execution gate serializes whole executions per task incl. push admission; disconnect does not release gate. | Med | docs/push-notifications.md | Separate executor lock from delivery; push delivery via outbox (S11), never on request path. |
| D8 | Proactive `ContinueTaskAsync` requires active push registration. | Med | docs/push-notifications.md | Server-initiated continuation independent of push; events go to event store + bus; push is one subscriber. |
| D9 | No durable push sender; "app responsibility". | Med | docs/durable-push-notifications.md | `IPushNotificationOutbox` + background sender with retry/DLQ (S11). |
| D10 | Client has no timeout/retry/backoff. | Med | source | Built on `Microsoft.Extensions.Http.Resilience`; retry only when `ErrorInfo.metadata.retryable` or transport failure; idempotency key on every `SendMessage` (S21). |
| D11 | Auto-delete push configs after terminal delivery broke conformance tests. | Low | #592 open | Retention policy opt-in, default off; purge via sweeper (S32). |

Status: D4, D6, D7, D9, D10 and D11 are done (`TaskStateMachine` and version checks, shared `TaskStreamer` with `Parity/RegressionTests`, `PushOutbox` and `PushDeliveryService`, `RetryPolicy` instead of `Http.Resilience`, opt-in `AddA2ARetention`). Still partial: D1 (Postgres and Redis ship, Cosmos does not), D2 (no mutation id per write), D3 (no Azure Service Bus provider), D5 (heartbeat, ids and resume are done; `MaxStreamDuration` exists, but there is no idle timeout).

### 2.2 Security

| # | Gap in a2a-dotnet | Sev | Evidence | This SDK |
|---|-------------------|-----|----------|----------|
| D12 | No auth/authz hooks; "SDK does not apply any authentication"; no OAuth sample since 2025. | High | docs/security.md §1; #148 open | `IA2AAuthorizationHandler` with per-operation policy; ASP.NET Core auth integration (`RequireAuthorization` on all bindings); samples: API key, OAuth2 client-credentials, mTLS. |
| D13 | No principal/tenant scoping of tasks or push configs (IDOR). Explicit non-goal. Agent Framework had to wrap. | High | docs/security.md §7; PR #450 open; AF #9494 | Owner + tenant are part of `TaskRecord` and store key from day one (S12, S14). |
| D14 | Agent card JWS: DTO only, no sign/verify. Resolver drops signatures on v0.3 upcast. | Med | source; #164 | `A2A.Protocol` ships signer + verifier (S1–S3). |
| D15 | Unexpected exceptions → ProblemDetails with `ex.Message`. | Med | docs/security.md | Generic message by default; detail only in Development environment; structured log with redaction. |
| D16 | Shared default `HttpClient` with cookies across users. | Med | docs/security.md §2 | `UseCookies=false` default; named `HttpClient` per peer via `IHttpClientFactory`; never share across principals. |
| D17 | No DoS limits; all delegated to Kestrel. | Med | docs/security.md §3-4 | Defaults in SDK (S20). |
| D18 | Webhook SSRF guard is strong (keep). Receiver-side verification absent. | Low/Med | docs/push-notifications.md | Match their guard; add receiver helper verifying token + HMAC + delivery id (S10). |

Status: D12 to D18 are done (`A2AAuthorizationHandler` with policy on all bindings, samples and `docs/site/snippets/ClientCredentials.cs` for API key, OAuth2 and mTLS; `TaskRecord` tenant and owner; `AgentCardSigner` and `AgentCardVerifier`; `HttpPlumbing.Normalize`; `UseCookies=false`; `A2ALimits`; `PushNotificationReceiver`).

### 2.3 Spec coverage and transport parity

| # | Gap in a2a-dotnet | Sev | Evidence | This SDK |
|---|-------------------|-----|----------|----------|
| D19 | One static card per host; per-request handler factory PR stale; "enterprise territory". | High for platforms | [#353](https://github.com/a2aproject/a2a-dotnet/issues/353); PR #382 | `IAgentCardProvider` and `IAgentHandlerResolver` resolved per request (tenant, path, claims). Many agents per host is a core scenario. |
| D20 | JSON-RPC/HTTP servers clear incoming `tenant`; no `/{tenant}/...` routes. | Med | #485 closed policy | Tenant route templates on all bindings; `ITenantResolver` (S14). |
| D21 | REST cancel drops `metadata`; REST GET subscribe missing. | Low | #593, PR #591 | Transport parity test matrix in CI: every op × 3 bindings × (metadata, tenant, history, errors). |
| D22 | Union models allow multiple alternatives populated; gRPC emits first only. | Med | #556 item 2 | `Part` as closed hierarchy (`TextPart`, `RawPart`, `UrlPart`, `DataPart`) with factory; `SendMessageResponse`/`StreamResponse` as discriminated unions. |
| D23 | Custom/extension method dispatch half-done. | Med | #169, #574 | `IA2AOperation` registry per transport from the start; extensions register operations + card entries together. |
| D24 | V0_3Compat silently drops final message/artifacts when `blocking` absent. | High for interop | #386 open | If 0.3 supported at all: behaviour-preserving mapping (default blocking) or loud error. Decision: ship 0.3 **read** compat only behind flag (S33). |
| D25 | No netstandard2.0. | Med | #197 open | `A2A.Protocol` and `A2A.Client` target netstandard2.0 + net8.0 + net10.0. Server stays net8+. |

Status: D19, D20, D21, D22 and D24 are done (`IAgentCardProvider` and `TenantCardProvider`, `{tenant}` routes, `Parity/ParityTests` over all three bindings, closed `Part` hierarchy, default-blocking mapping in `V03Codec`). D23 is done (`AddExtensionOperation` registers the operation and its card entry together; `A2AOperationTable` for JSON-RPC and HTTP+JSON; `A2AGrpcExtensionService` and `MapA2AGrpcExtensions` for gRPC, with the same `a2a.extensions.v1` service as the official SDK). Extension operations are unary on every binding, and the client has no helper to call them.

### 2.4 API design and DX

| # | Gap in a2a-dotnet | Sev | Evidence | This SDK |
|---|-------------------|-----|----------|----------|
| D26 | 21+ breaking changes across previews; AF needed its own migration guide. | High | PR #300; AF migration guide | Semver from 1.0; `[Experimental]` for unstable surface; CHANGELOG; public API baseline files (`PublicAPI.Shipped.txt`) enforced by analyzer. |
| D27 | Public interfaces vs abstract classes never resolved; every new member breaks implementers. | Med | #133 open | Extension points = abstract base classes with virtual members; options = sealed classes; small interfaces only where multiple inheritance needed. |
| D28 | Per-request headers: 16-month issue; "use DelegatingHandler". | Med | #107, PR #399 | `A2ARequestOptions` (headers, auth, timeout, extensions, idempotency key) on every client call. |
| D29 | Required fields not enforced. | Med | #198 open | `required` members + runtime validator before send; Roslyn analyzer package optional. Done: runtime check before send; no new `required` members, see docs/superpowers/specs/2026-11-08-required-field-validation-design.md. |
| D30 | `SubscribeToTask` on terminal task throws; no helper. | Low | #341 | Client `ObserveTaskAsync` returns snapshot then stream; handles terminal, reconnect, cursor (S22). |
| D31 | Observability metric names SDK-specific, not OTel GenAI semconv. | Low | source | Follow the OTel A2A conventions page (`docs/gen-ai/a2a.md`); trace context propagation (S34). |
| D32 | Mandatory path param, surface noise, duplicated error maps, 5 copies of streaming lifecycle. | Low/Med | #186, #556 | Default `/` route; one error-code map in `A2A.Protocol`; one streaming pipeline. |
| D33 | AF hosting: `ReferenceTaskIds` unsupported, message-only mode cannot cancel/resume, scoped lifetimes ignored, context memory lifecycle unresolved. | Med | AF #7733, #4876, #9481 | Not our bug, but: scoped DI per request, `referenceTaskIds` passed through to handler, context lifecycle policy hook. |

Status: D27, D28, D30 and D32 are done (abstract extension points and sealed options, `A2ARequestOptions`, `ObserveTaskAsync`, default `/` route with one error map and one streamer). D26 is done (policy in `docs/site/guides/versioning.md`; `[Experimental]` with `A2AEXP001` to `A2AEXP004`; CHANGELOG current; `PublicAPI.*.txt` enforced on all 15 packable projects by warnings as errors). Package validation against a baseline version needs a first published version. D33 is partial (no context lifecycle policy hook).

---

## Part 3: Prioritized additions

**P0: before first usable release** (security + correctness; without these a deployment is unsafe)
1. Owner/tenant-scoped task store contract with versioned writes (S12, S13, S14, S16, D2, D13).
2. Auth hooks on every binding; foreign task → `TaskNotFound` (S12, D12). Done.
3. Push webhook SSRF guard default-on + secret redaction + dual token headers (S7, S8, S9, D18). Done.
4. Idempotency: `Idempotency-Key` + `messageId` dedupe (S21, D10).
5. SSE sequence ids, `Last-Event-ID`, heartbeat, bounded replay (S22, D5).
6. Agent card verifier with trust-anchor policy and correct RFC 8785 canonicalization (S1, S2, D14).
7. Size/rate limits defaults (S20, D17). Done.
8. Generic error messages by default; no cookies by default (D15, D16). Done.
9. Closed `Part` hierarchy and discriminated unions (D22). Done.
10. Transport parity test matrix (D21). Done.

**P1: production scale**
11. SQL/Redis stores (D1).
12. `IEventBus` + `IEventStore` with Redis provider (D3, S22).
13. Durable push outbox + retry + HMAC signature + delivery id (S10, S11, D9).
14. Per-request card/handler resolution, tenant routes (D19, D20). Done.
15. Interrupted-state TTL, approval binding (loopjacking), cancel-requested semantics (S18, S19, S25). Done.
16. Client resilience: timeouts, retry on retryable, `ObserveTaskAsync` (D10, D30, S30). Done.
17. Trace context + OTel GenAI semconv (S34, D31). Done.
18. Task retention sweeper (S32, D11). Done.

**P2: ecosystem**
19. actorChain / token exchange for delegation (S17). Done.
20. Extension registry, operation registry (S31, D23). Done.
21. netstandard2.0 for Protocol + Client (D25).
22. 0.3 read-compat shim behind flag (S33, D24). Done.
23. Artifact chunk hashing (S27). Done.
24. Skill-selection and elicitation extension packages (S35, S38). S35 done, in the existing packages.
25. Analyzer package, PublicAPI baseline (D26, D29). PublicAPI baseline done; no analyzer package.

---

## Part 4: Non-goals

- Agent registry/discovery service (S36). Resolver + cache only.
- Agent identity standard (DID/SPIFFE). Provide abstraction, not implementation.
- Payment, cost, priority, batch semantics (S39). Metadata conventions documented only.
- Full v0.3 write compatibility (D24).
- Replacing Kestrel-level protections; SDK limits are defaults, not a WAF.

## Part 5: Package layout implied

```
A2A.Protocol                 models, STJ source-gen, errors, canonicalizer, card signer/verifier, validators
A2A.Client                   bindings, resilience, ObserveTaskAsync, card resolver + pinning, credential providers
A2A.Server                   request pipeline, state machine, stores (contracts + in-memory), event bus/store contracts,
                             idempotency, authz hooks, push outbox, SSRF guard, limits, retention
A2A.Server.AspNetCore        JSON-RPC + HTTP+JSON + SSE endpoints, tenant routes, auth integration, rate limiter
A2A.Server.Grpc              (later) gRPC binding sharing the same pipeline
A2A.Server.Storage.*         EF Core, Redis, Cosmos
A2A.Server.Messaging.Redis   event bus provider
A2A.Extensions.*             skill selection, elicitation, actorChain
```

## Sources

Spec repo: https://github.com/a2aproject/A2A (issues/epics cited inline; v1.1 candidates #1986–#1992, #1995). Security research: A2ABreak arXiv 2609.10871; arXiv 2609.27624; MAESTRO arXiv 2504.16902; CVE-2026-111761..111765, CVE-2026-90820, CVE-2026-116479 (a2a-java); AAIF builder's guide; Eon production report. .NET SDK: https://github.com/a2aproject/a2a-dotnet issues, PRs, docs/, releases; Microsoft Agent Framework issues; AF migration guide. Severity ratings and items marked "inferred" are this document's judgment, not sourced.
