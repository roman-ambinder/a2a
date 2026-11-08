# SDK design principles: easy, performant, extendable, modular

Date: 2026-11-07. Status: draft for review.
Scope: the A2A .NET SDK in this repository (`src/*`). This document sets the design rules every future change is measured against, states where the code stands today against each rule, and decides the structural changes needed to meet them. It builds on `docs/superpowers/specs/2026-11-07-p0-security-correctness-design.md` (P0), which stays in force: nothing here relaxes a P0 security default.

The user's brief was four words: easy to use, as performant as possible, extendable, modular. Everything below either comes from that brief, from P0, or is marked as my call.

## 1. Intent

The SDK should be the one a .NET team picks when they need to ship an A2A agent or client that is safe, fast and still theirs to shape. Today the safety story is done (P0). The code is sound but grew by accretion: one 1,559-line class owns most of the server, every store boundary copies by serializing, there is no operation pipeline and no tracing, and a hello-world server needs three registrations and two middleware calls that each fail the app at startup if forgotten. This design fixes the shape before the public API is frozen at the first NuGet release (`v0.1.0` is tagged in `Directory.Build.props` but not yet published).

## 2. The principles and how they rank

Order when two principles collide (my call, derived from the brief's order plus P0):

0. **Safe and correct by default** (P0, floor). No principle below buys a weaker default.
1. **Easy to use.** The call site a user writes wins over the call site we would prefer to implement.
2. **Performant.** Hot paths (one request, one event, one SSE frame) must not pay for convenience the caller did not ask for. Convenience overloads must be thin wrappers over the fast path, never a second slower path.
3. **Extendable.** Every behaviour a host might reasonably need to change has a seam. Seams are public, documented and tested through a second implementation.
4. **Modular.** A package carries only what its name says. A user who does not want gRPC, DI or ASP.NET Core does not download it.

Resolution rule: a change may trade down the list but never up. Example: splitting a class to make it replaceable (3) may not add an allocation per event (2); adding a fast path (2) may not make the one-line client call longer (1).

## 3. Goals

G1. Server hello world is one `Add` call, one `Use` call, one `Map` call and an executor, plus whatever authentication scheme the host already has. Nothing else is required for a secure default. (Reviewer question 2, my call: `UseA2A()` counts, the Agent Card endpoint is mapped by `MapA2A`, and the host's `AddAuthentication` lines are the host's, not the SDK's; the line-count gate counts only lines that name an `A2A` API.)
G2. Client hello world is one line without DI and two lines with DI. Same for gRPC.
G3. Per-event server cost is O(event size), not O(task size). No serialize-to-measure, no serialize-to-clone, no double encode per frame.
G4. Every server operation runs through a public pipeline where a host can observe, short-circuit or wrap it. Tracing and metrics come from that pipeline.
G5. Every binding (JSON-RPC, HTTP+JSON, gRPC, third-party) talks to the server core through one public interface and gets the same capabilities. No `InternalsVisibleTo` between shipped packages.
G6. `A2A.Protocol` is pure data and math. `A2A.Client` and `A2A.Server` work without a DI container. Optional dependencies live in optional packages.
G7. Every public type and member has an XML summary. The public API is snapshotted and a change to it fails the build until the snapshot is updated on purpose.
G8. Performance is measured in CI with benchmarks that fail on regression beyond a threshold.

## 4. Non-goals

- Durable stores, multi-instance bus, Redis, SQL. Contracts must allow them (G3 changes the store contract); implementations are later work.
- Public API compatibility with today's code. Nothing is on NuGet yet. Renames are fine until the first publish.
- netstandard2.0. Targets stay net8.0 and net10.0 (P0 constraint).
- A plugin or extension registry for A2A protocol extensions beyond the negotiation and echo that exist today. The pipeline (G4) is where such a registry would hang; the registry itself is not in scope.
- Rewriting the gRPC mapping as hand-written code. A faster path is in scope (section 7.4); a full generated mapper is not.

## 5. Anti-goals (failure even if it works)

- A convenience API that silently takes a slower path than the explicit one.
- A seam that the first-party binding bypasses through internals. If our own binding needs it, the seam is wrong.
- An extension point without a second implementation in the tests. Untested seams rot.
- A "simple" overload that turns a security default off to be simple.
- Performance work that changes wire behaviour or breaks the stored-before-published and gap-fill invariants of the streaming code.
- A benchmark that is not run in CI.

## 6. Constraints

- Targets net8.0 and net10.0; `TreatWarningsAsErrors`, `IsAotCompatible`, `Nullable` on (existing `Directory.Build.props`).
- System.Text.Json source generation only; no reflection serializer paths. `A2AJsonContext` is the single context.
- Third-party runtime dependencies: none in `A2A.Protocol`, `A2A.Client`, `A2A.Server` other than `Microsoft.Extensions.*` abstractions. gRPC packages carry Google/Grpc packages.
- Wire behaviour is fixed by P0 and the parity suite in `tests/A2A.Tests/Parity`. Those tests must keep passing unchanged through every refactor here; they are the regression net.
- Refactor, not rewrite (user decision from P0, carried forward).

## 7. Where the code stands and what changes

Findings come from an audit of the code on 2026-11-07 (build green, 0 warnings, 348 tests). File references are to this commit; builders locate code by symbol, since lines move.

### 7.1 Easy to use

Today:
- Server needs `AddA2AServer(...)`, `.AddAgentExecutor<T>()`, `AddA2AServerAspNetCore()`, then `UseAuthentication()`, `UseA2A()`, `UseAuthorization()`, `MapAgentCard()`, `MapA2A(...).RequireAuthorization()`. Forgetting `AddA2AServerAspNetCore` throws at `MapA2A`; forgetting `UseA2A` or `RequireAuthorization` fails startup. The card needs eight `required` fields, and its interface URLs duplicate the host's listen address.
- Client without DI is one line (`A2AClientFactory.CreateAsync(uri, httpClient)`). The gRPC client has no URL-based factory; the caller must fetch the card first.
- 36 of 313 public types and about 170 public members lack an XML summary. `CS1591` is suppressed, so this does not fail the build. Every request DTO, most Agent Card sub-types and `A2AServerOptions` are affected.
- 13 option types. The rate-limit switch lives in `A2AServerOptions` while the rate-limit options live in `A2A.Server.AspNetCore`. Tenant can be set in three places on the client.

Decisions:
- D1. `A2A.Server.AspNetCore` gets `AddA2A(this IServiceCollection, Action<A2AServerBuilder>)` (name my call) that registers the core and the ASP.NET Core side in one call. `AddA2AServer` and `AddA2AServerAspNetCore` stay as the two-step form for hosts that need it. `MapA2A` keeps its fail-fast check. `MapA2A` also maps the Agent Card at `/.well-known/agent-card.json` (anonymous, cacheable) unless `A2AEndpointOptions.MapAgentCard = false`; `MapAgentCard()` stays for hosts that want the card on another path or port. The target hello world is:

```csharp
builder.Services.AddA2A(a => a.AddAgentExecutor<MyAgent>().AddAgentCard(new AgentCard { Name = "...", Description = "...", Version = "1.0.0", Skills = [...] }));
app.UseAuthentication(); app.UseA2A(); app.UseAuthorization();
app.MapA2A("/a2a").RequireAuthorization();
```
- D2. `UseA2A()` stays required (it owns the rate limiter middleware and cannot be done at `Map` time), but the startup error message names the exact two lines to add. `MapA2A` gains an `A2AEndpointOptions` overload so `RequireAuthorization` can be expressed as `MapA2A("/a2a", o => o.Authorization = "policy")`; the group form remains.
- D3. Card convenience: a card whose `SupportedInterfaces` is empty is completed at first serve, not at `Add` time (reviewer question 5, my call). `AgentCardEndpoint` reads the bindings from `A2AEndpointRegistry` (already populated by every `MapA2A*` call) and the listen address from `IServerAddressesFeature`, or from `A2AHttpOptions.PublicBaseUrl` when set (reverse proxies). The completed card is what gets signed and cached; `ExtendedAgentCardProvider` receives the completed card. Explicit interfaces are never touched. `Capabilities`, `DefaultInputModes`, `DefaultOutputModes` and `Skills` get empty defaults so the `required` burden drops to `Name`, `Description`, `Version`.
- D4. `A2AGrpcClientFactory.CreateAsync(Uri, ...)` that resolves the card the same way the HTTP factory does. `A2AClientFactory` already picks gRPC when the card lists it first; keep that.
- D5. Lift the `CS1591` suppression for all `src/` projects. Document every public member. This is a one-time writing task, then enforced forever by the build.
- D6. Options: `A2ARateLimitOptions` and `EnableRateLimiting` move to the same side. Rule: a switch and the options it switches live in one type. Tenant on the client: keep the three (card interface, client default, per request) because they are genuinely three scopes, but document the precedence in one place (`A2AClientOptions.Tenant`).
- D7. Rule for new API: a static factory or builder for the common case, the full options type for the rest. Options types are sealed, bound through `IOptions<T>`, with every default stated in the XML summary.

### 7.2 Performant

Today (audit, verified against source):
- `A2ARequestHandler` serializes every agent event to a byte array only to measure it against `Limits.MaxEventBytes`, then discards the bytes. The same event is then serialized again by `InMemoryEventStore.AppendAsync` to clone it, again per subscriber on `ReadAfterAsync`, again for the SSE frame, and again for push. `A2AJson.Clone` (serialize then deserialize) is called at 14 sites in the handler alone. The in-memory task store clones the whole task on get and twice on update. Since a task carries its full history, cost per event is proportional to task size: O(n²) over a task's life.
- JSON-RPC responses are serialized twice: payload to `JsonElement`, then the envelope. This happens per unary response and per SSE event (`JsonRpcEndpoint`, `JsonRpcTransport`).
- Request bodies are read into a `MemoryStream`, copied with `ToArray()`, parsed into a `JsonDocument`, then deserialized from the element. On net8 a further reader pass checks duplicate keys. Up to five passes over the body.
- gRPC mapping goes proto → JSON string → model and back, per message and per stream event (`ProtoMapper`).
- All channels are unbounded: executor incoming and outgoing, every bus subscription. A fast agent with a slow SSE client grows memory without bound.
- Push delivery lists all push configs of all tasks per delivered event (`PushConfigStore.ListAsync` does a `Where` over the whole dictionary).
- Client reads bodies as `string`, parses to `JsonDocument`, then deserializes. `SseReader` allocates a string per line.
- Done right already: source-generated JSON with no reflection fallback, `ValueTask` store contracts, `ConfigureAwait(false)` everywhere, no sync-over-async, `ConfigureAwait` and cancellation plumbed, one `[GeneratedRegex]`.

Decisions:
- D8. **Store contract returns owned records; callers never mutate them.** No second task model (reviewer question 1, my call: the cheaper reading). `TaskRecord` keeps holding `AgentTask`. The contract changes from "implementations must return copies" to "the store owns the instance it returns; callers must not mutate it", stated on the abstract class and checked in debug builds by a test-only store wrapper that hashes records on return and on next call. The handler builds the next state by a structural copy made **once per write** (`AgentTask.CopyForUpdate()`: new `AgentTask`, new history and artifact lists, shared immutable `Message`/`Artifact` instances; a new `Artifact` only when `append` touches it) and hands that copy to `UpdateAsync`. The executor's view (`AgentRequestContext.Task`, `CancelAsync(AgentTask)`) is one deep copy per execution, made with the same structural copy, not JSON. `Shape()` builds a trimmed view that shares untouched parts, because the view is serialized at once and never kept. The `Message`/`Artifact` instances the executor emits through `TaskUpdater` are stored as given; `TaskUpdater` documents that the caller must not mutate them after emitting. This removes every `A2AJson.Clone` on the read and write paths. Wire DTOs stay mutable classes.
- D9. **Measure once, encode once.** The event-size check uses a counting `Utf8JsonWriter` over a null stream, or better, the event is encoded once into a pooled buffer that is then used for the size check, the event store, every SSE subscriber and push. A `SequencedEvent` carries its encoded UTF-8 payload alongside the model; SSE and push write the bytes, they do not re-encode.
- D10. **One encode per frame.** JSON-RPC envelopes are written with `Utf8JsonWriter`, embedding the payload bytes (from D9) or writing the payload inline via `JsonTypeInfo<T>`. No `SerializeToElement` on the response path. Same for the client transport.
- D11. **One pass over the request body.** Read the body into a pooled buffer once (respecting `Limits.MaxBodyBytes`), run a single `Utf8JsonReader` pass that enforces depth and duplicate keys, then deserialize from the same bytes. Client side: `ReadAsStreamAsync` + `DeserializeAsync`, and `SseReader` works on `ReadOnlySpan<byte>` lines.
- D12. **Bounded channels.** Executor and bus channels become bounded with a configured capacity (`StreamingOptions.ChannelCapacity`, default my call: 256). Bus subscriptions use `DropOldest` because the handler already fills gaps from the store; dropping the oldest (not the newest, as `DropWrite` would) keeps the latest event queued, so a lagging subscriber always sees the gap and never waits without it (for example for an INPUT_REQUIRED that no later event follows). The request's own outgoing channel uses `DropWrite` (written under the task lock, so it never waits; its first event is always kept), and its readers gap-fill too. The executor's incoming channel uses `Wait` so a fast agent is throttled rather than dropped. The stored-before-published invariant is untouched.
- D13. **Index push configs by `TaskKey`**; one long-lived delivery worker per task with an active config, not `Task.Run` + `ContinueWith` per event.
- D14. **gRPC mapping** switches to UTF-8 bytes end to end (`JsonFormatter` to a `Utf8JsonWriter`-compatible buffer, `JsonParser` from bytes) so no UTF-16 string is created. A direct mapper is a non-goal.
- D15. **Benchmarks in CI.** New `tests/A2A.Benchmarks` (BenchmarkDotNet) with at least: one SendMessage round trip in-process, one 1,000-event streaming task, one JSON-RPC envelope encode, one card verification. CI runs them in a short-job mode and compares against a committed baseline; regression over 10 percent on allocations or time fails the job. Thresholds are my call and may be tuned by the builder.
- D16. Rule for new code: no `A2AJson.Clone` outside tests; no `SerializeToElement` on a response path; no unbounded channel; no `ToList` inside a loop that runs per event.

### 7.3 Extendable

Today:
- Seams that exist and are registered with `TryAdd`: `TaskStore`, `EventStore`, `PushConfigStore`, `IdempotencyStore`, `A2AAuthorizationHandler` (abstract classes); `TaskEventBus` (virtuals); `IPushNotificationSender`, `IPushSecretProtector`, `IPrincipalResolver`, `ITenantResolver`, `IAgentExecutor`, `IA2ATransportFactory`, `ICardPinStore`. Good.
- `A2ARequestHandler` is `sealed`, registered as its concrete type, and all three bindings take the concrete type. It owns call validation, authorization, idempotency orchestration, the blocking wait loop, two streaming state machines, stream limits and re-auth, task CRUD, push-config CRUD with redaction and secret protection, the execution engine, the task state machine, lock striping and shutdown. Nothing in it can be wrapped or replaced without forking the file.
- The only hooks around an operation are `ValidateCall` and `AuthorizeAsync` returning a boolean. No way to observe a request and response, add a header, time an operation or return a custom error.
- No `ActivitySource` anywhere. Three counters total.
- Custom JSON-RPC methods: the dispatcher is a closed `switch` with `MethodNotFound` as default. REST routes are hardcoded. Both endpoint classes are `internal sealed`.
- Third-party client transports lose stream resume because `IResumableTransport` and `StreamFrame` are internal.
- The ASP.NET Core binding calls `internal` handler overloads through `InternalsVisibleTo`; the gRPC binding and third-party bindings cannot.

Decisions:
- D17. **Split `A2ARequestHandler`** along its responsibilities into (names my call): `CallValidator` (version, extensions), `IdempotencyCoordinator`, `TaskExecutionEngine` (start, consume, apply, store, publish), `TaskStreamer` (send-stream and subscribe state machines, gap fill, limits, re-auth), `PushConfigService`, and a thin `A2ARequestHandler` that composes them and implements the 11 operations. Each piece is a public class or abstract class registered with `TryAdd` and replaceable. The composition root is `AddA2AServer`.
- D18. **`IA2ARequestHandler`** public interface with the 11 operations plus the resume overloads that today are internal. Every binding depends on the interface. The `onCaughtUp` overloads become public. `InternalsVisibleTo` between shipped packages is removed; only test assemblies keep it.
- D19. **Operation pipeline.** `IA2AOperationFilter` with `ValueTask<TResponse> InvokeAsync<TRequest,TResponse>(A2AOperationContext ctx, TRequest request, A2AOperationDelegate next, CancellationToken ct)` (shape my call; the builder may choose a non-generic variant if generics fight the AOT analyzer). Filters are registered in order through `A2AServerBuilder.AddOperationFilter<T>()`. Placement (reviewer question 4, my call): `ValidateCall` and tenant binding happen before the pipeline, in the handler's entry for each operation, because they are request parsing, not policy; `ValidateCall` stays a member of `IA2ARequestHandler` so bindings keep calling it. Authorization is a built-in filter that runs first. Host filters run after it by default, so they only ever see authenticated, tenant-bound calls and can tag `tenant` reliably. A host filter may opt to run before authorization with `AddOperationFilter<T>(A2AFilterStage.BeforeAuthorization)`; such a filter sees unauthenticated calls and the XML docs say so. The operation itself is the innermost delegate. Streaming operations pass through the same chain with an `IAsyncEnumerable` response; filters that need per-event access wrap the enumerable.
- D20. **Telemetry as the first filter.** `A2A.Server` ships `ActivitySource("A2A.Server")` and `Meter("A2A.Server")` with spans per operation (tags: operation, tenant, task id hash, outcome) and counters for requests, events, stream opens, push deliveries. `A2A.Client` ships `ActivitySource("A2A.Client")`. W3C `traceparent` is propagated on HTTP and gRPC. The server telemetry is implemented as an `IA2AOperationFilter`, proving the seam.
- D21. **Operation registry for bindings.** `JsonRpcEndpoint` and `RestEndpoint` become public and dispatch through an `A2AOperationTable` that maps method names and routes to handler operations. Hosts can add entries for extension operations (a `Func<A2AOperationContext, JsonElement, CancellationToken, ValueTask<JsonElement>>`) which also appear in the card's extensions list. Unknown methods still answer `MethodNotFound`. Scope and authorization (reviewer question 3, my call): the table serves JSON-RPC (method name) and REST (route `POST {pattern}/ext/{name}`); gRPC is exempt because its proto is fixed, and this is the one stated exception to G5. `A2AAuthorizationHandler.AuthorizeAsync` changes its `A2AOperation op` parameter to an `A2AOperationDescriptor` record (`A2AOperation Kind`, `string Name`); the 11 built-ins keep their enum values and names, extension operations use `Kind = A2AOperation.Extension` with the registered name. The default allow-all handler **denies** extension operations unless the registration passed an explicit `allow: true` or an authorization policy name; a custom handler decides by `Kind` and `Name`. Fail closed keeps the P0 floor. The descriptor change is a breaking change to a P0 seam, allowed because nothing is published.
- D22. **Client transport parity.** `IResumableTransport` and `StreamFrame` become public. `IA2ATransportFactory` documentation states that implementing `IResumableTransport` enables resume; `A2AClient` treats a non-resumable transport exactly as today. Server side (reviewer question 6, my call): the gRPC binding does **not** gain stream resume. The spec defines no cursor for gRPC and an SDK-only metadata key would be wire-visible. G5's "same capabilities" means the same handler surface is reachable; the gRPC binding may leave the resume overloads unused and says so in its XML docs.
- D23. Rule: every seam has a second implementation in `tests/` (in-memory plus a test double at minimum), and a test that swaps it in through DI.
- D24. Rule, carried from P0: growing contracts are abstract classes with virtual members; one-method contracts that will not grow are interfaces. `IA2ARequestHandler` is the exception because callers mock it and only the SDK implements it (same reasoning as `IA2AClient`).

### 7.4 Modular

Today:
- Package graph: `Protocol` (no package references) ← `Client` ← `Client.Grpc`; `Protocol` ← `Server` ← `Server.AspNetCore` ← `Server.Grpc`; `Protocol` ← `Grpc`. `A2A.Server` has no ASP.NET Core reference. Good.
- `A2A.Protocol` performs network I/O: `PrivateAddressGuard` builds a `SocketsHttpHandler`, `AgentCardVerifier` owns a static `HttpClient` and fetches `jku` key sets.
- `A2A.Client` references `Microsoft.Extensions.Http` unconditionally, used only by its DI file. A console client pulls DI, Options, Logging and Configuration abstractions.
- `A2A.Server` requires `IServiceScopeFactory`, `IOptions<>` and `IHttpClientFactory`. It is transport-agnostic but not container-agnostic.
- Namespaces: the `A2A.Protocol` assembly uses `A2A`, `A2A.Json`, `A2A.Signing`, `A2A.Net`, `A2A.Validation`, `A2A.JsonRpc`. `RestErrorBody` (an HTTP binding shape) lives in Protocol. `A2AAuthorizationHandler` is in namespace `A2A.Server` though it sits in an `Authorization/` folder.

Decisions:
- D25. **`A2A.Protocol` becomes pure.** JCS, JWS signing and verification math, models, serialization, errors and the private-address *classification* (pure IP range logic) stay. The `SocketsHttpHandler` factory, the `jku` fetcher and the pinned-connect handler move to a new package `A2A.Security.Http` (name my call) referenced by both `A2A.Client` and `A2A.Server`. `AgentCardVerifier` takes an `IJwksFetcher` (interface, one method) so Protocol can verify with pre-fetched keys and the fetcher lives in the HTTP package.
- D26. **DI split for the client.** `A2A.Client` drops `Microsoft.Extensions.Http` and `DependencyInjection.Abstractions`. `AddA2AClient`, `IA2AClientFactory` and `BearerTokenHandler` registration move to `A2A.Client.DependencyInjection`. `Microsoft.Extensions.Logging.Abstractions` stays in `A2A.Client` because an `ILogger` parameter is cheap and widely expected (my call).
- D27. **Server core without a container.** `A2A.Server` keeps `Microsoft.Extensions.*` abstractions (they are the .NET standard for options and logging) but its core types get constructors that take plain dependencies: `TaskExecutionEngine(TaskStore, EventStore, Func<IAgentExecutor> executorFactory, A2AServerOptions, ILogger)`. `IServiceScopeFactory` is used only by the DI-registered `ScopedExecutorFactory`. `IHttpClientFactory` is replaced in `HttpPushNotificationSender` by an `HttpMessageInvoker` parameter, with the DI registration supplying one from the factory. A test proves the core serves a SendMessage with `new`-constructed parts and no `ServiceCollection`.
- D28. **Namespaces follow assemblies.** `A2A.Protocol` assembly uses `A2A.Protocol.*`? No: the model types stay in `A2A` because `A2A.AgentCard` is what users type (my call: brevity at the call site wins, principle 1). Rule instead: every sub-namespace in an assembly maps to one folder, and no type lives in a namespace owned by another assembly. Concretely: `RestErrorBody` moves to `A2A.Server.AspNetCore`; `A2AAuthorizationHandler` moves to `A2A.Server.Authorization`; the `A2A.Net` namespace goes with D25.
- D29. **Package dependency rules**, enforced by a test that reads each `csproj`: Protocol has no package references; Client and Server reference only `Microsoft.Extensions.*.Abstractions` and `Microsoft.Extensions.Options`; nothing in `src/` references a test or sample; no `InternalsVisibleTo` to a `src/` assembly.

## 8. Enforcement

Principles that are not enforced decay. Each goal maps to a gate:

| Goal | Gate |
|---|---|
| G1, G2 | `samples/` are compiled in CI and a test asserts the line counts of the hello-world sections stay at or below a committed number. |
| G3, G8 | `tests/A2A.Benchmarks` with baseline and threshold (D15). |
| G4, G5 | Tests that register a filter and observe every operation on all three bindings; a test that implements `IA2ARequestHandler` as a fake and runs the HTTP binding over it. |
| G6 | csproj rule test (D29); a no-DI server test (D27); a no-DI client test (exists, keep). |
| G7 | `CS1591` as error (D5) plus `Microsoft.CodeAnalysis.PublicApiAnalyzers` with `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` per `src/` project. Before the first NuGet publish, everything is in Unshipped; publishing moves it to Shipped. |
| AOT | A `dotnet publish -p:PublishAot=true` of `samples/EchoAgent` and `samples/EchoClient` in CI on net10.0; any trim or AOT warning fails. |
| Analyzers | `AnalysisLevel=latest-recommended` in `Directory.Build.props` for `src/`. |

## 9. Order of work

Each stage ends with build green, all 348+ tests passing, parity suite unchanged.

1. Enforcement scaffolding first: PublicApiAnalyzers (all Unshipped), `CS1591` lifted project by project, benchmarks project with baseline from current code, AOT publish job, csproj rule test. This fixes the measuring stick before the code moves.
2. Modular moves (D25–D29). Mostly file moves and constructor changes; low risk, clears the ground.
3. Store contract to immutable records (D8). Touches the handler everywhere, so do it before the split.
4. Split the handler and introduce `IA2ARequestHandler` (D17, D18). Pure refactor, behaviour unchanged, parity suite proves it.
5. Pipeline and telemetry (D19, D20, D21, D22).
6. Encoding and channel work (D9–D14), measured against the stage 1 baseline.
7. Ease-of-use surface (D1–D4, D6), then documentation pass (D5), then samples and README.

Stages 2 and 6 touch different files from 4 and 5 and could run in parallel by separate people once stage 3 is done.

## 10. Left to the builder

- Exact names of the split classes, filter delegate shape, and buffer pooling strategy, within the rules above.
- Benchmark thresholds and the short-run configuration for CI.
- Whether `SequencedEvent` carries bytes as `ReadOnlyMemory<byte>` with a pooled owner or as an immutable array; both satisfy D9.
- Internal layout of `A2A.Security.Http` and `A2A.Client.DependencyInjection`.
- Logging message text, metric names under the `A2A.Server` and `A2A.Client` meters.

## 11. Calls I made (not discussed with the user)

1. Principle ranking: safety as floor, then easy, performant, extendable, modular, with the "trade down, never up" rule.
2. One-call `AddA2A` on the ASP.NET Core package; the two-step form stays.
3. `UseA2A()` stays required; better error text instead of magic.
4. Model types stay in namespace `A2A`, not `A2A.Protocol`.
5. Immutable store records with mutable wire DTOs at the user boundary.
6. Bounded channels: `DropWrite` for bus subscriptions, `Wait` for the executor; default capacity 256.
7. Telemetry implemented as the first pipeline filter.
8. `IA2ARequestHandler` is an interface despite the abstract-class rule, for the same reason as `IA2AClient`.
9. New packages `A2A.Security.Http` and `A2A.Client.DependencyInjection`; `Logging.Abstractions` stays in the client.
10. gRPC mapping stays JSON-based, moved to UTF-8; no hand-written mapper.
11. PublicApiAnalyzers and a 10 percent benchmark regression threshold as the enforcement tools.
12. Order of work: measuring stick first, then modular, store, split, pipeline, perf, surface.
13. Reviewer round, all settled without the user (they asked to skip questions): no second task model (D8); hello world includes `UseA2A()` and the host's own authentication, `MapA2A` maps the card (G1, D1); extension operations are JSON-RPC and REST only, gRPC exempt, denied by default (D21); host filters run after authorization unless they opt in to run before (D19); empty `SupportedInterfaces` completed at first serve from the endpoint registry (D3); no gRPC stream resume (D22).
