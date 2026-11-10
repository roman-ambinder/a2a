# Conversation memory

A `contextId` groups tasks. By default a new task in a context sees nothing of the earlier ones. Conversation memory lets an agent read the earlier messages of its context, and keep a short summary of it. It also lets a host list the conversations of a caller.

There is no new storage. The messages are already in the task rows. A read finds the newest tasks of the context and returns their messages.

## Turn it on for the chat client executor

[!code-csharp[](../../../samples/Snippets/ConversationMemory.cs#Enable)]

With `UseContextMemory`, the prompt is built in this order:

1. the system `Instructions`,
2. the context summary, if there is one, as a user message,
3. the messages of earlier tasks in the context, oldest first,
4. the history of this task.

`MaxHistoryMessages` counts the earlier messages and the task history together. The summary is not counted. As before, a prompt never starts with an agent reply. See [Microsoft.Extensions.AI](extensions-ai.md#history-and-multi-turn-chats).

When it is off, nothing changes.

### The reply is stored twice

A task keeps status messages only in its history. So with `UseContextMemory`, a completed task also stores its reply as the status message. The next task can then read it. The reply is now in the task twice: once in the artifact and once in the status message. It also counts twice toward `A2ALimits.MaxTaskBytes`. Plan for that when replies are long.

## Use it in a custom agent

Every <xref:A2A.Server.AgentRequestContext> has a <xref:A2A.Server.ConversationMemory> in `Conversation`. The server sets it for the caller's tenant and principal, this context and this task. It is null only for a context you built by hand.

[!code-csharp[](../../../samples/Snippets/ConversationMemory.cs#Custom)]

`ReadEarlierMessagesAsync` never returns the current task.

A runnable version is in [samples/MemoryAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/MemoryAgent/README.md). Its agent reads the earlier notes of the context, and the client sends only the new message.

## Limits

Both limits always apply. The first one reached wins. A read is always bounded.

| Limit | Default | Meaning |
|---|---|---|
| `maxTasks` | 20 | The newest tasks to read. Both must be at least 1, or the call throws `ArgumentOutOfRangeException`. |
| `maxBytes` | 262144 | The UTF-8 length of each message's JSON, added up. The same number in every store. |

How they cut:

- Tasks are ordered by status time, newest first, then by task id. This is the order of `ListTasks`. For a chat that goes turn by turn it is also the order of creation.
- `maxTasks` counts only tasks that can be used. The current task and rejected tasks do not count.
- The size limit starts at the newest message and goes back. It stops at the first message that does not fit. So the result has no holes: you never get an old message that skips a big one after it.
- The result is oldest first. Tasks come in reverse order, and each task's history keeps its own order.
- Artifacts are never read.

### Rejected tasks are left out

A rejected task is one the agent refused. If its messages were replayed, a caller could use the memory to go around the refusal. So they are never part of the memory. Failed and canceled tasks are included.

## The summary

A long chat will outgrow the limits. A summary keeps the main facts in a small text.

- **Who writes it.** Your agent code. Call `SetSummaryAsync`. The server never writes one, because an automatic summary needs an extra model call, and your host should choose that call (which model, how often, what it costs).
- **Last write wins.** Tasks that run at the same time do not merge their summaries.
- **Why it is a user message.** The summary is made from text the caller sent. If it were a system message, caller text could gain system rights. So `ChatClientAgentExecutor` adds it as a user message with a fixed prefix: `Summary of the earlier conversation:`. Do the same in your own agent.
- **Capability.** Writing needs a store that implements <xref:A2A.Server.Storage.IContextSummaryStore>. The in-memory and Postgres stores do. Check `CanStoreSummary` first. `SetSummaryAsync` throws `NotSupportedException` when it is false. `GetSummaryAsync` works with every store and returns null when there is none.
- `SetSummaryAsync(null)` clears it. A context that belongs to another caller looks like a missing one.

## List the conversations of a caller

The protocol has no operation to list conversations. Your host calls <xref:A2A.Server.Storage.TaskStore.ListContextsAsync*> from its own endpoint. Use the caller's own tenant and principal, never values from the request body or the query string. Otherwise one caller can list the conversations of another.

[!code-csharp[](../../../samples/Snippets/ConversationMemory.cs#List)]

The list is newest first by creation time of the context, then by context id. It uses keyset paging, so pages have no gaps and no repeats while new contexts arrive. A bad `PageToken` throws an <xref:A2A.A2AException> with `InvalidParams`.

The order is the creation time, not the last activity. Sorting by last activity would add a write to every task commit.

## Postgres

Migration `0003_conversation_memory` adds one nullable column and one index:

```sql
ALTER TABLE a2a_contexts ADD COLUMN IF NOT EXISTS summary text NULL;
CREATE INDEX IF NOT EXISTS a2a_contexts_owner_ix ON a2a_contexts (tenant, owner, created_ticks DESC, context_id);
```

Apply it like any other migration, with `CreateSchemaOnStartup` or `PostgresSchema.ScriptFor(schema, fromVersion)`. See [Durable stores](durable-stores.md).

The Postgres store cuts the history in the database. Tasks that are too old, and messages over the budget, never reach your process. `ListContextsAsync` is one indexed query and lists every context of the owner, also one with no tasks.

One case is slower. PostgreSQL cannot look into JSON that holds the escape `\u0000` (a NUL character in some text). For a conversation with such a message, the store reads the history with the fallback described under [Third-party stores](#third-party-stores): whole tasks, cut in your process. The result is the same. Only that read costs more.

## Retention

Messages come from stored tasks. When [retention](retention.md) deletes a task, its messages leave the memory. A context row outlives its tasks until the sweep removes it as an orphan. So a context can be listed with no messages to read. Its summary stays until then.

## Third-party stores

`ReadContextMessagesAsync` and `ListContextsAsync` are virtual members of <xref:A2A.Server.Storage.TaskStore>, with fallbacks. A store that does not override them still works. The fallbacks only read, so they are safe with several instances. They cost more:

- **`ReadContextMessagesAsync` fallback.** It pages through `ListAsync` until it has enough tasks. It loads whole tasks, so it holds their artifacts in memory for a moment, and the size limit cuts after the read. Override it when your store can read only the newest tasks and only their history.
- **`ListContextsAsync` fallback.** It pages through all tasks of the owner, takes the distinct context ids and reads each context. Its cost grows with the number of tasks, and it misses a context that has no tasks. Override it with one indexed query.

`SetContextSummaryAsync` has no safe fallback, so it is on the separate interface `IContextSummaryStore`. A store without it cannot keep summaries. `GetSummaryAsync` still works and returns null. See also the [store contract rules](https://github.com/roman-ambinder/a2a/blob/main/docs/adr/0001-store-contract-evolution.md).

## What can go wrong

- **The model forgets earlier turns.** `UseContextMemory` is off, or the caller sends a new `contextId` each time. Or the limits cut the old turns: raise `ContextMemoryMaxTasks` or `ContextMemoryMaxBytes`, or write a summary.
- **The task fails the size limit sooner.** With `UseContextMemory` the reply is stored twice. See [The reply is stored twice](#the-reply-is-stored-twice).
- **`SetSummaryAsync` throws `NotSupportedException`.** The store has no `IContextSummaryStore`. Check `CanStoreSummary`.
- **Reads are slow on a custom store.** It uses the fallbacks. Override `ReadContextMessagesAsync`.
- **A conversation is listed but has no messages.** Retention deleted its tasks. The context stays until the orphan sweep.

## See also

- [Microsoft.Extensions.AI](extensions-ai.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Retention](retention.md)
- [Durable stores](durable-stores.md)
