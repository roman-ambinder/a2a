using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using A2A.Diagnostics;
using A2A.Server.Storage;
using A2A.Server.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Push;

/// <summary>
///     Sends the push deliveries of the <see cref="PushOutbox" /> (spec §4.3.3, §13.2). Each pass leases due deliveries,
///     sends
///     each stored payload (the event as committed) to its push config and records the result: sent (removed), failed and
///     retryable (rescheduled with exponential backoff and jitter, <see cref="PushDeliveryOptions" />) or failed for good
///     (dead-lettered: a URL refused by the SSRF policy, a 4xx other than 408/429, secrets that cannot be unprotected, or
///     the
///     last attempt). Delivery is at least once and in order per task and push config; several instances can
///     run this service against one shared outbox.
/// </summary>
/// <remarks>
///     Each attempt is an <c>a2a.push.deliver</c> span of the <c>A2A.Server</c> source, a child of the agent run that
///     produced
///     the event (<see cref="PushDelivery.TraceParent" />), and its POST carries the span's W3C <c>traceparent</c>.
///     Outcomes are
///     counted on the <see cref="A2ATelemetry.ServerMeterName" /> meter: <see cref="DeliveriesCounterName" /> (tag
///     <see cref="A2ATelemetry.Tags.Outcome" /> = <see cref="A2ATelemetry.Outcomes.Sent" />,
///     <see cref="A2ATelemetry.Outcomes.Retried" />
///     or <see cref="A2ATelemetry.Outcomes.DeadLettered" />) and <see cref="DeadLettersCounterName" />.
///     <c>AddA2AServer</c> registers it
///     as a hosted service; a host that does not run hosted services sends no push notifications. Webhook POSTs go through
///     the
///     <see cref="HttpMessageInvoker" /> given to the constructor (in DI: the keyed service
///     <see cref="InvokerServiceKey" />, SSRF-guarded by default).
/// </remarks>
public sealed class PushDeliveryService : BackgroundService
{
    /// <summary>
    ///     Service key under which <c>AddA2AServer</c> registers the <see cref="HttpMessageInvoker" /> used for webhook
    ///     delivery
    ///     (SSRF-guarded by default). Register your own keyed singleton under this key to replace it deliberately; an unkeyed
    ///     <see cref="HttpMessageInvoker" /> is never used.
    /// </summary>
    public const string InvokerServiceKey = "A2A.PushNotifications";

    /// <summary>Counter of delivery attempts by outcome (<see cref="A2ATelemetry.Metrics.PushDeliveries" />).</summary>
    public const string DeliveriesCounterName = A2ATelemetry.Metrics.PushDeliveries;

    /// <summary>Counter of deliveries moved to the dead letters (<see cref="A2ATelemetry.Metrics.PushDeadLetters" />).</summary>
    public const string DeadLettersCounterName = A2ATelemetry.Metrics.PushDeadLetters;

    private readonly PushConfigStore _configs;
    private readonly ILogger _logger;
    private readonly PushNotificationOptions _notifications;
    private readonly PushDeliveryOptions _options;

    private readonly PushOutbox _outbox;
    private readonly IPushSecretProtector _protector;
    private readonly PushWebhookSender _sender;
    private readonly SemaphoreSlim _slots;
    private readonly TimeProvider _time;

    /// <summary>Creates the service. In an ASP.NET Core app the DI container does this.</summary>
    /// <param name="outbox">The queue of deliveries.</param>
    /// <param name="configs">Source of the push config of each delivery.</param>
    /// <param name="protector">Unprotects stored secrets at delivery time.</param>
    /// <param name="http">
    ///     Sends the webhook requests. Its handler should enforce the SSRF policy (
    ///     <see cref="PushUrlValidator.CreateHandler" />).
    /// </param>
    /// <param name="options">
    ///     Server options; <see cref="A2AServerOptions.PushDelivery" /> and
    ///     <see cref="A2AServerOptions.PushNotifications" /> are used.
    /// </param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="time">The clock for due times and polling. Default <see cref="TimeProvider.System" />.</param>
    public PushDeliveryService(PushOutbox outbox, PushConfigStore configs, IPushSecretProtector protector,
        HttpMessageInvoker http, A2AServerOptions options, ILogger<PushDeliveryService>? logger = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options.PushDelivery;
        _notifications = options.PushNotifications;
        _slots = new SemaphoreSlim(Math.Max(1, _options.BatchSize));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        // The encoder is read at send time: the v0.3 compat package sets it while the endpoints are mapped.
        _sender = new PushWebhookSender(http, options.PushNotifications, _logger, _time, options.SecurityLogger,
            () => options.PushPayloadEncoder);
    }

    /// <summary>Checks the options (<see cref="ValidateOptions" />), then starts sending.</summary>
    /// <exception cref="InvalidOperationException">The lease is too short for one send.</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    ///     Throws <see cref="InvalidOperationException" /> unless <see cref="PushNotificationOptions.Timeout" /> is positive
    ///     and
    ///     finite, <see cref="PushDeliveryOptions.PollInterval" /> is positive,
    ///     <see cref="PushDeliveryOptions.MaxInFlightPerCaller" />
    ///     is null or at least 1, and <see cref="PushDeliveryOptions.Lease" /> is longer
    ///     than twice the timeout (DNS check plus POST) plus 5 seconds: a send must end before its lease does, or another
    ///     instance could lease and send the same delivery at the same time.
    /// </summary>
    internal void ValidateOptions()
    {
        var timeout = _notifications.Timeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new InvalidOperationException(
                $"PushNotifications.Timeout ({timeout}) must be a positive, finite time span.");
        if (_options.PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"PushDelivery.PollInterval ({_options.PollInterval}) must be greater than zero.");
        if (_options.MaxDeliveryAge is { } age && age <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"PushDelivery.MaxDeliveryAge ({age}) must be greater than zero, or null for no limit.");
        if (_options.MaxAttempts is < 1)
            throw new InvalidOperationException(
                $"PushDelivery.MaxAttempts ({_options.MaxAttempts}) must be at least 1, or null for no limit.");
        if (_options.MaxInFlightPerCaller is < 1)
            throw new InvalidOperationException(
                $"PushDelivery.MaxInFlightPerCaller ({_options.MaxInFlightPerCaller}) must be at least 1, or null for no cap.");
        // In 128-bit ticks, so a huge timeout cannot overflow.
        if (_options.Lease.Ticks <= 2 * (Int128)timeout.Ticks + TimeSpan.FromSeconds(5).Ticks)
            throw new InvalidOperationException(
                $"PushDelivery.Lease ({_options.Lease}) must be longer than 2 x PushNotifications.Timeout ({timeout}) + 5 s, " +
                "so a webhook send (DNS check and POST) always ends before its lease.");
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Activity.Current = null; // each delivery names its parent explicitly
        var running = new ConcurrentDictionary<Task, byte>();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait first: at startup the stores may still be creating their schema. A finished send wakes the loop too.
                await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
                try
                {
                    while (await StartDueAsync(running, stoppingToken).ConfigureAwait(false))
                    {
                        // Every free slot got a delivery: more may be due.
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ServerLog.PushOutboxPollFailed(_logger, ex);
                }
            }
        }
        finally
        {
            // Sends see the cancelled token and stop; their leases run out and the deliveries are sent again later.
            await Task.WhenAll(running.Keys).ConfigureAwait(false);
        }
    }

    private async Task WaitForWorkAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = _outbox.WaitForPendingAsync(cts.Token);
        var poll = Task.Delay(_options.PollInterval, _time, cts.Token);
        await Task.WhenAny(signal, poll).ConfigureAwait(false);
        cts.Cancel();
    }

    /// <summary>
    ///     Leases as many due deliveries as there are free send slots (<see cref="PushDeliveryOptions.BatchSize" /> in all)
    ///     and
    ///     starts them. Each send holds one slot, so a slow receiver delays only its own task and config. With
    ///     <see cref="PushDeliveryOptions.MaxInFlightPerCaller" /> set, one caller never holds more than that many. Returns
    ///     true when
    ///     another pass may find work: every free slot got a delivery, or (with a cap) something was leased, because a capped
    ///     pass
    ///     can lease fewer than the free slots while other callers still have due work.
    /// </summary>
    private async Task<bool> StartDueAsync(ConcurrentDictionary<Task, byte> running,
        CancellationToken cancellationToken)
    {
        var free = _slots.CurrentCount;
        if (free == 0) return false;
        var due = await LeaseAsync(free, cancellationToken).ConfigureAwait(false);
        foreach (var entry in due)
        {
            await _slots.WaitAsync(cancellationToken)
                .ConfigureAwait(false); // only this loop takes slots, so one is free
            var send = SendInSlotAsync(entry, cancellationToken);
            running[send] = 0;
            _ = send.ContinueWith(t => running.TryRemove(t, out _), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        return due.Count > 0 && (due.Count == free || _options.MaxInFlightPerCaller is not null);
    }

    private ValueTask<IReadOnlyList<OutboxEntry>> LeaseAsync(int max, CancellationToken cancellationToken)
    {
        return _options.MaxInFlightPerCaller is { } cap
            ? _outbox.LeaseDueCappedAsync(max, _options.Lease, cap, cancellationToken)
            : _outbox.LeaseDueAsync(max, _options.Lease, cancellationToken);
    }

    private async Task SendInSlotAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();
            await DeliverAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
            _outbox.NotifyPending(); // a free slot, and maybe the next delivery of this task and config
        }
    }

    /// <summary>One pass for tests: leases up to a batch of due deliveries and sends them all. Returns how many were leased.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var due = await LeaseAsync(Math.Max(1, _options.BatchSize), cancellationToken).ConfigureAwait(false);
        if (due.Count > 0)
            await Task.WhenAll(due.Select(e => DeliverAsync(e, cancellationToken))).ConfigureAwait(false);
        return due.Count;
    }

    private async Task DeliverAsync(OutboxEntry entry, CancellationToken cancellationToken)
    {
        var d = entry.Delivery;
        var attempt = entry.Attempt + 1;
        A2ATelemetry.TryExtractTraceContext(d.TraceParent, null, out var parent);
        using var activity = ServerTelemetry.Source.HasListeners()
            ? ServerTelemetry.Source.StartActivity(A2ATelemetry.SpanNames.PushDelivery, ActivityKind.Client, parent)
            : null;
        if (activity is { IsAllDataRequested: true })
        {
            A2ATelemetry.SetTask(activity, d.Task.TaskId);
            activity.SetTag(A2ATelemetry.Tags.PushConfigId, d.ConfigId);
            activity.SetTag(A2ATelemetry.Tags.PushSequence, d.Sequence);
            activity.SetTag(A2ATelemetry.Tags.PushAttempts, attempt);
            if (!string.IsNullOrEmpty(d.Task.Tenant)) activity.SetTag(A2ATelemetry.Tags.Tenant, d.Task.Tenant);
        }

        try
        {
            if (_options.MaxDeliveryAge is { } maxAge && entry.Age > maxAge)
            {
                // Too old: no more attempts. The last real error (if any) stays with the dead letter.
                await DeadLetterAsync(entry, activity,
                        $"Expired: queued more than {maxAge} ago" +
                        (entry.LastError is null ? "" : $"; last error: {entry.LastError}"), "Expired")
                    .ConfigureAwait(false);
                return;
            }

            var record = await _configs.GetAsync(d.Task, d.ConfigId, cancellationToken).ConfigureAwait(false);
            if (record is null)
            {
                ServerLog.PushConfigGone(_logger, d.ConfigId, d.Task.TaskId, d.Sequence);
                if (!await _outbox.CompleteAsync(entry, CancellationToken.None).ConfigureAwait(false)) LogLostLease(d);
                return;
            }

            if (activity is not null && Uri.TryCreate(record.Config.Url, UriKind.Absolute, out var target))
                activity.SetTag(A2ATelemetry.Tags.ServerAddress, target.Host);

            TaskPushNotificationConfig config;
            try
            {
                config = Unprotect(record.Config);
            }
            catch (CryptographicException ex)
            {
                ServerLog.PushUnprotectFailed(_logger, ex, d.ConfigId, d.Task.TaskId);
                await DeadLetterAsync(entry, activity, "Cannot unprotect the config secrets", "SecretUnavailable")
                    .ConfigureAwait(false);
                return;
            }

            // Without a listener there is no span: pass on the producer's context, so the receiver still joins the trace.
            var traceContext = activity?.Context ?? parent;
            var result = await _sender
                .SendAsync(config, d, attempt, entry.Payload, activity, traceContext, cancellationToken)
                .ConfigureAwait(false);
            switch (result.Outcome)
            {
                case PushSendOutcome.Sent:
                    bool completed;
                    try
                    {
                        completed = await _outbox.CompleteAsync(entry, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // The receiver got the delivery. Do not count a failed attempt (that would reschedule and resend it
                        // at once): the lease ends, and the delivery is sent again (at least once).
                        ServerLog.PushSentNotRecorded(_logger, ex, d.Sequence, d.Task.TaskId, d.ConfigId);
                        return;
                    }

                    if (completed) Count(A2ATelemetry.Outcomes.Sent);
                    else LogLostLease(d);
                    break;
                case PushSendOutcome.Retry:
                    await RetryOrDeadLetterAsync(entry, attempt, activity, result.Error!, result.ErrorType!,
                        result.StatusCode, result.RetryAfter).ConfigureAwait(false);
                    break;
                default:
                    await DeadLetterAsync(entry, activity, result.Error!, result.ErrorType!).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down: the lease runs out and another pass (or instance) sends it again.
            A2ATelemetry.RecordError(activity, new OperationCanceledException(), true);
        }
        catch (Exception ex)
        {
            // Anything else (a failing store or protector) counts as a failed attempt, so a poison entry ends as a dead letter.
            ServerLog.PushDeliveryFailed(_logger, ex, d.Sequence, d.Task.TaskId, d.ConfigId);
            A2ATelemetry.RecordError(activity, ex);
            try
            {
                await RetryOrDeadLetterAsync(entry, attempt, activity, "Internal error: " + ex.GetType().Name,
                    ex.GetType().FullName ?? ex.GetType().Name).ConfigureAwait(false);
            }
            catch (Exception recordFailed)
            {
                // The outbox itself failed: the entry stays leased and is retried when the lease ends.
                ServerLog.PushRecordFailed(_logger, recordFailed, d.Sequence, d.Task.TaskId);
            }
        }
    }

    /// <summary>
    ///     A retryable failure: reschedules with backoff (or the wait <see cref="PushDeliveryOptions.RetryPolicy" />
    ///     returns), or dead-letters after <see cref="PushDeliveryOptions.MaxAttempts" /> (when set) or when the policy
    ///     returns null.
    /// </summary>
    private async Task RetryOrDeadLetterAsync(OutboxEntry entry, int attempt, Activity? activity, string error,
        string errorType, int? statusCode = null, TimeSpan? retryAfter = null)
    {
        var d = entry.Delivery;
        if (_options.MaxAttempts is { } maxAttempts && attempt >= maxAttempts)
        {
            ServerLog.PushWebhookGaveUp(_logger, d.ConfigId, d.Task.TaskId, attempt);
            await DeadLetterAsync(entry, activity, error, "GaveUp").ConfigureAwait(false);
            return;
        }

        var delay = Backoff(attempt);
        if (_options.RetryPolicy is { } policy)
        {
            TimeSpan? decided;
            try
            {
                decided = policy(new PushRetryContext(d.Task, d.ConfigId, d.Sequence, attempt, error, statusCode,
                    retryAfter, delay));
            }
            catch (Exception ex)
            {
                ServerLog.PushRetryPolicyFailed(_logger, ex, d.Sequence, d.Task.TaskId, d.ConfigId);
                decided = delay;
            }

            if (decided is not { } wait)
            {
                ServerLog.PushWebhookGaveUp(_logger, d.ConfigId, d.Task.TaskId, attempt);
                await DeadLetterAsync(entry, activity, error, "GaveUp").ConfigureAwait(false);
                return;
            }

            delay = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxPolicyDelay ? MaxPolicyDelay : wait;
        }

        Fail(activity, errorType);
        activity?.AddEvent(new ActivityEvent(A2ATelemetry.Events.Retry));
        if (await _outbox
                .RescheduleAsync(entry, attempt, Due(delay), error, CancellationToken.None)
                .ConfigureAwait(false))
            Count(A2ATelemetry.Outcomes.Retried);
        else
            LogLostLease(d);
    }

    private async Task DeadLetterAsync(OutboxEntry entry, Activity? activity, string error, string errorType)
    {
        Fail(activity, errorType);
        if (!await _outbox.DeadLetterAsync(entry, error, CancellationToken.None).ConfigureAwait(false))
        {
            LogLostLease(entry.Delivery);
            return;
        }

        Count(A2ATelemetry.Outcomes.DeadLettered);
        if (ServerTelemetry.PushDeadLetters.Enabled) ServerTelemetry.PushDeadLetters.Add(1);
        if (_options.OnDeadLetter is not { } hook) return;
        var d = entry.Delivery;
        try
        {
            await hook(new PushDeadLetterInfo(d.Task, d.ConfigId, d.Sequence, error)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServerLog.PushDeadLetterHookFailed(_logger, ex, d.Sequence, d.Task.TaskId,
                d.ConfigId); // never affects delivery
        }
    }

    private static void Count(string outcome)
    {
        if (ServerTelemetry.PushDeliveries.Enabled)
            ServerTelemetry.PushDeliveries.Add(1,
                new KeyValuePair<string, object?>(A2ATelemetry.Tags.Outcome, outcome));
    }

    /// <summary>A result write found the entry gone or leased by someone else (this send outlived its lease).</summary>
    private void LogLostLease(PushDelivery d)
    {
        ServerLog.PushLeaseLost(_logger, d.Sequence, d.Task.TaskId, d.ConfigId);
    }

    /// <summary>The longest wait a <see cref="PushDeliveryOptions.RetryPolicy" /> can set, so every outbox can store it.</summary>
    internal static readonly TimeSpan MaxPolicyDelay = TimeSpan.FromDays(30);

    private DateTimeOffset Due(TimeSpan delay)
    {
        return _time.GetUtcNow() + delay;
    }

    /// <summary>
    ///     The wait after <paramref name="attempt" /> failed attempts: InitialBackoff · 2^(attempt-1), ±20 %, at most
    ///     MaxBackoff.
    /// </summary>
    private TimeSpan Backoff(int attempt)
    {
        var baseTicks = _options.InitialBackoff.Ticks * Math.Pow(2, Math.Min(attempt - 1, 62));
        var jittered = baseTicks * (0.8 + 0.4 * Random.Shared.NextDouble());
        return TimeSpan.FromTicks((long)Math.Min(jittered, _options.MaxBackoff.Ticks));
    }

    /// <summary>Returns a copy of the stored config with its secrets in plaintext. Used only at delivery time.</summary>
    private TaskPushNotificationConfig Unprotect(TaskPushNotificationConfig stored)
    {
        var config = stored.DeepCopy(); // the store owns the stored config
        if (!string.IsNullOrEmpty(config.Token)) config.Token = _protector.Unprotect(config.Token);
        if (config.Authentication is { Credentials.Length: > 0 } auth)
            auth.Credentials = _protector.Unprotect(auth.Credentials);
        return config;
    }

    /// <summary>Marks an attempt that failed, was refused or was dropped.</summary>
    private static void Fail(Activity? activity, string type)
    {
        if (activity is null) return;
        activity.SetTag(A2ATelemetry.Tags.ErrorType, type);
        activity.SetStatus(ActivityStatusCode.Error, type);
    }
}