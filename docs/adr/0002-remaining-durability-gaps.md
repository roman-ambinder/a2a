# ADR-002: What to do with the last open gap items before the first published release

**Status:** Proposed
**Date:** 2026-11-12
**Deciders:** Repository owner

## Context

The gap analysis (`docs/2026-11-07-a2a-gap-analysis.md`) has four open items left:

| # | What is still missing (from the gap analysis) |
|---|---|
| D1 | No Cosmos store. |
| D2 | No mutation id per store write. |
| D3 | No Azure Service Bus event bus. |
| D33 | No context lifecycle policy hook. |

What already exists for each:

- **D1, stores.** The gap analysis says: "Postgres and Redis ship, Cosmos does not." Backends plug in through public abstract classes: `TaskStore`, `EventStore`, `PushOutbox`, `IdempotencyStore`, `ExecutionCoordinator`, `PushConfigStore` and `ArtifactObjectStore`.
- **D3, event bus.** `TaskEventBus` is a public, non-sealed class (`src/A2A.Server/TaskEventBus.cs`). `RedisTaskEventBus` derives from it. Its documentation says Redis pub/sub is at most once, and that the request handler "drops duplicates and fills gaps from the `EventStore`". Cancel requests cross instances through `ExecutionCoordinator`, which has Postgres and Redis implementations.
- **D2, writes.** `TaskStore.CommitAsync` is already a compare-and-set. `TaskCommit` carries `ExpectedVersion`, and a stale version throws `ConcurrencyException`. The engine retries on that exception: it reads the task again and applies the event again (`TaskExecutionEngine.cs`, around line 1178). What is missing is a way to tell "my write already landed" apart from "someone else wrote". A commit can succeed in the database while the caller sees an error, for example a timeout after the transaction committed.
- **D33, contexts.** `A2ARetentionOptions.OrphanContextRetention` (default 7 days) and `ConversationMemory` exist. A context is deleted only by that fixed age, and there is no hook for a policy of your own.

Two facts constrain the decision:

1. **No version is published yet.** The gap analysis says "package validation against a baseline version needs a first published version". ADR-001 says that until then the abstract store surface "can still change at no cost", and that after the release it cannot.
2. **ADR-001 is still Proposed, and its action items are open.** `TaskStore.GetForMaintenanceAsync` is still abstract (`TaskStore.cs:416`), though ADR-001 names it as a candidate to make virtual. `docs/site/guides/versioning.md` does not state the rule yet.

## Decision

Treat the four items differently, by how much the first release locks them in:

1. **D2: settle it before the first release.** It touches the store contract, which is the one surface ADR-001 freezes.
   - First prove whether the failure is real: a test with a store that applies a commit and then throws.
   - If it is real, add an optional mutation id to `TaskCommit`. A store that keeps the id and finds it already applied returns the existing result instead of applying the change again.
   - The engine relies on this only through a capability interface, as ADR-001 rule 2 says.
   - A store without the capability keeps today's behaviour.
2. **D1 and D3: leave them out of this repository, and document the extension points instead.** Cosmos and Azure Service Bus each need a new package with an Azure dependency, and an emulator that CI does not run. Postgres and Redis already cover durable stores and cross-instance events. The base classes are public, so anyone can build these providers outside this repository.
3. **D33: do it only when there is a concrete use case.** A hook is an API that must be kept once it ships. Today's fixed retention covers the stated need.

## Options considered

### Option A: Build all four before the first release

| Dimension | Assessment |
|---|---|
| Complexity | High: two new Azure packages, one contract change, one new hook |
| Cost | New CI jobs and emulators (Cosmos, Service Bus), and their upkeep |
| Scalability | More backends for users who run Azure |
| Team familiarity | High for D2 and D33 (existing code). Unknown for Cosmos and Service Bus: nothing in the repository uses them yet |

**Pros:** The gap analysis is closed in full.

**Cons:** It delays the release. It adds two packages whose tests need infrastructure that CI does not have. Any mistake in them is frozen at the release.

### Option B: Release first, decide all four later

| Dimension | Assessment |
|---|---|
| Complexity | Low now |
| Cost | D2 becomes a contract change after the freeze, so it falls under ADR-001's rules or needs a major version |
| Scalability | Unchanged |
| Team familiarity | High |

**Pros:** It is the fastest way to a release.

**Cons:** D2 is the one item that is cheap now and expensive later. Shipping without a decision on it gives up the window ADR-001 describes.

### Option C: D2 before the release, D1 and D3 out of this repository, D33 on demand (chosen)

| Dimension | Assessment |
|---|---|
| Complexity | Medium: one investigation, then possibly one optional capability |
| Cost | No new infrastructure in CI |
| Scalability | Users who need Cosmos or Service Bus build providers on public base classes |
| Team familiarity | High: the capability pattern already exists (`TryRegisterExclusiveAsync`, the batch commit capability on `TaskStore`) |

**Pros:**
- It spends effort where the release locks things in.
- It adds no packages that CI cannot test.

**Cons:**
- Azure users get no first-party Cosmos store or Service Bus bus.
- D2 may turn out to need no change, in which case the investigation is the whole cost.

## Trade-off analysis

The deciding question is which items get more expensive after the release.

- **D2 does.** It touches `TaskStore` and `TaskCommit`, the contract ADR-001 freezes.
- **D1 and D3 do not.** They are new packages on public base classes. They can be added in any later version without breaking anyone.
- **D33 does not either.** A new hook is an addition.

Option A pays the highest cost for the items that are cheapest to defer. Option B defers the one item that is cheapest now.

The main risk of Option C is the D2 investigation itself. If the failure exists, the engine's current recovery could store an event twice. That is a correctness problem for a store that holds task state. The test comes first so the decision rests on evidence rather than on this ADR.

## Consequences

- **Easier:** the first release ships without new Azure packages. CI keeps the infrastructure it runs today (Postgres, Redis, MinIO, Azurite).
- **Harder:** an Azure user who wants Cosmos or Service Bus writes the provider. The extension-point docs must be good enough for that.
- **To revisit:**
  - D1 and D3, if users ask for them.
  - D33, when a concrete policy is requested.
  - ADR-001's open action items, which D2 depends on.

## Action items

1. [x] Write a test with a `TaskStore` that applies a commit and then throws (a timeout after the commit). Record what the engine does: whether the event is stored twice, and whether the task ends up correct.

   Done in `tests/A2A.Tests/AmbiguousCommitTests.cs`. The store wraps the in-memory one, applies the first or second artifact commit of the `task` scenario, and then throws. Results on 2026-11-12:

   | The store throws | Commit that fails | Result |
   |---|---|---|
   | `TimeoutException` | 1 (creates the artifact) | The task ends `FAILED` with `part1` only. The run stops, so `part2` and `COMPLETED` are lost although the agent produced them. |
   | `TimeoutException` | 2 (last chunk) | The task ends `FAILED` with both parts. `COMPLETED` is lost. |
   | `ConcurrencyException` | 1 | The task ends correct (`COMPLETED`, `part1, part2`), but the event log holds the `part1` event twice (sequences 3 and 4). A stream replay or push delivery would see it twice. |
   | `ConcurrencyException` | 2 | Correct. The append is refused on the sealed artifact, so nothing is stored twice. |

   So D2 is real. A timeout after the commit makes the engine fail a task whose write landed, and a version conflict caused by the caller's own write stores the event twice.

   - The shipped stores never produce the conflict case themselves: `PostgresTaskStore` has no internal retry, and there is no Redis task store. It can happen with a store that retries internally.
   - The timeout case applies to any store whose commit answer can be lost after the transaction committed.
   - A conflict on an append that is not the last chunk (tested on 2026-11-12, `many-chunks` scenario, commit 2 is
     chunk `1`):

     | Store | The store throws | Result |
     |---|---|---|
     | Without `ITaskCommitLookup` | `ConcurrencyException` | The task ends `COMPLETED`, but chunk `1` is in the task twice (`0, 1, 1, 2, ...`) and the event log holds 23 artifact events instead of 22. The artifact is still open, so the second append is accepted. |
     | Without `ITaskCommitLookup` | `TimeoutException` | The task ends `FAILED` with `0, 1`, as for the other chunks. |
     | With `ITaskCommitLookup` | either | Correct: each chunk once, `COMPLETED`. |

     So a store that retries a commit internally must implement `ITaskCommitLookup`, or it can duplicate parts in the
     task itself. `AmbiguousCommitTests.Without_the_lookup_a_conflict_on_a_middle_append_stores_the_chunk_twice` pins
     this behaviour until it is fixed.
2. [x] If the event is stored twice: add an optional mutation id to `TaskCommit`, a capability interface for stores that keep it, and implementations in the in-memory, Postgres and Redis stores. Follow ADR-001 rule 2, and add the startup check from ADR-001 action item 3.

   Done:
   - `TaskCommit.MutationId` and `TaskBatchCommit.MutationId`.
   - The capability `ITaskCommitLookup.FindCommitAsync`, implemented by `InMemoryTaskStore` and `PostgresTaskStore` (migration 0006: `a2a_events.mutation_id`). There is no Redis task store, so there is nothing to implement there.
   - After a failed commit call, the engine looks up the ids of every attempt of that change. When one is stored, it carries on with the task as stored now.
   - No startup check was added. No feature requires the capability, and a store without it keeps the old behaviour, so there is nothing to refuse at startup.
   - The tests in `AmbiguousCommitTests` (single and batch commits) pass, and the store contract test `A_stored_commit_is_found_by_its_mutation_id` covers both stores.
   - Still not covered: the commit that creates a task. A create whose answer is lost is reported as before.
3. [x] ~~If the event is not stored twice: record why in the gap analysis, and close D2 there.~~ Not applicable: item 1 shows the failure.
4. [x] Finish ADR-001 before tagging the release:
   - make `GetForMaintenanceAsync` virtual, or move it to a capability interface;
   - add the rule to `docs/site/guides/versioning.md`;
   - set ADR-001's status.

   Done on 2026-11-12, after 0.2.0 had already shipped. ADR-001 is Accepted. `GetForMaintenanceAsync` stays
   abstract: it has no safe fallback, and moving it now would break the stores built against 0.2.0. The rule is in
   the versioning guide under "Store contracts".
5. [ ] Add a "Writing your own backend" section to `docs/site/guides/durable-stores.md`. It should list the base classes for stores, the event bus and the coordinator, and which guarantees each must give (for example the atomic `TryRegisterExclusiveAsync`, and the shared `EventStore` that a cross-instance bus needs).
6. [ ] In the gap analysis, mark D1 and D3 as "not planned in this repository; extension points documented". Leave D33 open, with "on demand".
