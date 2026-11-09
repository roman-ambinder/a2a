# SDK performance pass

Date: 2026-11-09. Status: design.

## 1. Goal

Make the hot paths of the SDK cheaper in memory and time, without changing behaviour and with **additive public API only**.

Starting point (local run, `--job short`, matches `tests/A2A.Benchmarks/baseline.json`):

| Benchmark | Mean | Allocated |
|---|---:|---:|
| `StreamingTaskBenchmark.ThousandEvents` | 11.5 ms | **18.5 MB** (~18 KB per event) |
| `SendMessageBenchmark.RoundTrip` | 422 µs | 39 KB |
| `CardVerifyBenchmark.Verify` | 214 µs | 33 KB |
| `JsonRpcEnvelopeBenchmark.Encode` | 158 ns | 480 B |

The findings come from a static review of server, wire format and client code. Every fix below must show its gain in a benchmark before it merges.

## 2. Decisions

| Question | Decision | Why |
|---|---|---|
| Scope | All four areas: server streaming, wire format, client, card signing. | User's choice. |
| Public API | Additive only. New members and new optional interfaces are fine. Nothing removed, no signature changed. | User's choice. Matches ADR-001. |
| Appending chunks | **Batch commits.** The engine drains every queued event, applies them to one task copy, and commits them together. Each event keeps its own sequence number. Stores opt in through a new optional interface. | `Artifact.Parts` is a public `List<Part>`, so a shared list type would break the API. Batching makes a fast agent close to linear and needs no change to readers. |
| Stores that do not opt in | Keep today's path: one commit per event. The parts list is sized right once (no second grow). | ADR-001 rule 2: a missing capability must still work. |
| Delivery | One PR per area (section 4). Each PR updates `baseline.json` from the CI run, as `tests/A2A.Benchmarks/README.md` says. | Easier to review and to revert. |
| Proof | Allocation numbers from BenchmarkDotNet. Time is reported but not gated (CI runners are noisy). | Same rule as the existing CI gate. |

## 3. Design

### 3.1 Batch commits (server)

Today `TaskExecutionEngine.ConsumeAsync` reads one event, copies the task, and commits. For an artifact append, `ModelCopies.CopyWithParts` (`src/A2A.Protocol/Models/ModelCopies.cs:39`) copies the whole parts list and then grows it once more. With n chunks that is about 24·n bytes per chunk, so ~12 MB of the 18.5 MB.

New optional interface in `A2A.Server` (namespace of `TaskStore`):

```csharp
public interface IBatchTaskCommit
{
    /// Stores the final record and all events in one step. Events get consecutive sequence numbers, in order.
    ValueTask<BatchCommitResult> CommitBatchAsync(TaskBatchCommit commit, CancellationToken cancellationToken = default);
}
public sealed record TaskBatchCommit(TaskRecord Record, long ExpectedVersion, IReadOnlyList<SequencedEvent> Events, IReadOnlyList<PushDelivery> Deliveries);
public sealed record BatchCommitResult(TaskRecord Record, long FirstSequence);
```

Engine rules:

- After reading one event, it takes more with `TryRead`, up to 64 (an internal constant, not an option). It never waits for more.
- Only plain appends go into a batch: an append of parts with no name, description or metadata that does not seal the artifact. Their effect on the task and its size is known without walking the task. Anything else is stored on its own, as today.
- It applies all of them to **one** task copy. An append adds to the copy's list, which this batch owns, so no copy per chunk.
- Size limit, state machine and validation run per event, in order, as today. An event that would be refused ends the batch before it; that event then takes the normal single path.
- A terminal status ends the batch.
- The first event that creates the task is never batched (it has idempotency and push-config work).
- After the commit, events are published one by one in sequence order, as today. Subscribers see no difference.
- If the store does not implement `IBatchTaskCommit`, or the batch has one event, the engine uses `CommitAsync` as today.

Stores: `InMemoryTaskStore` and `PostgresTaskStore` implement it (Postgres: one transaction, one snapshot write, N event rows). Both get contract tests: sequence numbers, version check, a `ConcurrencyException` stores nothing.

### 3.2 Server streaming overhead

| Fix | Where |
|---|---|
| Size the list once in `CopyWithParts`, no LINQ. | `ModelCopies.cs:39` |
| New virtual `EventStore.ReadRangeAsync(after, before)` (fallback: `ReadAfterAsync` with a stop). In-memory and Postgres read only the range. Streams fill gaps with it. | `EventStore.cs`, `TaskStreamer.ReadContiguousAsync` |
| Event log becomes a ring buffer; `ReadAfterAsync` copies only the asked range and gets an upper bound. No `RemoveRange(0, …)`. | `InMemoryEventStore.cs:51,65` |
| Skip the in-order iterator when the event is a duplicate (`live.Sequence <= position.Last`). | `TaskStreamer.cs:323,328,505` |
| One long-lived cancellation registration instead of `WaitToReadAsync(token)` + `AsTask` + `WhenAny` per event. | `TaskStreamer.cs:169,342,509` |
| Re-auth check by timestamp between events, not a `Task.Delay` per event. | `TaskStreamer.LimitStreamAsync:680` |
| `ApplyAndStoreAsync` returns `ValueTask`. | `TaskExecutionEngine.cs:808` |
| Keep the committed record on `Execution`; re-read only after a `ConcurrencyException`. | `TaskExecutionEngine.cs:647` |
| Cache scope key and lock stripe on `Execution`. Plain loop instead of `FindIndex(lambda)`. | `TaskExecutionEngine.cs:170,1013,1041`, `TaskLocks.cs:18` |

Looked at and left out of PR 1 (measured with an allocation profile of the benchmarks):

- Message deep copies per request: they keep the stored history safe from an agent that changes its message. Not worth the risk.
- Event copies with host filters: a filter may change the events. Only with host filters.
- Cap on span events per stream: changes what traces show. Owner's decision, not a performance fix.
- Usage meter `JsonDocument`: only when an agent reports usage; small.
- `ShapeHistoryAsync` layer: small next to the JSON work.
- Request hash: it uses the JCS canonicalizer; it gets faster with the canonicalizer rewrite in PR 5.
- Per-request `ChannelClosedException` objects: made by .NET when a channel completes; never thrown.

### 3.3 Wire format (server side)

| Fix | Where |
|---|---|
| SSE: write each frame straight into `Response.BodyWriter`. JSON-RPC envelope prefix `{"jsonrpc":"2.0","id":…,"result":` is built once per stream; frame = prefix + stored event bytes + `}`. Event id formatted into the span. One flush per frame. | `JsonRpcEndpoint.cs:241`, `JsonRpcWriter.cs:206`, `HttpPlumbing.cs:217-277` |
| SSE keep-alive: one `PeriodicTimer` per stream; call `AsTask()` only when `MoveNextAsync` did not finish at once. | `HttpPlumbing.cs:346-361` |
| Unary responses: write the pooled buffer to the `PipeWriter`, no `ToArray()`. | `HttpPlumbing.cs:144`, `JsonRpcEndpoint.cs:228` |
| Converters: error text only on error paths; match names with `ValueTextEquals("…"u8)`; static `JsonEncodedText` names; `GetBytesFromBase64`; build `Part` once (no `with`); timestamps formatted into a stack span. | `OneOfConverter.cs`, `PartConverter.cs`, `ProtoEnumConverter.cs`, `TimestampConverter.cs` |
| Source-gen check: confirm whether `A2AJson` falls back to metadata mode. If it does, move settings into `[JsonSourceGenerationOptions]` and attach converters by attribute. Ship only if a benchmark shows a gain. | `A2AJson.cs:27-43` |
| Small per-request costs: no LINQ when the extensions header is absent; no route-value dictionary copy; REST query names looked up once. | `HttpPlumbing.cs:25,98`, `RestEndpoint.cs:93,295` |

### 3.4 Client

| Fix | Where |
|---|---|
| Internal `SseReader` mode that yields event data as UTF-8 bytes (valid until next read). JSON-RPC deserializes `StreamResponse` straight from the `result` range that `TryScan` already finds. No string, no `JsonDocument`. Public `SseEvent` unchanged. | `SseReader.cs:161`, `JsonRpcTransport.cs:158-215`, `HttpJsonTransport.cs:172` |
| Unary JSON-RPC: bind the result from the byte range; read with `ResponseHeadersRead` into one pooled buffer with a size cap. | `JsonRpcTransport.cs:114-124`, `HttpJsonTransport.cs:147` |
| Card cache hit: re-verify only when the trust policy changed (a generation counter), not every call. One fetch per key at a time (`Lazy<Task>`). No serialize-then-parse on the verified path. | `A2ACardResolver.cs:175-299` |
| Factory: one shared handler with `PooledConnectionLifetime`; reuse the resolver; cache DNS checks briefly. `A2AClient` gets `IDisposable`/`IAsyncDisposable` (additive) and disposes what it owns. | `A2AClientFactory.cs:61-222`, `A2AClient.cs:22` |
| DI: set `PooledConnectionLifetime` on the primary handler. | `A2AClientServiceCollectionExtensions.cs:127` |
| Headers: pre-join extensions; `TryAddWithoutValidation("Accept", …)`; no trace closure when `Activity.Current` is null; request body sent from the pooled buffer (custom `HttpContent`). | `HttpTransportBase.cs:37-44`, `JsonRpcWriter.Request` |
| Resume: compare task id as a span; reuse `event`/`id` strings when the bytes match. | `ResumableStream.cs:210`, `SseReader.cs:194` |
| Outbox: priority queue on `DueAt`; store the tenant on the entry. | `InMemoryClientOutboxStore.cs:53`, `ClientOutboxDispatcher.cs:266` |

### 3.5 gRPC

| Fix | Where |
|---|---|
| Quick: server streaming parses the stored event bytes (`evt.Json`) instead of serializing the event again. | `A2AGrpcService.cs:140` |
| Quick: reuse the writer/reader buffers in `ProtoMapper` (no new `StreamWriter`/`StreamReader` per call). | `ProtoMapper.cs:46,73` |
| Full: hand-written model ↔ proto mapper. Raw parts with `UnsafeByteOperations.UnsafeWrap`; `JsonElement` ↔ `Struct` by a direct walk. The ProtoJSON path stays as the reference in tests: both mappers must give equal results on the full test corpus. | `ProtoMapper.cs` |
| Client: cache channels per address in the factory; dispose them with the client. | `GrpcTransport.cs:62,338` |
| Extension service: no `GetRawText` + copy; `JsonElement.ParseValue` instead of `Parse` + `Clone`. | `A2AGrpcExtensionService.cs:67,83` |

### 3.6 Card signing and checking

| Fix | Where |
|---|---|
| Card → `JsonNode` with `SerializeToNode`, no string step. | `AgentCardCanonicalizer.cs:105` |
| One prune pass that returns both payload and prune log (today: two clone + prune passes). Member paths built only when something is removed. | `AgentCardVerifier.cs:100,112`, `AgentCardCanonicalizer.cs:272-400` |
| JCS writes UTF-8 into an `IBufferWriter<byte>`; sort keys with an array sort, not `OrderBy`; numbers formatted without temp strings. Output bytes must stay identical: RFC 8785 test vectors plus existing tests. | `JsonCanonicalizer.cs` |
| Span-based base64url (`System.Buffers.Text.Base64Url` on net10, a span helper elsewhere). Signing input built in one rented buffer. | `Jwk.cs`, `AgentCardVerifier.cs:255` |
| Cache the imported public key per JWK. | `AgentCardVerifier.cs:184` |
| `node.Deserialize(typeInfo)` instead of `ToJsonString` + parse. | `AgentCardVerifier.cs:139` |

## 4. Delivery (one PR each, in this order)

1. **Server streaming** (3.1, 3.2). Biggest win.
2. **Server wire format** (3.3).
3. **Client** (3.4).
4. **gRPC** (3.5). Quick fixes first, full mapper in the same PR only if tests show equal output.
5. **Card signing** (3.6).

## 5. Benchmarks

New benchmarks, added in the PR that needs them:

| Benchmark | PR | Measures |
|---|---|---|
| `StreamingTaskBenchmark.ThousandEventsSlowAgent` (agent yields between chunks, so no batching) | 2 | The non-batched path stays good. Added together with a baseline from CI. |
| `RestStreamingBenchmark` | 2 | SSE over REST. |
| `SseParseBenchmark` (client only, canned bytes) | 3 | Client per-event cost alone. |
| `CardResolveCachedBenchmark` | 3 | A cache hit. |
| `GrpcStreamingBenchmark`, `ProtoMapperBenchmark` | 4 | gRPC per-event cost. |
| `CanonicalizeBenchmark` | 5 | JCS alone. |

Targets (to check, not promises): streaming 1,000 events under 3 MB; SendMessage under 25 KB; card verify under 15 KB.

## 6. Risks and how we check

- **Batching changes store timing.** Contract tests for sequence order, version checks and push deliveries per event. Existing streaming, resubscribe and push tests must pass unchanged.
- **Buffers reused across awaits.** Byte views from `SseReader` are valid only until the next read; no public type exposes them.
- **JCS output must not change by a single byte**: signatures depend on it.
- **gRPC mapper drift.** Both mappers run on the same corpus in tests; any difference fails.
- **netstandard2.0.** Span helpers that do not exist there go through `src/Shared` polyfills, built only for that target.

## 7. Out of scope

- Redis bus and S3/Azure stores (not on the measured paths).
- A cluster-wide change to the store contract (ADR-001 status unchanged).
- Time-based CI gates.
