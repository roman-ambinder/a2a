# Audit trail

The audit trail records who approved, cancelled or read what. Every binding feeds it, because the hooks sit in the request handler and the admin classes, not in the transports.

By default each event is one Information log line. Send events to a store with a sink.

## What is audited

| Action | When |
|---|---|
| `TaskCreated` | A message that starts a new task was accepted. An agent that answers with a plain message stores no task. |
| `FollowUpSent` | A follow-up message was accepted. |
| `ApprovalGranted` | A follow-up carried the right approval hash. `Resource` is the id of the status message it used. A wrong hash is `ApprovalGranted` with outcome `Failed` and reason `InvalidParams`. |
| `ApprovalDeclined` | A follow-up answered a task that waits for approval, without the hash. |
| `ElicitationAnswered` | A follow-up answered a form. `Reason` is `Accept`, `Decline` or `Cancel`. |
| `TaskCanceled` | CancelTask. `Reason` is `CancelRequested` when the agent was still running after the grace period. |
| `ArtifactDownloaded` | A stored artifact object was opened. A miss is `Failed`. |
| `PushConfigCreated`, `PushConfigDeleted` | A push config was created or deleted. An inline config in SendMessage is a second event. |
| `AccessDenied` | The call was refused: not authenticated, not allowed, a skill it may not use, or a context that is not its own (`Resource` is the context id). |
| `AdminTaskDeleted`, `AdminDeadLetterRedriven`, `AdminDeadLettersListed` | The admin classes. |
| `TaskDeleted` | A caller deleted its own task with the task-deletion extension (`AddTaskDeletion`). A missing task is `Failed` with `NotFound`, a task that is not terminal `Failed` with `UnsupportedOperation`. See [Retention](retention.md#let-callers-delete-their-own-tasks). |

Reads are not audited, except artifact downloads and dead-letter listing. `GetTask` and `ListTasks` are polled and would flood the sink. Add them with an operation filter if you need them. Retention sweeps are not audited: they are not an act of an operator.

A duplicate follow-up has the reason `duplicate`, and a follow-up that started an agent that never ran has the reason `recovered`. An idempotent replay adds no event, because nothing happened.

## The event

| Field | Notes |
|---|---|
| `Id` | A GUID. The `Attempted` row and the final row of one action share it. |
| `Time` | UTC, from the server's `TimeProvider`. |
| `Action`, `Outcome` | `Attempted`, `Succeeded`, `Denied` or `Failed`. |
| `Actor` | The caller's principal, or the `actor` of an admin call. |
| `Tenant`, `TaskId` | The tenant, and the task if there is one. |
| `Resource` | A push config id, an object id, a delivery id, or the status message an approval used. |
| `Operation` | The pipeline operation name. Set on denials. |
| `Reason` | An error code name or a fixed word. Never exception text. |
| `Binding`, `RemoteAddress`, `TraceId` | Where the call came from. |

An event holds no content. There is no free-form field. Message parts, form values, webhook URLs, push tokens, credentials, bearer tokens and exception messages never enter it.

Task ids and tenants come from callers, so the auditor cleans every string before the sink sees it. Strings are cut at 256 characters. Control characters and lone surrogates become `_`. This stops log forging, and it stops a NUL character from making a database refuse the row.

## Sinks

The default sink is `LoggerAuditSink`. Its category is `A2A.Server.Audit.LoggerAuditSink`. It writes nothing when Information logging is off. Logs are widely read, so a log line has the actor as `sha256:` plus the hash used by the [security event log](observability.md), and no address. Set `Audit.LogIdentity = true` to log both in clear. Other sinks, such as the Postgres one, always get the real values.

To store events in PostgreSQL, add the Postgres sink. It appends to the table `a2a_audit_log`.

[!code-csharp[](../../../samples/Snippets/Audit.cs#PostgresSink)]

To write your own, derive from `A2AAuditSink`. It is an abstract class with one method, like the stores. One instance serves every call, so it must be safe for many threads. Honor the token.

[!code-csharp[](../../../samples/Snippets/Audit.cs#CustomSink)]

A runnable version, with a sink in memory that the app reads back, is in [samples/TenantAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/TenantAgent/README.md).

## Fail open and fail closed

The default is fail open. The server waits for each write, at most `Audit.WriteTimeout` (2 seconds). If the sink throws or times out, the server logs an Error and the request goes on. A sink that ignores its token still cannot hang a request.

With `Audit.FailClosed = true` the promise is: no change without a record. The server writes an `Attempted` row before the change. If that write fails, the request is refused with `Internal` and nothing changed. After the change it writes the final outcome. A failure of the final row is only logged, because the change cannot be undone, and an error would make the client retry something that worked.

[!code-csharp[](../../../samples/Snippets/Audit.cs#FailClosed)]

Denials are always only logged when the write fails. The caller keeps the original refusal, never a 500.

## Admin calls

The admin classes have no caller, so say who acts. The old overloads record the actor `operator`.

[!code-csharp[](../../../samples/Snippets/Audit.cs#AdminActor)]

`A2ATaskAdmin.DeleteTaskAsync` is audited. The retention sweep deletes tasks through a different path and is not.

## Limits

- A denial from ASP.NET Core `RequireAuthorization` happens before the handler. It is not audited here.
- A request for another owner's task is `Failed` with `TaskNotFound`. The stores are keyed by owner, so they cannot tell it from a missing task.
- An unauthenticated call is written without waiting for the sink. At most 1024 such writes run at once. More are dropped with a log, so a flood of anonymous calls cannot pile up work.
- The SDK does not delete audit rows and has no retention for them.

## PostgreSQL

The sink creates the table at startup unless `CreateSchemaOnStartup` is off. If you manage the schema yourself, apply `PostgresSchema.ScriptFor("<schema>")` and give the app role `INSERT` only on `a2a_audit_log`. The app then cannot read, change or delete the trail. Keep or archive the rows as your rules say.
