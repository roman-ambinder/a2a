# Audit trail design

Today nothing records who approved, cancelled or read what. This adds an audit sink that every binding feeds, because the hooks sit in `A2ARequestHandler` and the admin classes, not in transports.

## Decisions

- **Abstract class, one method.** `A2AAuditSink.WriteAsync(A2AAuditEvent, CancellationToken)`. An abstract class, like the stores, so ADR-001 can add members later.
- **Default sink logs.** `LoggerAuditSink` writes one Information log per event (category `A2A.Server.Audit.LoggerAuditSink`). Registered by `AddA2AServer` with `TryAdd`. Log lines never carry a principal in clear (a rule of the security event log), so it writes the actor's hash and no address unless `Audit.LogIdentity` is set.
- **Postgres sink is opt-in.** `AddPostgresAuditSink(...)`. Table `a2a_audit_log`, added by migration `0004_audit_log.sql`. Append only. The SDK has no update or delete path.
- **Writes are awaited, with a timeout.** `A2AAuditOptions.WriteTimeout`, default 2 s. No background queue: nothing to lose on a crash, no shutdown flush.
- **Reads are not audited**, except artifact downloads and dead-letter listing. `GetTask` and `ListTasks` are polled and would flood the sink. A host can add them with an operation filter.
- **Retention sweeps are not audited.** They are not an admin act. `A2ATaskAdmin.DeleteTaskAsync` is.

## Fail open and fail closed

Default is fail open: a sink error or timeout is logged (Error) and the request goes on.

`A2AAuditOptions.FailClosed = true` promises **no change without a record**. A record written after a change cannot promise that, so fail closed adds one step:

1. Before the change, write the event with outcome `Attempted`. If that write fails, the request is refused with `Internal` and nothing changed.
2. After the change, write the final outcome. A failure here is only logged, because the change cannot be undone and an error would make the client retry something that worked. The `Attempted` row is already in the trail.

Fail open writes only step 2. `Id` is the same on both rows of one action. Denials are always only logged on failure: the caller keeps the original refusal, never a 500.

## Event

| Field | Notes |
|---|---|
| `Id` | GUID (N format), same on the `Attempted` and final rows |
| `Time` | From `TimeProvider`, UTC |
| `Action` | See below |
| `Outcome` | `Attempted`, `Succeeded`, `Denied`, `Failed` |
| `Actor` | `call.Principal`, or the admin's `actor` argument |
| `Tenant` | Bound tenant |
| `TaskId` | Nullable |
| `Resource` | Nullable. Push config id, object id, delivery id, or the status message id an approval used |
| `Operation` | Nullable. Pipeline operation name, on denials |
| `Reason` | Nullable. Only an `A2AErrorCode` name or a fixed word. Never exception text |
| `Binding`, `RemoteAddress`, `TraceId` | Nullable |

Actions: `TaskCreated`, `FollowUpSent`, `ApprovalGranted`, `ApprovalDeclined`, `ElicitationAnswered`, `TaskCanceled`, `ArtifactDownloaded`, `PushConfigCreated`, `PushConfigDeleted`, `AdminTaskDeleted`, `AdminDeadLetterRedriven`, `AdminDeadLettersListed`, `AccessDenied`.

**No content.** There is no free-form field and no dictionary. Webhook URLs, push tokens, credentials, bearer tokens, message parts, form values and exception messages never enter an event. `Reason` for an unknown exception is `Error`, and for cancellation `Canceled`.

**Input is untrusted.** Task ids and tenants come from callers. The auditor cleans every string field in one place before the sink sees it: at most 256 characters, control characters and lone surrogates become `_`. This stops log forging. It also stops a NUL or bad surrogate from making Postgres refuse the row, which would let a caller hide its action.

## Hooks

`A2AAuditor` (public class, internal methods, registered in DI) holds the sink, options, logger and clock:

- `For(action, call, taskId, resource)` builds an event with outcome `Attempted`.
- `BeginAsync(event, ct)`: step 1. No-op when fail open.
- `SucceedAsync` / `FailAsync(event, ex)` / `RecordAsync`: step 2. Never throw. Use `CancellationToken.None` plus the timeout, so a client disconnect cannot lose the record.
- `RecordDetached(event)`: for the synchronous `ValidateCall`. Fire and forget, at most 1024 in flight, dropped with a log after that, so an anonymous flood cannot pile up tasks.
- `A2AAuditor.None`: for handlers built by hand without one.

| Action | Where |
|---|---|
| Message actions | `AcceptAsync` wraps `AcceptCoreAsync` with a small ticket. `PrepareAsync` classifies and calls `BeginAsync` after validation, before the task lock, so a slow sink never holds a stripe. Shared by unary and streaming |
| Final message action | Set from the resolved `ApprovalState` and elicitation. A hash mismatch is `ApprovalGranted` + `Failed` + `InvalidParams` |
| Follow-up to a task waiting for approval, no hash | `ApprovalDeclined` |
| Inline push config in SendMessage | A second event, `PushConfigCreated` |
| Duplicate follow-up | Final row with `Reason = duplicate`, so each `Attempted` has a final row. A recovered start has `Reason = recovered` |
| Idempotent replay | No event: nothing happened |
| Cancel, push create, push delete, artifact open | One `AuditedAsync` helper around the existing core methods. Cancel still running after the grace period has `Reason = CancelRequested` |
| Denials | `OperationAuthorization.RequireAsync` (pipeline and stream resume), stream re-authorization, per-skill denials, a refused context, and `Unauthenticated` from `ValidateCall` |
| Admin | `A2ATaskAdmin.DeleteTaskAsync`, `A2APushAdmin.RedriveAsync`, `ListDeadLettersAsync`. New overloads take `string actor`. Old ones record `A2AAuditEvent.UnknownOperator` |

Limits:

- A denial from ASP.NET Core `RequireAuthorization` happens before the handler and is not audited here.
- A request for another owner's task is `Failed` + `TaskNotFound`, because the stores are keyed by owner and cannot tell it from a missing task.
- `TaskCreated` means the message that starts a new task was accepted. An agent that answers with a plain message stores no task.

## Done

Guide `docs/site/guides/audit.md` with a compiled snippet. Tests on net8.0 and net10.0, including a sentinel test that no content or secret reaches an event, fail open, fail closed, and Postgres.
