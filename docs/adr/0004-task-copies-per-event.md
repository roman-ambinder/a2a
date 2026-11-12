# ADR-004: The list copies made for each task event

**Status:** Proposed (2026-11-12)
**Date:** 2026-11-12
**Deciders:** Repository owner

## Context

[ADR-003](0003-task-writes-per-event.md) removed the quadratic *write* cost of artifact appends on Postgres. It left one cost open: *"The in-memory copy of the part list is not addressed. That copy is in the engine, not the store."* Its "To revisit" lists that copy. This ADR is about that copy, and the same copy of the history and timeline lists.

How a task changes today:

- **Each event copies three lists.** `ApplyToCopy` (`src/A2A.Server/Execution/TaskExecutionEngine.cs:1824`) starts every status and artifact update with `current.CopyForUpdate()` (lines 1830 and 1850). `CopyForUpdate` (`src/A2A.Protocol/Models/ModelCopies.cs:20-34`) makes a new `Artifacts`, `History` and `Timeline` list. `CopyList` (lines 37-43) copies every item. A status update then adds the status message to `History` and an entry to `Timeline` (`TaskExecutionEngine.cs:1835-1836`). Client messages take the same path (`src/A2A.Server/A2ARequestHandler.cs:1023-1025`).
- **Each append copies the artifact's parts.** `CopyWithParts` (`ModelCopies.cs:50-71`) builds a new `Parts` list with the old parts and the new ones. It is called once per append (`TaskExecutionEngine.cs:1862`), and once per batch (line 1485). Later chunks of the same batch go into that copy with `AddRange` (line 1493).
- **Why the copies exist.** A stored task is a snapshot that readers share. `TaskStore` says the store owns what it returns and *"callers never mutate it"* (`src/A2A.Server/Storage/TaskStore.cs:110`). The in-memory store keeps and returns those instances without copying (`InMemoryTaskStore.cs:8-10`). `TaskShaper.Shape` hands the stored lists to responses as they are (`src/A2A.Server/Pipeline/TaskShaper.cs:28-33`, `41-46`). So a new version must not change a list that an old version holds. Two `List<T>` objects cannot share one backing array, so the only safe way to append is to copy.
- **Batching helps only when events queue.** Chunks are merged only when the next one is already waiting in the channel (`reader.TryPeek`, `TaskExecutionEngine.cs:911-916`), up to `MaxCommitBatch` = 64 (line 89). Status updates are never batched (`IsBatchable`, line 1437). A producer slower than a commit, such as a model streaming tokens, gets one copy per chunk.
- **Stores that serialize pay O(n) anyway.** Postgres writes the whole task JSON on each commit (ADR-003). With `PostgresStoreOptions.ArtifactRows` the parts move to their own rows, and an append writes only the new parts (ADR-003, "Results with Option C"). History and timeline still sit in the task JSON, so a status update with a message still rewrites them. `PostgresArtifactRows.WithoutParts` also calls `CopyForUpdate` on each write (`src/A2A.Server.Postgres/PostgresArtifactRows.cs:26-31`).
- **Only the timeline is capped by count.** `MaxHistoryLengthReturned` and `MaxTimelineLengthReturned` (default 100) cap only what a response returns. `MaxTimelineEntries` (default 1000, `A2ALimits.cs:73`) keeps the newest stored timeline entries, which bounds the timeline copy at 1000 entries. History has no count cap: it grows until `MaxTaskBytes` (64 MiB).

### Measurements

The review measured these on main after PR #178: 4,000 status updates with a message, 143.4 MB and 182 ms; 4,000 bare status updates, 72.95 MB; paced appends, 3.38 MB for 500 and 24.97 MB for 2,000.

I reproduced them with a scratch harness, not in the repo. It uses the in-memory stores, an in-process TestServer and a JSON-RPC streaming client, as `BenchHost` does. It counts `GC.GetTotalAllocatedBytes(true)` over one task after a warm-up task. "Paced" waits `Task.Delay(1)` before each append, so chunks do not queue. One run each, 4 cores:

| Scenario | n | Allocated | Time |
|---|---:|---:|---:|
| status + message | 1000 | 13.66 MB | 107 ms |
| status + message | 2000 | 43.07 MB | 227 ms |
| status + message | 4000 | 150.49 MB | 381 ms |
| bare status | 2000 | 22.19 MB | 62 ms |
| bare status | 4000 | 76.11 MB | 127 ms |
| paced append | 500 | 3.64 MB | 2130 ms (mostly the delay) |
| paced append | 2000 | 26.32 MB | 8288 ms (mostly the delay) |
| fast append (batched) | 2000 | 6.14 MB | 18 ms |

When *n* doubles, allocation grows about 3.2–3.5 times, so the cost is quadratic. A first run of the 4,000 status-and-message case measured 150.12 MB and 662 ms, so treat the times as rough.

A second scratch program (labelled **prototype**) measures only the list operation: *n* appends, each making a new version from the latest one and keeping the old version intact. Median of 7 runs, one thread:

| Strategy | n=500 | n=2000 | n=4000 | Time, n=4000 |
|---|---:|---:|---:|---:|
| `List<T>` copy per event (today's `CopyList`) | 1.030 MB | 16.120 MB | 64.240 MB | 9.893 ms |
| `ImmutableList<T>.Add` | 0.228 MB | 1.102 MB | 2.395 MB | 1.014 ms |
| `ImmutableArray<T>.Add` | 1.014 MB | 16.056 MB | 64.112 MB | 8.740 ms |
| `AppendList<T>` prototype (shared buffer) | 0.025 MB | 0.097 MB | 0.194 MB | 0.057 ms |
| `AppendList<T>` + `ToList()` per event | 1.055 MB | 16.217 MB | 64.434 MB | 26.833 ms |
| Coalesced text: `string.Concat` of 5-char chunks | 1.265 MB | 20.060 MB | 80.120 MB | 10.327 ms |

The list copy explains most of what the server allocates. A status update with a message copies two lists of 4,000: 2 × 64.2 MB = 128 MB of the 150 MB. A bare status update copies the timeline: 64 MB of the 76 MB.

`AppendList<T>` is the prototype's data structure. Every version shares one backing array and keeps its own count. An append to the newest version claims the next slot with a compare-and-swap, writes it, and returns a longer view. Older versions never read past their own count, so they do not change. An append to an older version (a fork), or to a full array, copies and doubles the array. Its smoke test passes: forks, and 8 threads appending 10,000 items each from one snapshot, leave every snapshot intact.

## Decision

1. **Adopt option B.** History, timeline and artifact parts share one buffer per list inside the model types. The public property types stay `List<T>`.
2. **Do not change the public types (option A)** in the 0.x line. Look at it again before 1.0, with what option B taught.
3. **Do not coalesce parts, and do not cap the stored lists by count (option C).** A count cap can come later as an opt-in limit if a user asks for one.
4. Until option B ships, the durable-stores guide says a long task costs O(n) per event in memory (part of option D).

## Options considered

### Option A: An append-only list as the public type

`AgentTask.History`, `AgentTask.Timeline` and `Artifact.Parts` would become `IReadOnlyList<T>`, or a public `AppendList<T>`.

| Dimension | Assessment |
|---|---|
| Complexity | Medium in the SDK; the cost falls on users |
| Cost | Breaks the shipped API: `PublicAPI.Shipped.txt:138` (`History`), `:187` (`Artifact.Parts`), `:1238` (`Timeline`) |
| Scalability | O(1) per append everywhere, also for third-party code |
| Team familiarity | Medium |

**Pros:**
- The type itself says the lists are immutable, which `TaskStore` today can only say in a remark.
- No hidden state, and no "materialized or not" case.

**Cons:**
- **Users.** Code that calls `.Add`, `.AddRange`, `.ConvertAll`, `.Find` or `.RemoveAll`, or passes the lists where a `List<T>` is expected, stops building. That includes agents, because an agent gets its own copy of the task and may change it (`AgentExecutor.cs:51-56`, `TaskExecutionEngine.cs:353`). It also includes client code and custom stores. In `src`, `ModelCopies` and `PostgresArtifactRows.CopyParts` would have to change too.
- **Inconsistency.** `Message.Parts` (`src/A2A.Protocol/Models/Core.cs:88`) would stay a `List<T>`.
- **JSON.** `A2AJsonContext` is source-generated with no reflection fallback (`src/A2A.Protocol/Json/A2AJsonContext.cs:9-19`). With `IReadOnlyList<T>`, the generator reads a `List<T>`, and the engine must wrap it on the first append. With a custom type, it needs a `[JsonConverter]`. Either one is trim- and AOT-safe if it does not use reflection (`IsAotCompatible`, `Directory.Build.props:63`).
- **netstandard2.0.** `A2A.Protocol` also targets `netstandard2.0` (`A2A.Protocol.csproj:7`). `Interlocked` exists there. Collection expressions (`Parts = [..]`) on a custom type need `[CollectionBuilder]`, which PolySharp 1.17.0 polyfills: its generator contains the attribute, but I did not build that case.
- **Versioning.** Before 1.0 a minor version may break the API (`docs/site/guides/versioning.md:7`), so this is allowed. But it touches the most-used type of every package, and gives no `[Obsolete]` period (that rule starts at 1.0, line 19).

### Option B: Keep `List<T>` public, share a buffer inside (recommended)

`AgentTask` and `Artifact` keep their public `List<T>` properties. Inside, each of the three lists is either a materialized `List<T>` or an internal `AppendList<T>`.

- **`CopyForUpdate` and `CopyWithParts` become O(1)** when the source has not been materialized: the copy shares the source's `AppendList<T>`. Their documented results stay the same. The copy still *has a new `History` list* (`ModelCopies.cs:12`), because a new list is built when someone first reads the property.
- **The engine appends through new additive methods.** For example, `CopyForUpdate(this AgentTask, Message? history, TimelineEntry? timeline)` and an internal-buffer path for `CopyWithParts`, so it never reads the getter. They must be public: the shipped packages do not use `InternalsVisibleTo` between themselves (`src/Shared/AI/ExecutorTexts.cs:1-3`; `A2A.Protocol.csproj:25` names only `A2A.Tests`). They go in `PublicAPI.Unshipped.txt`.
- **The first read of the getter materializes the list.** It builds a `List<T>` once and publishes it with `Interlocked.CompareExchange`, so readers sharing a stored instance get the same list. From then on that instance's `List<T>` is the truth. The next copy wraps it in a new buffer: one O(n) copy, the same as today.
- **The setter** replaces both.

**Who pays, and when:**
- **In-memory store, nobody reading:** O(1) per event, amortized. This is the prototype's 0.194 MB row, against 64.240 MB.
- **A reader of a version** (`GetTask`, a resubscribe snapshot): O(n) once for that version. Today it costs nothing, because the list already exists.
- **A store that serializes on each commit:** the serializer calls the getter, so every version is materialized. That is the "+ `ToList()` per event" row: 64.434 MB and 26.833 ms, against 64.240 MB and 9.893 ms today. Allocation stays the same order as today. The time can be up to about 2.7 times higher in the list operation alone, because the next copy re-wraps the list. The prototype's lazy `yield` enumerator inflates that figure, so a real implementation needs a benchmark. These stores already write O(n) bytes per commit. For Postgres the fix is a store change (ADR-003 option C), not this one. A follow-up could let the source-generated serializer enumerate the buffer without materializing it, but that is not checked yet.

| Dimension | Assessment |
|---|---|
| Complexity | Medium: one internal collection, three properties, two or three additive copy methods, plus engine changes at `TaskExecutionEngine.cs:1832-1836`, `1481-1493`, `1860` and `A2ARequestHandler.cs:1023-1025` |
| Cost | No breaking change; hidden state in two model types |
| Scalability | O(1) per event in the engine; serializing stores no worse than today in allocation |
| Team familiarity | Medium: the ownership rule already exists; the lock-free append is new |

**Pros:**
- No user code changes.
- The ownership contract stays as written.
- It fixes the in-memory store ("For development and tests", `InMemoryTaskStore.cs:8`) and the engine path that every store shares.

**Cons:**
- A model type that looks like a plain DTO gets hidden state and a thread-safe lazy getter.
- A contract violation, such as a reader mutating a materialized list, now affects only that instance and its later copies. That is no worse than today, but harder to see.

### Option C: Caps and coalescing only

- **A stored count cap** (keep the newest N history messages and timeline entries in the task) would bound each copy at N. But agents get the stored history (`AgentExecutor.cs:51-56`), so they would see less. A host that raises `MaxHistoryLengthReturned` could no longer return older entries. For the timeline this is now done (`MaxTimelineEntries`): resume reads the event store, not the stored timeline, so trimming it does not change resume. History stays uncapped.
- **Coalescing consecutive text parts** keeps the part count at 1. But the text string must then be copied on each append. The prototype's `string.Concat` row allocates more than the list copy: 80.120 MB against 64.240 MB at n=4000. It also changes what clients see: part boundaries, and per-part metadata that cannot be merged.

### Option D: Change nothing, document it

Today 4,000 status messages cost about 150 MB of short-lived allocation and 0.18–0.66 s per task (the review and the runs above). No user has reported it. The cost is real for agents that report progress often, or stream tokens slowly into one artifact.

## Trade-off analysis

- **A** is the cleanest model, but every user pays to fix a cost inside the server.
- **B** removes the copy where it arises (64.240 → 0.194 MB in the prototype) with no break. Serializing stores stay as they are in allocation and already pay O(n) to write.
- **C** changes what agents and clients see, and coalescing does not remove the copy.
- **D** leaves a known quadratic cost on the default path.

## Consequences

- **Easier:** long-running agents with frequent status messages, and slow token streams on the in-memory store, stop allocating O(n²).
- **Harder:** `AgentTask` and `Artifact` get internal state that tests must cover. The cases are a concurrent first read, a fork from an old version, a set after a read, and JSON round-trips on both targets.
- **To revisit:**
  - Option A before 1.0.
  - History and timeline rows for Postgres (the same idea as ADR-003 option C), if status-heavy tasks show up in WAL.

## Action items

1. [ ] **Benchmark first.** Add `TaskCopyBenchmark` to `tests/A2A.Benchmarks`, with two cases: 4,000 status updates with a message, and 2,000 single-commit appends. For the appends, wrap the store without `IBatchTaskCommit`, as `tests/A2A.Benchmarks.Postgres/Program.cs:92-97` does, so no timer is needed. Regenerate `baseline.json` from the CI runner, as `tests/A2A.Benchmarks/README.md` says.
2. [ ] **Build option B.**
   - Add the internal `AppendList<T>` to `A2A.Protocol`, with the lazy properties on `AgentTask` and `Artifact`.
   - Add the additive copy methods and record them in `PublicAPI.Unshipped.txt`.
   - Switch the engine call sites listed above.
   - Add tests next to `tests/A2A.Tests/ModelCopyTests.cs` and `TaskStoreOwnershipTests.cs`, and run `tests/A2A.NetStandard.Smoke`.
3. [ ] **Measure success.** Compare with `python3 tests/A2A.Benchmarks/compare.py … --threshold 0.10`.
   - `TaskCopyBenchmark` should allocate O(n). Doubling n should about double the allocation, against about 3.5 times today. Target: under 20 MB for 4,000 status messages, against about 150 MB.
   - `StreamingTaskBenchmark.ThousandEvents` (baseline 5,668,262 bytes) must not regress.
   - Re-run `tests/A2A.Benchmarks.Postgres` to confirm the serializing path is no worse.
4. [ ] **Versioning.** Option B is additive: a minor version, a `CHANGELOG.md` entry at release, and no analyzer. An analyzer and code fix in `src/A2A.Analyzers` / `A2A.Analyzers.CodeFixes` would only help option A, by rewriting `.Add` calls. It is not needed here.
5. [ ] **Docs.** Until item 2 ships, say in the durable-stores guide that a long task costs O(n) per event in memory.
