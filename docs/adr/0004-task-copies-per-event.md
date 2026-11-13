# ADR-004: The list copies made for each task event

**Status:** Accepted (2026-11-12)
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
3. **Do not coalesce parts, and do not cap the stored lists by count to fix the copies (option C).** The history stays uncapped. The timeline does have a count cap, `A2ALimits.MaxTimelineEntries` (default 1000), but it was added separately, in the same release as this ADR, to stop status updates without a message from growing the stored task forever (`MaxTaskBytes` does not count them). It is a storage limit, not a fix for the copy cost, and option B supports it: dropping the oldest entries only moves the start of the shared buffer. A cap on the history can come later as an opt-in limit if a user asks for one.
4. Until option B ships, the durable-stores guide says a long task costs O(n) per event in memory (part of option D). Option B shipped with this ADR, so the guide describes the shipped cost instead (see "Results").

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
- **A store that serializes on each commit:** the serializer calls the getter, so every version is materialized. That is the "+ `ToList()` per event" row: 64.434 MB and 26.833 ms, against 64.240 MB and 9.893 ms today. Allocation stays the same order as today. The time can be up to about 2.7 times higher in the list operation alone, because the next copy re-wraps the list. The prototype's lazy `yield` enumerator inflates that figure, so a real implementation needs a benchmark. These stores already write O(n) bytes per commit. For Postgres the fix is a store change (ADR-003 option C), not this one. A follow-up could let the source-generated serializer enumerate the buffer without materializing it, but that is not checked yet. (Done: see "Follow-up: serializing without building the lists" under "Results".)

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
  - The paths that still read the getters and so build the lists (see the follow-up): the gRPC `DirectMapper` (stream
    events and send responses that carry a task), the v0.3 codec, and the server code that shapes a task for a response.
    Each would need a public, additive way to read a list without building it, because the packages do not share
    internals.

## Results

Built as decided. What differs from the sketch in option B:

- **The additive methods append in place on a copy the caller owns**, instead of a `CopyForUpdate` overload that takes the new history message and timeline entry: `ModelCopies.AppendToHistory(this AgentTask, Message)`, `AppendToTimeline(this AgentTask, TimelineEntry, int maxEntries = 0)` (experimental, like `TimelineEntry`) and `AppendParts(this Artifact, IEnumerable<Part>)`. Each does what `(task.History ??= []).Add(m)`, the timeline trim and `Parts.AddRange` did, without building the list. The follow-up commit (`A2ARequestHandler`) adds the history message first and the timeline entry later, once it knows the generation, and the batch path adds more parts to the artifact copy it already owns, so one method per list fits every call site. `CopyForUpdate` and `CopyWithParts` keep their signatures and become O(1) in the list lengths.
- **The timeline trim keeps a start offset.** A version of the shared buffer is (array, start, count); `MaxTimelineEntries` moves the start. A growth copies only the kept entries into an array twice their count, so with the 1000-entry cap the dropped prefix costs one 1000-entry copy per 1000 appends. Building a new list on each trim would have cost a 1000-entry copy per event (about 32 MB for 4,000 events), more than the whole target.
- **Thread safety.** A slot of the shared array is written once, by the appender that claimed it with a compare-and-swap on the array's high-water mark, before the version that sees it exists. Only a version that ends at the mark can claim; a fork from an older version, a second append from the same version, or an append that does not fit, copies its own items. A version never reads past its own count, so it never sees a slot it did not have when it was made. The first read of a property builds a `List<T>` and publishes it with `Interlocked.CompareExchange`: concurrent first readers get the same list, and from then on that list is the instance's content (a copy copies it once, O(n)). The setter replaces both.
- **Serializing stores** read every list of every version, so they build each one: O(n) per commit, as before. (The follow-up below removes that build.) Measured with an in-process store that serializes the task on each commit (a scratch run, not in the suite): 1,000 status messages 292.50 MB before, 300.54 MB after; 2,000: 981.33 MB before, 1,008.60 MB after (+3%, about the same time). The serializer's own output dominates.

`TaskCopyBenchmark`, `--job short`, the same 4-core dev machine (not the CI runner), before (`88b2a55`, the benchmark alone) and after:

| Benchmark | Before | After | Change |
|---|---:|---:|---:|
| `StatusMessages2000` | 33.69 MB, 30.9 ms | 6.74 MB, 19.8 ms | −80% |
| `StatusMessages4000` | 101.53 MB, 90.5 ms | 13.70 MB, 40.4 ms | −87% |
| `Appends1000` | 6.72 MB, 12.1 ms | 2.69 MB, 8.4 ms | −60% |
| `Appends2000` | 21.08 MB, 20.0 ms | 5.61 MB, 14.2 ms | −73% |
| `StreamingTaskBenchmark.ThousandEvents` | 2.80 MB, 6.1 ms | 2.70 MB, 6.4 ms | −3.5% |

Doubling the events now multiplies the allocation by 2.03 (status) and 2.09 (appends), against 3.0 and 3.1 before. 4,000 status messages allocate 13.70 MB, under the 20 MB target. The status case allocates less than the review's 143 MB before the change too, because `MaxTimelineEntries` already bounded the timeline copy at 1000 entries.

### Follow-up: serializing without building the lists

The "To revisit" item of option B, built. `A2AJson` serializes a task without building its lists, and the bytes do not
change.

- **How.** `A2AJson.Options` (and `V10Options`, `OptionsFor`, `CreateOptions`) add a contract modifier,
  `WriteSharedLists` (`src/A2A.Protocol/Json/A2AJson.cs`). It replaces the generated `history` and `timeline` of
  `AgentTask` and `parts` of `Artifact` with properties of the same JSON name, place, `IsRequired` and attribute
  provider, typed `IReadOnlyList<T>`. Their getter returns `LazyList<T>.View`: the list when it exists, else the shared
  `AppendList<T>` version (now an `IReadOnlyList<T>`), else null. The generated `IReadOnlyList<T>` converter writes the
  array with the same element converter as the `List<T>` one, so the bytes are the same; reading makes a `List<T>`, and
  the setter sets the public property with it, as before. The public property types stay `List<T>`: no API change.
- **Why this shape.** The serializer only sees a value through the property's getter, and the getter must return a
  `List<T>`, so the property has to be replaced, not just given a converter. A second, write-only property of the same
  name is refused (`JsonTypeInfo` rejects two properties with one name, even if one only reads). `[JsonIgnore]` on the
  public property plus an internal `[JsonInclude]` one would have done it in the generated code, but a
  `JsonSerializerContext` in another assembly cannot see internal members, so a user context that includes `AgentTask`
  would silently drop the history. A converter for the whole `AgentTask` would have to write every member by hand and
  would lose the `V10Options` modifier, which works on the contract's properties. The replacements are made with
  `JsonMetadataServices.CreatePropertyInfo`, the factory the generated code calls, which is trimming and AOT safe:
  `JsonTypeInfo.CreateJsonPropertyInfo` is marked `RequiresUnreferencedCode` and `RequiresDynamicCode`. The three
  `IReadOnlyList<T>` types are added to `A2AJsonContext`.
- **`Artifact` is created differently.** `Parts` is a C# `required` member, and the generated contract binds required
  members as constructor arguments by name and type. The `IReadOnlyList<Part>` property cannot bind to the
  `List<Part>` argument, so the modifier sets `CreateObject` (a public setting that turns the argument path off), and
  `parts`, like every member, is set after the object is created. `artifactId` stays JSON-required, a missing `parts`
  still reads as `[]` (`IJsonOnDeserialized`), and valid JSON reads into the same models as before.
- **What changed in a failed read.** The same exception, at the same path and position. The text of a conversion error
  can name another type: `` IReadOnlyList`1[A2A.Message] `` for a `history` that is not an array, where it said
  `` List`1[A2A.Message] ``; and for an `artifactId` that is not a string, `System.String` where it said `A2A.Artifact`.
  Where the message reaches a caller (the JSON-RPC and REST endpoints quote it in invalid-params errors), it can read
  differently. `SharedListJsonTests` records both.
- **Covered paths.** Every serialization through `A2AJson`'s options: `Serialize`, `SerializeToUtf8Bytes`,
  `SerializeToElement`, `JsonSerializer.SerializeAsync` with `A2AJson.GetTypeInfo`, `StreamResponse` and
  `TaskResponse`, `EventEncoder.Encode` and `MeasureUtf8`, and the gRPC `ProtoMapper` ProtoJSON path (it serializes
  the model with `A2AJson` first). The Postgres store's `A2AJson.Serialize(task)` is one of them.
- **Not covered.** Code that reads the getters: the gRPC `DirectMapper` (`src/A2A.Grpc/DirectMapper.cs:396-407`,
  `483`, for stream events and send responses), the v0.3 codec (`src/A2A.V0_3Compat/V03Codec.cs:152`, `187`), the
  response shaping in the server, and a user's own `JsonSerializerContext` or reflection serializer. They build the
  lists as before.
- **Tests.** `tests/A2A.Tests/SharedListJsonTests.cs` compares the bytes with the getter contract (the options without
  the modifier, `A2AJson.CreateOptions(hideV11, writeSharedLists: false)`) for every list state (null, empty, built,
  shared, trimmed by `KeepLast`, forked, changed by the owner after a read, set) and for 20 random runs of 300 updates,
  in both protocol shapes; checks that no write path builds a list; compares reads of valid and invalid JSON; checks
  the member order and settings; and writes old versions while another thread appends and forks. The netstandard smoke
  test checks artifacts and the 1.0 shape too. The AOT publish of `samples/EchoAgent` has no trim or AOT warning, and
  the smoke test passes against the native binary.

`TaskCopyBenchmark` with the two new `Serialized` cases (the single-commit in-memory store, wrapped so each commit also
runs `A2AJson.SerializeToUtf8Bytes` on the task), `--job short`, the same 4-core dev machine, one run each. Before is
`cbaf4b3` (the benchmark alone), after is the change:

| Benchmark | Before | After | Change |
|---|---:|---:|---:|
| `StatusMessages2000Serialized` | 1,008.63 MB, 3,259.8 ms | 953.87 MB, 3,607.4 ms | −5.4% |
| `Appends2000Serialized` | 72.50 MB, 181.6 ms | 40.21 MB, 196.6 ms | −44.5% |
| `StatusMessages4000` | 13.47 MB, 38.5 ms | 13.60 MB, 39.6 ms | +1.0% |
| `StatusMessages2000` | 6.72 MB, 14.5 ms | 6.76 MB, 13.5 ms | +0.6% |
| `Appends2000` | 5.62 MB, 10.1 ms | 5.44 MB, 10.7 ms | −3.2% |
| `Appends1000` | 2.82 MB, 8.2 ms | 2.29 MB, 12.4 ms | −18.8% |
| `StreamingTaskBenchmark.ThousandEvents` | 2.68 MB, 4.4 ms | 2.68 MB, 4.9 ms | 0% |

The status case still allocates about 1 GB, because the JSON of 2,000 versions of a growing task is that large: what
the change removes is the list builds and the copies that re-wrap them (about 54 MB). For the appends, where each part
is small, the builds were almost half. The cases that do not serialize do not change beyond noise (the `Appends1000`
drop is noise: the earlier ADR-004 run measured 2.69 MB). Times are noisy on this machine: the error of the
`StatusMessages2000Serialized` mean was ±2.2 s before and ±11.4 s after.

## Action items

1. [x] **Benchmark first.** Add `TaskCopyBenchmark` to `tests/A2A.Benchmarks`, with two cases: 4,000 status updates with a message, and 2,000 single-commit appends. For the appends, wrap the store without `IBatchTaskCommit`, as `tests/A2A.Benchmarks.Postgres/Program.cs:92-97` does, so no timer is needed. Regenerate `baseline.json` from the CI runner, as `tests/A2A.Benchmarks/README.md` says. (Added with a twin at half the size for each case. The baseline still has to be regenerated from a CI run of this branch: until then the benchmarks job fails on the four new entries.)
2. [x] **Build option B.**
   - Add the internal `AppendList<T>` to `A2A.Protocol`, with the lazy properties on `AgentTask` and `Artifact`.
   - Add the additive copy methods and record them in `PublicAPI.Unshipped.txt`.
   - Switch the engine call sites listed above.
   - Add tests next to `tests/A2A.Tests/ModelCopyTests.cs` and `TaskStoreOwnershipTests.cs`, and run `tests/A2A.NetStandard.Smoke`.
3. [x] **Measure success.** See "Results". `tests/A2A.Benchmarks.Postgres` needs Docker and was not re-run; the in-process serializing store above stands in for it. Compare with `python3 tests/A2A.Benchmarks/compare.py … --threshold 0.10`.
   - `TaskCopyBenchmark` should allocate O(n). Doubling n should about double the allocation, against about 3.5 times today. Target: under 20 MB for 4,000 status messages, against about 150 MB.
   - `StreamingTaskBenchmark.ThousandEvents` (baseline 5,668,262 bytes) must not regress.
   - Re-run `tests/A2A.Benchmarks.Postgres` to confirm the serializing path is no worse.
4. [x] **Versioning.** Option B is additive: a minor version, a `CHANGELOG.md` entry at release, and no analyzer. An analyzer and code fix in `src/A2A.Analyzers` / `A2A.Analyzers.CodeFixes` would only help option A, by rewriting `.Add` calls. It is not needed here.
5. [x] **Docs.** Item 2 shipped, so the durable-stores guide describes the shipped cost: O(1) per event in the engine, O(n) once per version whose lists are read.
6. [x] **Follow-up: serialize without building the lists.** See "Follow-up" under "Results". The durable-stores guide
   says `A2AJson` does not build the lists. `StatusMessages2000Serialized` and `Appends2000Serialized` are new: like
   item 1, `baseline.json` needs their entries from a CI run of this branch, and until then the benchmarks job fails on
   them.
