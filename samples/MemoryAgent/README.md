# MemoryAgent sample

A note-taking agent with conversation memory and proactive continuation, and a client in the same process. Each note
is its own task in one context. It listens on `127.0.0.1` on a free port and needs no keys.

| Feature | What the sample does |
|---|---|
| Conversation memory | The agent reads the earlier notes with `context.Conversation.ReadEarlierMessagesAsync`. The client sends only the new message, with the same `contextId`. |
| Proactive continuation | `NotesAgent` is an `IContinuableAgentExecutor`. On `report` it keeps the `TaskKey` and leaves the task `Working`. The host then calls `TaskExecutionEngine.ContinueTaskAsync` with no client request and awaits `Completion`. `ContinueAsync` completes the task. |

The sample checks that the second turn names the first note, and that the report task ends `Completed` with both
notes. A real host continues from a timer or a callback, not right away.

```bash
dotnet run --project samples/MemoryAgent
```

Expected output ends with:

```text
--- conversation memory
turn 1: Noted: buy milk.
turn 2: Noted: call Bob. Earlier notes: buy milk.

--- proactive continuation
report task: Working: Writing a report of 2 notes. I will update this task.
continue: Started
report task: Completed: Report: buy milk; call Bob.

OK: the second turn remembered the first, and the continued report task completed.
```

Anonymous access is on because the sample is loopback only. A real agent needs authentication and
`MapA2A(...).RequireAuthorization()`. A host that exposes a continuation callback must also authenticate its callers.
See the [conversation memory guide](../../docs/site/guides/conversation-memory.md) and the
[proactive continuation guide](../../docs/site/guides/proactive-continuation.md).
