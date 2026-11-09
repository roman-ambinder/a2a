# ADR-001: How store contracts evolve after the first release

**Status:** Proposed
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

1. [ ] Decide the final abstract core before tagging the first release.
2. [ ] Move non-essential members to virtual or a capability interface. `GetForMaintenanceAsync` is a candidate.
3. [ ] Add the capability check to `A2AStartupDiagnostics`.
4. [ ] Add this rule to `docs/site/guides/versioning.md`.
5. [ ] Move `PublicAPI.Unshipped.txt` entries to `PublicAPI.Shipped.txt` in the release commit.
6. [ ] Settle the package names in the same pass, because published ids cannot be renamed.
