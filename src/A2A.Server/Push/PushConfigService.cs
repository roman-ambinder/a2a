using A2A.Diagnostics;
using A2A.Server.Storage;
using A2A.Server.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Push;

/// <summary>
///     Push notification config operations: webhook URL checks, the per-task limit (enforced atomically by
///     <see cref="PushConfigStore.SaveIfUnderLimitAsync" />), secret protection and redaction. The caller has already
///     authorized the
///     call and built the <see cref="TaskKey" />. Secrets never leave this class unredacted. Deliveries read the configs
///     when each
///     event is committed, so a change applies from the next event on.
/// </summary>
/// <param name="store">The push config store.</param>
/// <param name="protector">Protects secrets before they are stored.</param>
/// <param name="options">Server options (URL policy, <see cref="A2ALimits.MaxPushConfigsPerTask" />).</param>
/// <param name="logger">Optional logger.</param>
public sealed class PushConfigService(
    PushConfigStore store,
    IPushSecretProtector protector,
    A2AServerOptions options,
    ILogger<PushConfigService>? logger = null)
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>The store the configs live in.</summary>
    internal PushConfigStore Store => store;

    /// <summary>
    ///     Checks the webhook URL, applies the per-task limit, stores a copy of <paramref name="config" /> and returns it
    ///     with secrets removed. The caller's instance is not changed.
    /// </summary>
    public async ValueTask<TaskPushNotificationConfig> CreateAsync(TaskKey key, TaskPushNotificationConfig config,
        CancellationToken cancellationToken)
    {
        await ValidateUrlAsync(config.Url, A2AProtocol.Methods.CreateTaskPushNotificationConfig, key.Tenant, key.Owner,
            key.TaskId, cancellationToken).ConfigureAwait(false);
        var stored = config.DeepCopy(); // the caller's config is never changed
        stored.Id = string.IsNullOrEmpty(stored.Id) ? TaskIds.New() : stored.Id;
        stored.Tenant = key.Tenant;
        if (!await SaveIfUnderLimitAsync(key, stored, cancellationToken).ConfigureAwait(false))
            throw TooManyConfigs(options.Limits.MaxPushConfigsPerTask);
        return Redact(stored);
    }

    /// <summary>Returns one registration with secrets removed. Throws TaskNotFound when it does not exist.</summary>
    public async ValueTask<TaskPushNotificationConfig> GetAsync(TaskKey key, string configId,
        CancellationToken cancellationToken)
    {
        var record = await store.GetAsync(key, configId, cancellationToken).ConfigureAwait(false)
                     ?? throw ConfigNotFound(key.TaskId, configId);
        return Redact(record.Config);
    }

    /// <summary>
    ///     Returns the registrations of a task with secrets removed, <paramref name="pageSize" /> per page (default and
    ///     maximum 100).
    /// </summary>
    public async ValueTask<ListTaskPushNotificationConfigsResponse> ListAsync(TaskKey key, int pageSize,
        string? pageToken, CancellationToken cancellationToken)
    {
        var all = (await store.ListAsync(key, cancellationToken).ConfigureAwait(false)).Select(r => Redact(r.Config))
            .ToList();

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

    /// <summary>
    ///     Deletes a registration. Idempotent (spec §3.1.10): deleting one that does not exist succeeds and changes
    ///     nothing.
    /// </summary>
    public async ValueTask DeleteAsync(TaskKey key, string configId, CancellationToken cancellationToken)
    {
        _ = await store.DeleteAsync(key, configId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Checks the webhook URL of the inline config of a SendMessage request, before anything is committed.</summary>
    public ValueTask ValidateInlineAsync(TaskPushNotificationConfig config, CancellationToken cancellationToken)
    {
        return ValidateUrlAsync(config.Url, A2AProtocol.Methods.SendMessage, config.Tenant ?? "", null, config.TaskId,
            cancellationToken);
    }

    /// <summary>The same check for a call, so a refusal names the caller in the security event.</summary>
    internal ValueTask ValidateInlineAsync(TaskPushNotificationConfig config, ServerCallContext call,
        CancellationToken cancellationToken)
    {
        return ValidateUrlAsync(config.Url, A2AProtocol.Methods.SendMessage, call.Tenant, call.Principal, config.TaskId,
            cancellationToken);
    }

    /// <summary>
    ///     Saves the inline config of a follow-up atomically under the per-task limit, before its message is committed; throws
    ///     InvalidRequest at the limit (nothing stored). Returns whether the config id was new (so a request that is not
    ///     committed
    ///     after all removes it again with <see cref="TryDeleteAsync" />).
    /// </summary>
    internal async ValueTask<bool> SaveInlineUnderLimitAsync(TaskKey key, TaskPushNotificationConfig config,
        CancellationToken cancellationToken)
    {
        var isNew = await store.GetAsync(key, config.Id!, cancellationToken).ConfigureAwait(false) is null;
        if (!await SaveIfUnderLimitAsync(key, config, cancellationToken).ConfigureAwait(false))
            throw TooManyConfigs(options.Limits.MaxPushConfigsPerTask);
        return isNew;
    }

    /// <summary>
    ///     Protects the secrets of the inline config of a new task and stores a copy (a new task has no other configs).
    ///     The caller's instance is not changed.
    /// </summary>
    public ValueTask SaveInlineAsync(TaskKey key, TaskPushNotificationConfig config,
        CancellationToken cancellationToken)
    {
        return store.SaveAsync(ProtectedRecord(key, config), cancellationToken);
    }

    /// <summary>Best effort: removes a config saved for a task whose creation failed, or for a message that was not committed.</summary>
    internal async ValueTask TryDeleteAsync(TaskKey key, string configId)
    {
        try
        {
            await store.DeleteAsync(key, configId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServerLog.PushConfigCleanupFailed(_logger, ex, configId, key.TaskId);
        }
    }

    /// <summary>
    ///     Checks the webhook URL. A refusal (InvalidParams) is a <c>webhook_refused</c> security event; the event never
    ///     carries the URL.
    /// </summary>
    private async ValueTask ValidateUrlAsync(string url, string operation, string tenant, string? principal,
        string? taskId, CancellationToken ct)
    {
        try
        {
            await PushUrlValidator.ValidateAsync(url, options.PushNotifications, ct).ConfigureAwait(false);
        }
        catch (A2AException ex) when (ex.Code == A2AErrorCode.InvalidParams)
        {
            SecurityEventLog.Record(options.SecurityLogger, A2ATelemetry.SecurityEventNames.WebhookRefused, operation,
                tenant, principal, taskId);
            throw;
        }
    }

    private ValueTask<bool> SaveIfUnderLimitAsync(TaskKey key, TaskPushNotificationConfig config, CancellationToken ct)
    {
        return store.SaveIfUnderLimitAsync(ProtectedRecord(key, config), options.Limits.MaxPushConfigsPerTask, ct);
    }

    /// <summary>A copy of <paramref name="config" /> with its secrets protected, which the store then owns.</summary>
    private PushConfigRecord ProtectedRecord(TaskKey key, TaskPushNotificationConfig config)
    {
        var copy = config.DeepCopy();
        if (!string.IsNullOrEmpty(copy.Token)) copy.Token = protector.Protect(copy.Token);
        if (copy.Authentication is { Credentials.Length: > 0 } auth)
            auth.Credentials = protector.Protect(auth.Credentials);
        return new PushConfigRecord(key, copy);
    }

    /// <summary>
    ///     Returns a copy with the token and credentials blanked; <paramref name="config" /> (often the stored one) is
    ///     not changed.
    /// </summary>
    private static TaskPushNotificationConfig Redact(TaskPushNotificationConfig config)
    {
        var copy = config.DeepCopy();
        if (!string.IsNullOrEmpty(copy.Token)) copy.Token = "";
        if (copy.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = "";
        return copy;
    }

    private static A2AException TooManyConfigs(int limit)
    {
        return new A2AException(A2AErrorCode.InvalidRequest,
            $"Too many push notification configs for this task (max {limit}).");
    }

    private static A2AException ConfigNotFound(string taskId, string id)
    {
        return new A2AException(A2AErrorCode.TaskNotFound, "Push notification config not found")
            { Metadata = new Dictionary<string, string> { ["taskId"] = taskId, ["configId"] = id } };
    }
}