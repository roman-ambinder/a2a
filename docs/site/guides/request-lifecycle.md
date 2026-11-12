# How a request flows

A deep dive into what the server does with one `SendMessage` call, from the wire to the stores and back. It names the parts from [Architecture](architecture.md). Read it when you debug the server, write a filter or a store, or want to know what is guaranteed. The behavior described here is taken from the server source.

## 1. The binding

The binding (JSON-RPC, HTTP+JSON or gRPC) turns the wire format into the same request object and a `ServerCallContext`. It also reads the headers or metadata that matter: `A2A-Version`, `A2A-Extensions`, `Idempotency-Key` and the W3C `traceparent`. All three bindings then call the same `IA2ARequestHandler`.

Before that, the ASP.NET Core layer applies the request body cap (10 MB by default) and the per-caller rate limit, which is taken after authentication and before the authorization decision. See [Limits and rate limiting](limits-and-rate-limiting.md).

## 2. The operation pipeline

The handler runs each operation through a chain of filters, outermost first:

1. **Telemetry**: opens the span for the operation and records its outcome.
2. **Host filters registered `BeforeAuthorization`**: these see every call, including calls that authorization will refuse. The caller's identity must not be trusted here. A result such a filter returns without calling the next step goes to the client without any authorization.
3. **Authorization**: the anonymous backstop, then your `A2AAuthorizationHandler`, then the skill check of a send when skill authorization is on. A denial of a call that names a task looks like a missing task (`TaskNotFound`); any other denial is `PermissionDenied`.
4. **Host filters registered `AfterAuthorization`** (the default stage): these only see calls that are authenticated (or allowed anonymous) and authorized.
5. **The operation itself.**

Host filters get their own copy of the result, because results can share instances with the stores. The chain is built once. Without host filters a call goes through telemetry, authorization and the operation, with no extra allocation for the chain. See [Custom operations](custom-operations.md) for filters and your own operations.

## 3. Validation and ownership

Inside the operation the handler checks the request against the limits (parts per message, JSON depth and so on) and the task rules:

- A new task is bound to the caller id and the tenant. Any lookup of an existing task is scoped by owner and tenant, so another caller's task reads as `TaskNotFound`.
- A follow-up on a task is accepted only if the task's state allows it (`INPUT_REQUIRED` or `AUTH_REQUIRED` by default).
- A follow-up whose `messageId` is already in the task's history is not run again: the caller gets the task as it is.
- An approval hash that does not match is refused with `InvalidParams` before the agent runs. See [Task safety](task-safety.md).

## 4. Idempotency

The server keys a `SendMessage` by its `Idempotency-Key` header, or by the message id when the header is missing. The SDK client always sends the header (default: the message id). Keys are scoped by tenant and caller. The server claims the key before it does work, so a retry does not create the task twice. The claim is renewed by the execution heartbeat while the request runs, so a live request keeps its key and a crashed instance frees it sooner.

## 5. Running the agent

The engine rents the executor for the tenant. By default that is a new DI scope per execution, so scoped services such as a `DbContext` are safe in your executor. The engine takes an execution slot (it can wait in a queue, and refuses with `RATE_LIMITED` and reason `execution_capacity` when there is no slot or no room), starts a span `a2a.agent.execute` as a child of the operation span, and calls your `ExecuteAsync`.

While the agent runs it holds a **lease** (a time-limited claim that says "this execution is alive on this instance") with the `ExecutionCoordinator`, renewed every `Heartbeat` (10 s by default; the lease TTL, `LeaseTtl`, is 30 s). Both are on `A2AServerOptions.Coordination`. Every instance that shares the coordinator can see that the task is running, and a cancel request from another instance reaches the agent through it.

An optional `MaxTaskDuration` fails a run that goes over it. Token and cost budgets and event size caps can also fail a task. See [Task safety](task-safety.md) and [Execution limits and budgets](execution-limits.md).

## 6. Events are committed, then published

Each call your agent makes on the `TaskUpdater` (start work, add an artifact, ask for input, complete) becomes an event. For each event the engine, under the task's lock:

1. applies it to a copy of the task;
2. calls `TaskStore.CommitAsync`, which writes **the task (with a version check), the event in the task's log, and the push deliveries for that event, as one unit**: all of it or nothing;
3. publishes the stored event to the request that started the run and to stream subscribers.

Because publishing happens under the task's lock and after the commit, publish order is sequence order, and every event a subscriber sees has already been stored.

The lock is per process. Across instances the version check in the commit keeps the event log in the order of the task versions. When a write loses a version race with another instance, the engine retries with a short randomized backoff.

When an agent produces events faster than they are stored (for example many artifact chunks), a store that supports batch commits (`IBatchTaskCommit`) can write several events of one task as a single unit.

## 7. Streams and resume

A streaming call (`SendStreamingMessage`, `SubscribeToTask`) reads the stored events and the live ones from the event bus. The bus holds a bounded number of events per subscriber and drops the **oldest** when a subscriber is slow, never the newest. A subscriber that sees a gap in sequence numbers fills it from the event store. Client resume uses the `Last-Event-ID` header and skips events it already has. With several instances, the Redis event bus fans events out to all of them. See [Streaming and resume](streaming.md).

## 8. Push delivery

If the task has push configs, the commit in step 6 also queues one delivery per config in the push outbox. The hosted `PushDeliveryService` sends them through an SSRF-guarded HTTP invoker, with retries. A delivery that cannot succeed goes to the dead letters, a list you can inspect and re-send with `A2APushAdmin`. Each attempt is a span `a2a.push.deliver`, and the webhook request carries the `traceparent` of that span. See [Push notifications](push-notifications.md).

## 9. The response

For a unary `SendMessage`, the handler returns either a message (a stateless reply) or the task as it stands. Errors are mapped to each binding's form: a JSON-RPC error object, an HTTP status with a `google.rpc.Status` body on HTTP+JSON, or `google.rpc.Status` with an `ErrorInfo` detail on gRPC. Unexpected exception text is not sent unless `IncludeExceptionDetails` is on, which by default means the host is in Development. An exception from your agent moves the task to `FAILED` with the generic status text "The agent failed to process the request."

## 10. Afterwards

If retention is on, a background sweep deletes old terminal tasks with their events, push configs and deliveries, cancels tasks that wait in `INPUT_REQUIRED` or `AUTH_REQUIRED` longer than `InterruptedTimeout` (24 hours by default), and fails the tasks of executions whose lease lapsed. See [Retention](retention.md).

## Where to look in the source

| Step | Source |
|---|---|
| Pipeline | `src/A2A.Server/Pipeline/OperationPipeline.cs`, `AuthorizationFilter.cs` |
| State rules | `src/A2A.Server/TaskStateMachine.cs` |
| Execution | `src/A2A.Server/Execution/TaskExecutionEngine.cs` |
| Atomic commit | `src/A2A.Server/Storage/TaskCommit.cs` |
| Event bus | `src/A2A.Server/TaskEventBus.cs`, `Streaming/` |
