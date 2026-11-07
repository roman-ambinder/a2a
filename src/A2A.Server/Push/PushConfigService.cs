using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
/// Push notification config operations: webhook URL checks, the per-task limit, secret protection and redaction.
/// The caller has already authorized the call and built the <see cref="TaskKey"/>. Secrets never leave this class unredacted.
/// After every stored or deleted config it tells the optional <see cref="IPushConfigChangeListener"/> (in an ASP.NET Core app,
/// the <see cref="HttpPushNotificationSender"/>), so delivery sees the change from the next event on.
/// </summary>
public sealed class PushConfigService(PushConfigStore store, IPushSecretProtector protector, A2AServerOptions options,
    IPushConfigChangeListener? changes = null)
{
    /// <summary>Checks the webhook URL, applies the per-task limit, stores a copy of <paramref name="config"/> and returns it with secrets removed. The caller's instance is not changed.</summary>
    public async ValueTask<TaskPushNotificationConfig> CreateAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken cancellationToken)
    {
        await ValidateUrlAsync(config.Url, cancellationToken).ConfigureAwait(false);
        var stored = config.DeepCopy(); // the caller's config is never changed
        stored.Id = string.IsNullOrEmpty(stored.Id) ? TaskIds.New() : stored.Id;
        stored.Tenant = key.Tenant;
        await EnsureCapacityAsync(key, stored.Id, cancellationToken).ConfigureAwait(false);
        await SaveAsync(key, stored, cancellationToken).ConfigureAwait(false);
        return Redact(stored);
    }

    /// <summary>Returns one registration with secrets removed. Throws TaskNotFound when it does not exist.</summary>
    public async ValueTask<TaskPushNotificationConfig> GetAsync(TaskKey key, string configId, CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(key, configId, cancellationToken).ConfigureAwait(false)
            ?? throw ConfigNotFound(key.TaskId, configId);
        return Redact(record.Config);
    }

    /// <summary>Returns the registrations of a task with secrets removed, <paramref name="pageSize"/> per page (default and maximum 100).</summary>
    public async ValueTask<ListTaskPushNotificationConfigsResponse> ListAsync(TaskKey key, int pageSize, string? pageToken, CancellationToken cancellationToken)
    {
        var all = (await store.ListAsync(key, cancellationToken).ConfigureAwait(false)).Select(r => Redact(r.Config)).ToList();

        var size = pageSize > 0 ? Math.Min(pageSize, 100) : 100;
        IEnumerable<TaskPushNotificationConfig> rest = all;
        if (!string.IsNullOrEmpty(pageToken))
            rest = all.SkipWhile(c => string.CompareOrdinal(c.Id, pageToken) <= 0);
        var page = rest.Take(size + 1).ToList();
        string? next = null;
        if (page.Count > size)
        {
            page.RemoveAt(page.Count - 1);
            next = page[^1].Id;
        }
        return new ListTaskPushNotificationConfigsResponse { Configs = page, NextPageToken = next };
    }

    /// <summary>Deletes a registration. Throws TaskNotFound when it does not exist.</summary>
    public async ValueTask DeleteAsync(TaskKey key, string configId, CancellationToken cancellationToken)
    {
        if (!await store.DeleteAsync(key, configId, cancellationToken).ConfigureAwait(false))
            throw ConfigNotFound(key.TaskId, configId);
        changes?.ConfigChanged(key);
    }

    /// <summary>Checks the webhook URL of the inline config of a SendMessage request, before anything is committed.</summary>
    public ValueTask ValidateInlineAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken cancellationToken) =>
        ValidateUrlAsync(config.Url, cancellationToken);

    /// <summary>Throws InvalidRequest when adding config <paramref name="configId"/> would exceed the per-task limit. Replacing an existing id is allowed.</summary>
    public async ValueTask EnsureCapacityAsync(TaskKey key, string configId, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(key, configId, cancellationToken).ConfigureAwait(false) is not null) return;
        var limit = options.Limits.MaxPushConfigsPerTask;
        if (await store.CountAsync(key, cancellationToken).ConfigureAwait(false) >= limit)
            throw new A2AException(A2AErrorCode.InvalidRequest, $"Too many push notification configs for this task (max {limit}).");
    }

    /// <summary>Protects the secrets of the inline config and stores a copy. The caller's instance is not changed.</summary>
    public ValueTask SaveInlineAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken cancellationToken) => SaveAsync(key, config, cancellationToken);

    internal ValueTask ValidateUrlAsync(string url, CancellationToken ct) => PushUrlValidator.ValidateAsync(url, options.PushNotifications, ct);

    /// <summary>Protects the secrets of <paramref name="config"/> and stores a copy, which the store then owns. Then tells the change listener.</summary>
    private async ValueTask SaveAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken ct)
    {
        var copy = config.DeepCopy();
        if (!string.IsNullOrEmpty(copy.Token)) copy.Token = protector.Protect(copy.Token);
        if (copy.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = protector.Protect(auth.Credentials);
        await store.SaveAsync(new PushConfigRecord(key, copy), ct).ConfigureAwait(false);
        changes?.ConfigChanged(key);
    }

    /// <summary>Returns a copy with the token and credentials blanked; <paramref name="config"/> (often the stored one) is not changed.</summary>
    private static TaskPushNotificationConfig Redact(TaskPushNotificationConfig config)
    {
        var copy = config.DeepCopy();
        if (!string.IsNullOrEmpty(copy.Token)) copy.Token = "";
        if (copy.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = "";
        return copy;
    }

    private static A2AException ConfigNotFound(string taskId, string id) =>
        new(A2AErrorCode.TaskNotFound, "Push notification config not found") { Metadata = new() { ["taskId"] = taskId, ["configId"] = id } };
}
