# S28 and S29: event metadata merge, history timestamps

Date: 2026-11-08. Closes S28 and S29 in `docs/2026-11-07-a2a-gap-analysis.md`. The A2A v1.0 wire format does not change.

## Research

- **A2A#1374 (open).** Proposes: `TaskStatusUpdateEvent.metadata` shallow-merges into `Task.metadata`; `Artifact.metadata` merges when `append: true` and replaces when `append: false`; `TaskArtifactUpdateEvent.metadata` is transient. Keys not in the update stay. No way to remove a key. Go and Python differ today.
- **Epic A2A#1991 (closed by #2129 on `dev-1.1`).** Adds `Task.timeline` of `TimelineEntry { message | status, generation }` and soft-deprecates `history`. Open PR #2426 adds `TimelineEntry.timestamp`, equal to the event's time, and says wall-clock time is informational: order by position, not by time.

Today in this SDK:

- `ApplyToCopy` ignores both event-level metadata fields. `Artifact.metadata` already follows the #1374 rule.
- `TaskShaper` applies `historyLength` and the cap on SendMessage, GetTask, ListTasks, CancelTask and SubscribeToTask snapshots.
- SendStreamingMessage does not. Its first event is a `Task`, and the follow-up snapshot is `current.Task` with the whole history, over the cap and ignoring `historyLength`.

## S28: metadata rule

One rule: **shallow merge, last write wins**. A key in the update adds or overwrites. A key not in the update stays. Values are not merged inside.

| Source | Target |
| --- | --- |
| `TaskStatusUpdateEvent.metadata` | `Task.metadata` |
| `TaskArtifactUpdateEvent.metadata` | `Task.metadata` |
| `Artifact.metadata`, `append: true` | stored artifact's metadata (unchanged) |
| `Artifact.metadata`, `append: false` | replaces the artifact (unchanged) |

- An event that is dropped (terminal task, invalid transition, sealed artifact) merges nothing. The merge runs on the fresh record, so a version-conflict retry is correct.
- **Removal: not supported.** JSON `null` is a value like any other and is stored. This matches #1374. Null-as-delete would make this server's task differ from what another SDK computes from the same events. An agent that needs to clear a key writes a value that means "cleared".
- **Server-owned keys are skipped in the merge**: `a2a.approval.consumed`, `a2a.cancelRequested`, `actorChain`. Without this, an agent could reset `a2a.approval.consumed` and reuse an approval. The existing skip of `a2a.artifact.sealed` on artifacts stays.
- Merged values are cloned (`JsonElement.Clone`), so they do not depend on a document the agent disposes.
- **Difference from #1374:** the proposal calls `TaskArtifactUpdateEvent.metadata` transient. Here it merges, so there is one rule for both event types. If upstream settles on transient, removing one call changes it.

**Raw event list.** `EventStore` is public, is in DI, and `ReadAfterAsync(TaskKey, afterSequence)` is owner-scoped. That is the supported read path; no new type wraps it. A2A v1.0 has no RPC for it, so it is server-side only. It returns the retained events: the last `ReplayBufferSize` per task, removed after retention. The guide states this. A store contract test pins the order, the owner scope and that event metadata survives.

## S29: history

### Timestamp per message

Each message added to `Task.history` gets `metadata["a2a.message.recordedAt"]`: an ISO 8601 UTC string, in the same form as `TaskStatus.timestamp`.

**Decision: metadata key, not store-only.**

- `metadata` is an open map, so the v1.0 JSON and proto shape does not change on any binding.
- Clients can read it. A store-only value would need a new read path to be of any use.
- No schema change and no migration. `PostgresSchema.Version` stays 1. Custom `TaskStore` implementations work unchanged. There is no rolling-upgrade problem, since an old instance treats it as ordinary metadata.
- Existing rows are safe: their messages have no key and it reads as null. No backfill, since the true time is not known.

Where it is set:

- **Client messages:** once, in `AcceptAsync`, on the incoming message before it is copied into history, with the server's clock. This overwrites a value the client sent, so the agent also sees the server's value.
- **Agent status messages:** in `TaskExecutionEngine.Encode`, equal to `status.timestamp`, on a copy of the message with a new metadata dictionary. The status event, `status.message` and the history entry then agree, as #2426 requires. System status messages pass through `Encode` too.
- Direct message replies are not stored, so they get no key.

Public read helper in `A2A.Protocol`: `A2AProtocol.MessageRecordedAtKey` and an extension `Message.GetRecordedAt()`, which returns `DateTimeOffset?` (null when absent or malformed).

The value is informational, not an ordering key. History order is the list order.

### `historyLength`

One rule on every path that returns a task to a caller who can ask for it: `TaskShaper`. Null means all, up to `MaxHistoryLengthReturned`; 0 means none; n means the newest n.

Fix: SendStreamingMessage shapes every `Task` event it yields (first event, gap snapshots, idempotent replays) with `Configuration.HistoryLength` and the cap. Bytes are re-encoded with the same sequence only when the history is actually cut. The stored event, the log and push payloads do not change.

A reconnect through `Last-Event-ID` is a SubscribeToTask, which has no `historyLength` in v1.0, so its snapshots use the cap only.

Paging is out of scope: v1.0 has no wire field for it.

### 1.1 timeline

No code. The stored data is enough for a later projection: a client history message becomes a `TimelineEntry.message`, and an agent message becomes a `status` entry, with `recordedAt` as `timestamp`. `generation` and message-less status entries would come from the event log. An unused internal model now would be dead code.

## Tests

- All three bindings (parity tests): status and artifact event metadata visible in `GetTask`; history stamps present and parseable, with no other new key; a client-sent stamp is overwritten; SendStreamingMessage with `historyLength` 0, 1 and unset on a new task and a follow-up, and the cap.
- Server: last write wins, absent keys stay, null is stored, server-owned keys are skipped, dropped events merge nothing, the artifact rule is unchanged.
- Store contract (in-memory and Postgres): stamped messages and merged task metadata round-trip; a message without the key reads as null; `ReadAfterAsync` is ordered, owner-scoped and keeps event metadata.
