# ADR-001: How store contracts evolve after the first release

**Status:** Accepted (2026-11-12)
**Date:** 2026-11-09
**Deciders:** Repository owner

## Context

Backends plug in through abstract classes:

| Class | Abstract members |
|---|---|
| `TaskStore` | 13 |
| `PushOutbox` | 8 |
| `ExecutionCoordinator` | 8 |
| `EventStore` | 6 |
| `IdempotencyStore` | 6 |
| `ArtifactObjectStore` | 4 |

(Counts on 2026-11-09. The counts that shipped in 0.2.0 are under "Outcome" below.)

`TaskStore` gained `GetForMaintenanceAsync` when the artifact store was added. After the first release, each such addition breaks every third-party store.

`ExecutionCoordinator.TryRegisterExclusiveAsync` already shows an alternative: a virtual member with a fallback.

Nothing is published yet, so the abstract surface can still change at no cost. After the first release it cannot.

## Decision

Freeze a small abstract core at the first release. After that:

1. A new member is virtual with a real fallback, when the fallback is safe with several instances.
2. If no safe fallback exists, the member goes on an optional capability interface. A feature that needs a missing capability fails at startup with a clear error.

## Options considered

### Option A: Keep every member abstract

| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Cost | Every addition is a major version |
| Scalability | Poor for third-party stores |
| Team familiarity | High: this is the current state |

**Pros:** The compiler makes implementers handle everything.

**Cons:** Every addition breaks all stores, which discourages adding features.

### Option B: Virtual members that throw `NotSupportedException`

| Dimension | Assessment |
|---|---|
| Complexity | Low |
| Cost | Failures move to production |
| Scalability | Good for compile compatibility |
| Team familiarity | High |

**Pros:** Never breaks a compile.

**Cons:** An old store fails at runtime on first use, possibly deep inside a background sweep.

### Option C: Abstract core, virtual with fallback, optional interfaces (chosen)

| Dimension | Assessment |
|---|---|
| Complexity | Medium |
| Cost | One design review per new member |
| Scalability | Good |
| Team familiarity | High: the pattern already exists |

**Pros:** Old stores keep working. A missing capability is found at startup. Reviewers get a clear rule.

**Cons:** Fallbacks can be slower. There are more types.

## Trade-off analysis

Option A protects third-party stores least. Option B trades a compile break for a production surprise, which is worse for a store that holds task state. Option C costs review discipline.

The main risk of Option C is a fallback that is silently unsafe on a shared backend. The default `TryRegisterExclusiveAsync` is not atomic, and its documentation warns that shared coordinators must override it. So a fallback is allowed only when it is safe with several instances. Otherwise the member uses a capability interface.

## Consequences

- **Easier:** adding features without a major version.
- **Harder:** each addition needs a fallback or a capability design, plus a startup check.
- **To revisit:** appending deltas instead of rewriting the whole task on each event. `TaskCommit` already carries `Event`, so the write side needs no contract change. The read side may.

## Action items

1. [x] Decide the final abstract core before tagging the first release. The core is the set of abstract members that
   shipped in 0.2.0 (see "Outcome").
2. [x] Move non-essential members to virtual or a capability interface. `GetForMaintenanceAsync` was the candidate. It
   stays abstract: see "Outcome".
3. [ ] ~~Add the capability check to `A2AStartupDiagnostics`.~~ Deferred: no feature requires a capability yet. The
   optional capabilities that exist (`IBatchTaskCommit`, `ITaskCommitLookup`, `IContextSummaryStore`,
   `IPushOutboxStatistics`) all have a fallback, so there is nothing to refuse at startup. ADR-002 reached the same
   conclusion for `ITaskCommitLookup`. Add the check together with the first capability that a feature requires.
4. [x] Add this rule to `docs/site/guides/versioning.md` ("Store contracts").
5. [x] Move `PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt` in the release commit. Done in the 0.2.0
   release commit (#144).
6. [x] Settle the package names in the same pass, because published ids cannot be renamed. 0.2.0 published them with
   the `RomanAmbinder.` prefix.

## Outcome (2026-11-12)

0.2.0, the first version on NuGet, shipped with these abstract members. They are the frozen core:

| Class | Abstract members |
|---|---|
| `TaskStore` | 13 |
| `PushOutbox` | 8 |
| `ExecutionCoordinator` | 8 |
| `PushConfigStore` | 7 |
| `EventStore` | 6 |
| `IdempotencyStore` | 4 |
| `ArtifactObjectStore` | 4 |

The exact members are the `abstract` lines in `src/A2A.Server/PublicAPI.Shipped.txt`.

**`TaskStore.GetForMaintenanceAsync` stays abstract.** It has no fallback that is safe with several owners:

- `GetAsync` hides a task behind its owner. A fallback built on it would report another owner's live task as missing.
  The orphan sweeps of artifact objects (`A2ARetentionService`) and uploads (`InboundFiles`) would then delete objects
  that a live task still links to.
- A fallback that throws is Option B, rejected above.

Rule 2 would put it on a capability interface instead. That would remove a member that 0.2.0 shipped, and every store
that overrides it would stop compiling (CS0115). The member is one read by tenant and id, which any store can
implement. Breaking published stores to save that one method is not worth it, so it is part of the core.
