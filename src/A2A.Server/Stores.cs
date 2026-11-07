using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using A2A.Json;

namespace A2A.Server;

/// <summary>A stored task plus ownership data used for authorization scoping (spec §13.1).</summary>
public sealed class TaskRecord
{
    public required AgentTask Task { get; set; }

    /// <summary><see cref="ServerCallContext.Principal"/> of the creator.</summary>
    public string? OwnerId { get; set; }

    public string? Tenant { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A ListTasks query, already scoped to the caller.</summary>
public sealed class TaskQuery
{
    public string? OwnerId { get; init; }
    public string? Tenant { get; init; }
    public string? ContextId { get; init; }
    public TaskState? Status { get; init; }
    public DateTimeOffset? StatusTimestampAfter { get; init; }
    public int PageSize { get; init; } = 50;
    public string? PageToken { get; init; }
}

public sealed record TaskQueryResult(IReadOnlyList<TaskRecord> Records, string NextPageToken, int TotalSize);

/// <summary>Task persistence. Implementations must return copies, not shared instances.</summary>
public interface ITaskStore
{
    Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    /// <summary>Insert or replace.</summary>
    Task SaveAsync(TaskRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns tasks matching the query, sorted by status timestamp descending (spec §3.1.4). Must filter by
    /// <see cref="TaskQuery.OwnerId"/> and <see cref="TaskQuery.Tenant"/> before counting or paging, so nothing outside the caller's scope leaks.
    /// Throws <see cref="A2AException"/> (InvalidParams) for a bad page token.
    /// </summary>
    Task<TaskQueryResult> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default);
}

/// <summary>Webhook registrations. Implementations must return copies.</summary>
public interface IPushNotificationConfigStore
{
    Task SaveAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default);
    Task<TaskPushNotificationConfig?> GetAsync(string taskId, string configId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskPushNotificationConfig>> ListAsync(string taskId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string taskId, string configId, CancellationToken cancellationToken = default);
}

/// <summary>Process-local task store. For development and tests; data is lost on restart.</summary>
public sealed class InMemoryTaskStore : ITaskStore
{
    private readonly ConcurrentDictionary<string, TaskRecord> _tasks = new(StringComparer.Ordinal);

    public Task<TaskRecord?> GetAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_tasks.TryGetValue(taskId, out var r) ? Copy(r) : null);

    public Task SaveAsync(TaskRecord record, CancellationToken cancellationToken = default)
    {
        _tasks[record.Task.Id] = Copy(record);
        return Task.CompletedTask;
    }

    public Task<TaskQueryResult> QueryAsync(TaskQuery query, CancellationToken cancellationToken = default)
    {
        var cursor = PageCursor.Decode(query.PageToken);
        var matching = _tasks.Values
            .Where(r => r.OwnerId == query.OwnerId && r.Tenant == query.Tenant)
            .Where(r => query.ContextId is null || r.Task.ContextId == query.ContextId)
            .Where(r => query.Status is null || r.Task.Status.State == query.Status)
            .Where(r => query.StatusTimestampAfter is null || (r.Task.Status.Timestamp ?? r.CreatedAt) >= query.StatusTimestampAfter)
            .OrderByDescending(r => (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks)
            .ThenBy(r => r.Task.Id, StringComparer.Ordinal)
            .ToList();

        IEnumerable<TaskRecord> rest = matching;
        if (cursor is { } c)
        {
            rest = matching.SkipWhile(r =>
            {
                var ticks = (r.Task.Status.Timestamp ?? r.CreatedAt).UtcTicks;
                return ticks > c.Ticks || (ticks == c.Ticks && string.CompareOrdinal(r.Task.Id, c.Id) <= 0);
            });
        }

        var page = rest.Take(query.PageSize + 1).ToList();
        var hasMore = page.Count > query.PageSize;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var next = hasMore ? PageCursor.Encode(page[^1]) : string.Empty;
        return Task.FromResult(new TaskQueryResult(page.Select(Copy).ToList(), next, matching.Count));
    }

    private static TaskRecord Copy(TaskRecord r) => new()
    {
        Task = A2AJson.Clone(r.Task), OwnerId = r.OwnerId, Tenant = r.Tenant, CreatedAt = r.CreatedAt,
    };
}

/// <summary>Opaque keyset cursor: (status timestamp ticks, task id).</summary>
public static class PageCursor
{
    public static string Encode(TaskRecord last)
    {
        var ticks = (last.Task.Status.Timestamp ?? last.CreatedAt).UtcTicks;
        return Signing.Base64Url.Encode(Encoding.UTF8.GetBytes($"{ticks.ToString(CultureInfo.InvariantCulture)}|{last.Task.Id}"));
    }

    public static (long Ticks, string Id)? Decode(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var s = Encoding.UTF8.GetString(Signing.Base64Url.Decode(token));
            var bar = s.IndexOf('|');
            if (bar <= 0) throw new FormatException();
            return (long.Parse(s[..bar], CultureInfo.InvariantCulture), s[(bar + 1)..]);
        }
        catch (FormatException)
        {
            throw A2AException.InvalidParams("pageToken is invalid");
        }
    }
}

public sealed class InMemoryPushNotificationConfigStore : IPushNotificationConfigStore
{
    private readonly ConcurrentDictionary<(string TaskId, string Id), TaskPushNotificationConfig> _configs = new();

    public Task SaveAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default)
    {
        _configs[(config.TaskId!, config.Id!)] = A2AJson.Clone(config);
        return Task.CompletedTask;
    }

    public Task<TaskPushNotificationConfig?> GetAsync(string taskId, string configId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_configs.TryGetValue((taskId, configId), out var c) ? A2AJson.Clone(c) : null);

    public Task<IReadOnlyList<TaskPushNotificationConfig>> ListAsync(string taskId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskPushNotificationConfig>>(_configs
            .Where(kv => kv.Key.TaskId == taskId)
            .Select(kv => A2AJson.Clone(kv.Value))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .ToList());

    public Task<bool> DeleteAsync(string taskId, string configId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_configs.TryRemove((taskId, configId), out _));
}

/// <summary>Decides whether a caller may see a task.</summary>
public interface ITaskAccessPolicy
{
    bool CanAccess(TaskRecord record, ServerCallContext call);
}

/// <summary>Default: a task is visible only to its creator, within the same tenant. Matches <see cref="TaskQuery"/> scoping.</summary>
public sealed class OwnerTenantAccessPolicy : ITaskAccessPolicy
{
    public bool CanAccess(TaskRecord record, ServerCallContext call) =>
        string.Equals(record.OwnerId, call.Principal, StringComparison.Ordinal) &&
        string.Equals(record.Tenant, call.Tenant, StringComparison.Ordinal);
}
