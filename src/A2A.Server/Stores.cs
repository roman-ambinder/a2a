using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using A2A.Json;
using A2A.Server.Storage;

namespace A2A.Server;

/// <summary>Webhook registrations. Implementations must return copies.</summary>
public interface IPushNotificationConfigStore
{
    Task SaveAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken = default);
    Task<TaskPushNotificationConfig?> GetAsync(string taskId, string configId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TaskPushNotificationConfig>> ListAsync(string taskId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string taskId, string configId, CancellationToken cancellationToken = default);
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
