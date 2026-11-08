# Task safety

An agent runs code on behalf of a peer you do not control. This guide covers the guards around that: time and turn limits, approvals for risky actions, cancel, retries, error text, and untrusted input on the client side.

## Time, turn and cancel limits

[!code-csharp[](../snippets/TaskSafety.cs#ServerLimits)]

| Option | Default | Meaning |
|---|---|---|
| <xref:A2A.Server.A2AServerOptions.MaxTaskDuration> | null (off) | Longest one execution may run. |
| <xref:A2A.Server.A2AServerOptions.MaxTurnsPerTask> | null (off) | Most incoming messages one task accepts. |
| <xref:A2A.Server.A2AServerOptions.CancelGracePeriod> | 5 seconds | How long CancelTask waits for a running agent to stop. |

**Time limit.** One execution is one incoming message. When it runs longer than `MaxTaskDuration`, the task moves to `FAILED` with the text "Task exceeded its time limit". Streams and webhooks get it like any final event. The execution's cancellation token fires, and events the agent sends afterwards are dropped. A task waiting in `INPUT_REQUIRED` or `AUTH_REQUIRED` with no agent running is not affected. Each follow-up gets the full time again. Null, zero, a negative value, or about 24.8 days or more turns the limit off. The clock is the `TimeProvider` in DI. For tasks that wait too long for input, see [retention](retention.md).

**Turn cap.** A peer can steer a long conversation one small step at a time. `MaxTurnsPerTask` counts the user messages in the stored history, including the one that created the task. A follow-up over the cap is refused with `UnsupportedOperation` before the agent runs, and the task is not changed. A retry of an accepted message is not a new turn. A value of 1 means no follow-ups. A custom `TaskStore` must keep the full history, or the cap does not hold.

**Cancel requested.** CancelTask asks the agent to stop and waits up to `CancelGracePeriod`. If the agent has not stopped by then, the answer is the task as stored, with the metadata `a2a.cancelRequested = true`. That flag is never stored. The `CANCELED` state arrives later on the stream. A task that is already final gives `TaskNotCancelable`.

## Approve risky actions

An executor that needs the user to confirm an action calls <xref:A2A.Server.TaskUpdater.RequireApprovalAsync*> with a <xref:A2A.Server.PendingApproval>:

[!code-csharp[](../snippets/TaskSafety.cs#Approval)]

What happens:

1. The task goes to `INPUT_REQUIRED`. The status message has the metadata `a2a.approval.hash`: the lowercase hex SHA-256 of `operationId + "\n" + description`. So `operationId` must not contain a line feed.
2. The client shows the user the exact action. To approve, it sends a follow-up whose metadata has the same key and the same hash.
3. The executor sees `context.Approval` as <xref:A2A.Server.ApprovalState> `Approved`.

[!code-csharp[](../snippets/TaskSafety.cs#Approve)]

| `Approval` value | Meaning |
|---|---|
| `None` | Nothing is pending. Do not treat the message as an approval. |
| `Approved` | The message has the hash of the pending approval. |
| `NotApproved` | An approval is pending and the message has no hash. The follow-up is accepted. Ask again. |

A hash that does not match, or a hash when nothing is pending, is refused with `InvalidParams` before the agent runs. An approval works once. The server records it in the task metadata when the approving message is committed, so a second use of the same hash is `InvalidParams`, also across instances. Call `RequireApprovalAsync` again to ask anew.

## Follow-up de-duplication

A follow-up whose `messageId` is already in the task's history is not run again. The caller gets the task as it is: no new turn, no push config, no execution. The check runs before the state check, so a retry after the task has finished still gets its outcome.

## Executor errors

If the executor throws, the task becomes `FAILED` with the fixed text "The agent failed to process the request." The exception goes to the log, not to the caller. If you want a specific message, call `updater.FailAsync` yourself with text you are happy to show.

## Check what a peer agent says

Client side, <xref:A2A.Client.A2AClientOptions.OnMessageReceived> runs for every agent message the client receives: direct replies, status update messages, and the latest status message of a task snapshot. It works for unary and streamed calls, on every binding.

[!code-csharp[](../snippets/TaskSafety.cs#Hook)]

Throw to abort. The exception reaches your code, and a stream stops. Use it to check each turn against what the user asked for, and to ask a human before a sensitive action. The same snapshot message can arrive more than once, for example when you poll with GetTask or a stream reconnects, so keep the check idempotent.

## Treat card text and URLs as untrusted

Agent Card text is written by the peer. `A2ACardResolver.ResolveAsync` returns a <xref:A2A.Client.ResolvedAgentCard> whose description and skill text are <xref:A2A.Client.Cards.UntrustedText>:

[!code-csharp[](../snippets/TaskSafety.cs#Untrusted)]

`ToString()` removes control and bidi characters, zero-width and tag characters, and cuts the text to 2000 characters (`MaxCardTextLength` on the resolver options). `Raw` gives the text as received. Use `Raw` only on purpose. Identity is the origin and key id, not the card `name`.

A `UrlPart` in a message points to a URL the peer chose. Do not fetch it with a plain `HttpClient`. <xref:A2A.Security.Http.SafeHttp> blocks private, loopback, link-local and similar ranges by the resolved address at connect time, allows HTTPS only, does not follow redirects, and caps the size:

[!code-csharp[](../snippets/TaskSafety.cs#SafeHttp)]

<xref:A2A.Security.Http.SafeHttpOptions> can allow plain HTTP, private networks or specific networks. Leave them off in production.

## What can go wrong

- **Tasks fail with "Task exceeded its time limit".** The agent runs longer than `MaxTaskDuration`. Raise it, or report progress and finish sooner.
- **A follow-up gets `UnsupportedOperation`.** The task is at `MaxTurnsPerTask`. Start a new task and list the old one in `referenceTaskIds`.
- **Approval always `NotApproved`.** The client did not copy the hash into the follow-up's metadata.
- **A hash is refused the second time.** Approvals work once.

## See also

- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Retention](retention.md)
- [Streaming and resume](streaming.md)
