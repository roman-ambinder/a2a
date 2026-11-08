# Task safety

An agent runs code on behalf of a peer you do not control. This guide covers the guards around that: time and turn limits, approvals for risky actions, cancel, retries, error text, and untrusted input on the client side.

## Time, turn and cancel limits

[!code-csharp[](../snippets/TaskSafety.cs#ServerLimits)]

| Option | Default | Meaning |
|---|---|---|
| <xref:A2A.Server.A2AServerOptions.MaxTaskDuration> | null (off) | Longest one execution may run. |
| <xref:A2A.Server.A2AServerOptions.MaxTurnsPerTask> | null (off) | Most incoming messages one task accepts. |
| <xref:A2A.Server.A2AServerOptions.CancelGracePeriod> | 5 seconds | How long CancelTask waits for a running agent to stop. |
| <xref:A2A.Server.A2AServerOptions.ShutdownDrainTimeout> | 20 seconds | How long a host stop waits for running agents to finish. Zero turns the wait off. |

**Time limit.** One execution is one incoming message. When it runs longer than `MaxTaskDuration`, the task moves to `FAILED` with the text "Task exceeded its time limit". Streams and webhooks get it like any final event. The execution's cancellation token fires, and events the agent sends afterwards are dropped. A task waiting in `INPUT_REQUIRED` or `AUTH_REQUIRED` with no agent running is not affected. Each follow-up gets the full time again. Null, zero, a negative value, or about 24.8 days or more turns the limit off. The clock is the `TimeProvider` in DI. For tasks that wait too long for input, see [retention](retention.md).

**Turn cap.** A peer can steer a long conversation one small step at a time. `MaxTurnsPerTask` counts the user messages in the stored history, including the one that created the task. A follow-up over the cap is refused with `UnsupportedOperation` before the agent runs, and the task is not changed. A retry of an accepted message is not a new turn. A value of 1 means no follow-ups. A custom `TaskStore` must keep the full history, or the cap does not hold.

**Cancel requested.** CancelTask asks the agent to stop and waits up to `CancelGracePeriod`. If the agent has not stopped by then, the answer is the task as stored, with the metadata `a2a.cancelRequested = true`. That flag is never stored. The `CANCELED` state arrives later on the stream. A task that is already final gives `TaskNotCancelable`.

**Host shutdown.** When the host stops, the server first waits up to `ShutdownDrainTimeout` for running agents to finish (it honours the stop token too). Keep it below the host's shutdown timeout, 30 seconds by default. Agents still running after that are asked to stop, but their tasks are **not** moved to `CANCELED`: nobody cancelled them. They stay `WORKING`, and the lease expiry, the [retention](retention.md) sweep or a continuation handles them. Only a real CancelTask ends in `CANCELED`, even when it arrives during shutdown.

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

## Structured input (elicitation)

An agent in `INPUT_REQUIRED` can ask for a small typed form instead of free text. This is an optional extension (`Elicitation.ExtensionUri`). The agent lists it on its card with `Elicitation.CreateExtension()`. The design is in `docs/superpowers/specs/2026-11-08-s38-elicitation-design.md`.

On the server, build an <xref:A2A.Elicitations.ElicitationForm> and call `updater.RequireInputAsync(context, form, text)`. The answer arrives as `context.ElicitationAnswer`:

[!code-csharp[](../snippets/TaskSafety.cs#ElicitServer)]

On the client, activate the extension with `UseElicitation`, read the form with `GetElicitationForm`, and answer with `ElicitationAnswer.Accept`, `Decline` or `Cancel`:

[!code-csharp[](../snippets/TaskSafety.cs#ElicitClient)]

A runnable version is in `samples/ElicitationAgent`.

**A client that does not activate the extension** sees the text part only. The form is not in the message. It answers in words, and the agent reads `context.UserText`. A client that activated it can also answer in words: `ElicitationAnswer` is then null. So `text` must always say what you need. Check `context.CanElicit` if the agent must behave differently.

**Accept, decline, cancel.** `accept` carries the values. `decline` means the user refuses this request. `cancel` means the user dismissed it without a decision. The agent decides what each one means, for example `FailAsync`, a new form, or going on without the data. The server moves no state. It is not CancelTask.

**Refused answers.** The server checks the answer against the form before the agent runs: the form id, the action, the field names, the types, the limits and the options. A bad answer gets `InvalidParams`, and the error names the field and the rule but never repeats the value. Nothing is stored and no turn is used, so the client can correct it and send again. An answer with no form waiting is `InvalidParams` too.

**With the other guards.**

- `OnMessageReceived` also sees the status message with the form, so a hook can call `GetElicitationForm()` and throw before the app shows it.
- Every accepted answer, of any action, is one turn under `MaxTurnsPerTask`. A refused one is not. At the cap the answer is `UnsupportedOperation`, so an agent that may ask a form must leave one turn for the answer.

**Safety.**

- The form text is peer input. `title` and `description` are sanitized when read, and capped. Show the user which agent is asking, by card origin and not only by `name`.
- Never let an LLM fill a form unattended. A hostile agent could use a form to pull data out of the model's context. A person fills it.
- Secrets are refused by default. A field whose name, title or description looks like a password, token, key, card number and so on is refused when the server builds the form, and when the client answers (`allowSensitive: true` is the client's own choice and never goes on the wire). The check is by name and is best effort. It cannot catch every wording.
- Answers are stored in the task history, sent to webhooks with the task, and readable by anyone who can read the task. Do not collect secrets in a form. Use the auth-required flow for credentials.

## Follow-up de-duplication

A follow-up whose `messageId` is already in the task's history is not run again. The caller gets the task as it is: no new turn, no push config, no execution. The check runs before the state check, so a retry after the task has finished still gets its outcome.

**A message the agent never saw.** The follow-up is committed before its agent starts. If the process dies in between, the message is in the history and nothing runs. The client's retry of the same `messageId` then starts the agent, once. This happens only when all of these hold: the task is not final and still accepts a follow-up, the message is the last one in the history, no agent runs for the task on any instance (an unreachable coordinator counts as running), and the event store has no event after the one the commit recorded (an agent that wrote anything, an artifact included, leaves one). Two retries at once start one execution. The commit records a server-owned key, `a2a.followUp.pendingStart`, in the task metadata; agents cannot write it. If the client never retries, nothing starts: the task stays in its state, with no lease, until the [retention](retention.md) sweep times it out. An agent that ran and wrote nothing at all looks the same as one that never started, so a retry runs it again.

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
- **The form never reaches the client.** The client did not activate the extension (`UseElicitation`), or the agent card does not declare it. The client then sees the text only.
- **An answer gets `InvalidParams`.** It does not match the form: a wrong id, a missing required field, a value out of range, or a form that was already answered. The error names the field. Fix it and send again.
- **`ElicitationForm` throws for a field.** The field looks like a request for a secret. Use the auth-required flow instead.
- **`UnsupportedOperation` on the answer.** The task is at `MaxTurnsPerTask`. Leave one turn for the answer.

## See also

- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Retention](retention.md)
- [Streaming and resume](streaming.md)
