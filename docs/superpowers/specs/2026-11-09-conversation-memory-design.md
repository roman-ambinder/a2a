# Conversation memory

## Problem

`contextId` groups tasks, but a new task in a context sees nothing earlier, and a caller's conversations cannot be listed.
Messages already live in task rows grouped by `contextId`. No new storage, only a read path, one index and one nullable column.

## Decisions

| Question | Decision | Why |
|---|---|---|
| Order of "last N tasks" | Status time, newest first, then task id (same as ListTasks) | Uses `a2a_tasks_context_ix`. Same as creation order for normal turn-by-turn chats |
| Limits | `MaxTasks` and `MaxBytes` both always apply. First one reached wins | A read is always bounded |
| Byte measure | UTF-8 length of each message's JSON | Same number in every store |
| Budget cut | Newest first. Stop at the first message that does not fit | The result has no holes |
| Rejected tasks | Left out | The agent refused them. Replaying them would go around that refusal |
| "Newest" conversation | Context creation time, newest first | Keyset paging on one index. Sort by last activity would add a write to every task commit |
| Summary role in the prompt | User message with a fixed prefix, never system | A summary is derived from caller text. It must not gain system rights |
| Who writes the summary | The agent's code | An automatic summary would need an extra model call, which the host should choose |

## API (A2A.Server.Storage)

```csharp
public sealed record ContextRecord(...)            // unchanged positional members
{
    public string? Summary { get; init; }          // rolling summary, optional
}

public sealed record ContextHistoryQuery(string Tenant, string Owner, string ContextId, int MaxTasks, long MaxBytes, string? ExcludeTaskId = null);
public sealed record ContextQuery(string Tenant, string Owner, int PageSize, string? PageToken);
public sealed record ContextPage(IReadOnlyList<ContextRecord> Records, string NextPageToken);

// TaskStore: both virtual, per ADR-0001. Both fallbacks only read, so they are safe across instances.
public virtual ValueTask<IReadOnlyList<Message>> ReadContextMessagesAsync(ContextHistoryQuery query, CancellationToken ct = default);
public virtual ValueTask<ContextPage> ListContextsAsync(ContextQuery query, CancellationToken ct = default);

// No safe fallback for a write: a capability interface. InMemory and Postgres implement it.
public interface IContextSummaryStore
{
    // False when the context does not exist in the tenant or belongs to another owner. Last write wins.
    ValueTask<bool> SetContextSummaryAsync(string tenant, string owner, string contextId, string? summary, CancellationToken ct = default);
}
```

`PageCursor` gets `Encode(ContextRecord)`: (created ticks, context id). `Decode` is reused. Arguments below 1 throw `ArgumentOutOfRangeException`.

**`ReadContextMessagesAsync`** returns messages oldest first: tasks in reverse order, each task's history in its own order. Task
order is status time descending, then task id ascending, as in `ListAsync`. It filters by tenant, owner and context, leaves out
`ExcludeTaskId` and rejected tasks. `MaxTasks` counts only tasks that pass those filters.

Fallback: pages through `ListAsync` until it has `MaxTasks` tasks that pass the filters. It loads whole tasks, so it holds
artifacts in memory for a moment; the doc comment says to override it. `InMemoryTaskStore` inherits it (everything is in memory anyway).

**`ListContextsAsync`** returns contexts of that tenant and owner, created time descending, then context id ascending.

Fallback: pages through all of the owner's tasks, takes the distinct context ids, calls `GetContextAsync`, keeps those with the
right owner, sorts and pages. Slow, and it misses a context with no tasks; the doc comment says so. `InMemoryTaskStore` overrides it.
It returns every context of the owner, including one with no tasks.

## Postgres

```sql
ALTER TABLE {s}.a2a_contexts ADD COLUMN IF NOT EXISTS summary text NULL;
CREATE INDEX IF NOT EXISTS a2a_contexts_owner_ix ON {s}.a2a_contexts (tenant, owner, created_ticks DESC, context_id);
```

Both are in migration `0003_conversation_memory.sql`.

The history is cut in the database, so artifacts and messages over budget never reach the client:

```sql
WITH recent AS (
    SELECT task_id, status_ticks, (task::json -> 'history') AS h
      FROM {tasks}
     WHERE tenant = $1 AND owner = $2 AND context_id = $3 AND task_id <> $4 AND state <> {Rejected}
     ORDER BY status_ticks DESC, task_id
     LIMIT $5
), msgs AS (
    SELECT r.status_ticks, r.task_id, e.ord, e.value::text AS msg, octet_length(e.value::text) AS len
      FROM recent r
     CROSS JOIN LATERAL json_array_elements(CASE json_typeof(r.h) WHEN 'array' THEN r.h END) WITH ORDINALITY AS e(value, ord)
), sized AS (
    SELECT *, sum(len) OVER (ORDER BY status_ticks DESC, task_id, ord DESC ROWS UNBOUNDED PRECEDING) AS total FROM msgs
)
SELECT msg FROM sized WHERE total <= $6 ORDER BY status_ticks, task_id DESC, ord
```

`$4` is `ExcludeTaskId ?? ""`. Use `json`, not `jsonb`: `json` keeps each message's text as stored, so its length matches what other stores measure. Pass `$6` as `bigint`.

PostgreSQL's JSON functions raise 22P05 on the escape `\u0000`, with `json` too. So a read that gets 22P05 falls back to the base `ReadContextMessagesAsync`: same result, higher cost for that conversation.

## Agent API (A2A.Server)

`AgentRequestContext.Conversation` (`ConversationMemory?`), set by `A2ARequestHandler` with the caller's tenant and principal, this context and this task. Null only for a context built by hand.

```csharp
public sealed class ConversationMemory
{
    public const int DefaultMaxTasks = 20;
    public const long DefaultMaxBytes = 262_144;
    public bool CanStoreSummary { get; }
    // Earlier messages of this context, without the current task.
    public ValueTask<IReadOnlyList<Message>> ReadEarlierMessagesAsync(int maxTasks = DefaultMaxTasks, long maxBytes = DefaultMaxBytes, CancellationToken cancellationToken = default);
    public ValueTask<string?> GetSummaryAsync(CancellationToken cancellationToken = default);   // null also when owner differs
    public ValueTask SetSummaryAsync(string? summary, CancellationToken cancellationToken = default);  // NotSupportedException when !CanStoreSummary
}
```

The constructor is internal.

## ChatClientAgentExecutor

New options on `ChatClientAgentOptions`: `UseContextMemory` (default false), `ContextMemoryMaxTasks` (20), `ContextMemoryMaxBytes` (262144); limits at least 1.

When on:

- Prompt: system instructions, then the summary if any as a user message `Summary of the earlier conversation:\n{summary}`, then earlier messages, then this task's history. `MaxHistoryMessages` and the rule "do not start with an agent reply" apply to earlier + task history together; the summary is not counted.
- On completion the reply also goes into the status message (`UpdateStatusAsync(Completed, reply)`). A task's history holds status messages only, so without this the next task would see user turns only. Cost: the reply is stored twice and counts twice toward the task size limit. The guide says so.

When off, nothing changes. `AllowUrlParts` still filters earlier messages through `ToModelContent`.

## Not in this change

A protocol operation to list conversations (the host calls `ListContextsAsync` from its own endpoint). Automatic summary generation. Sort by last activity.

## Tests

`TaskStoreContract` (in-memory, Postgres, and a new core-only store that overrides only the abstract members and delegates to in-memory, so `ListContextsAsync` runs through its fallback):

- Read: oldest-first order; excludes the given task; `MaxTasks`; exact `MaxBytes` boundary; other owner, tenant and context see nothing; rejected left out; artifacts never in the result; a message with `\u0000` text does not break it.
- List: newest first; paging has no gaps or repeats; owner and tenant scope; bad token gives InvalidParams.
- Summary (skip when the store lacks `IContextSummaryStore`): round trip through `GetContextAsync` and list; foreign owner returns false and changes nothing; null clears; `GetOrCreateContextAsync` keeps it.

Handler level: a second task in a context reads the first task's messages through `context.Conversation`, not its own.

`ExecutorTests`: on, the second task's prompt has the first user message and reply; off, it does not; `MaxHistoryMessages` trims across both; summary is a user message, never system; a completed reply is in the status message when on and absent when off.
