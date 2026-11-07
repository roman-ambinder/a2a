using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using A2A.Diagnostics;
using A2A.Json;
using A2A.Server.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace A2A.Server.Push;

/// <summary>
/// Sends the push deliveries of the <see cref="PushOutbox"/> (spec §4.3.3, §13.2). Each pass leases due deliveries, sends
/// each stored payload (the event as committed) to its push config and records the result: sent (removed), failed and
/// retryable (rescheduled with exponential backoff and jitter, <see cref="PushDeliveryOptions"/>) or failed for good
/// (dead-lettered: a URL refused by the SSRF policy, a 4xx other than 408/429, secrets that cannot be unprotected, or the
/// last attempt). Delivery is at least once and in order per task and push config; several instances can
/// run this service against one shared outbox.
/// </summary>
/// <remarks>
/// Each attempt is an <c>a2a.push.deliver</c> span of the <c>A2A.Server</c> source, a child of the agent run that produced
/// the event (<see cref="PushDelivery.TraceParent"/>), and its POST carries the span's W3C <c>traceparent</c>. Outcomes are
/// counted on the <c>A2A</c> meter: <see cref="DeliveriesCounterName"/> (tag <c>outcome</c> = <c>sent</c>, <c>retried</c>
/// or <c>dead_lettered</c>) and <see cref="DeadLettersCounterName"/>. <c>AddA2AServer</c> registers it as a hosted service;
/// a host that does not run hosted services sends no push notifications.
/// </remarks>
public sealed class PushDeliveryService : BackgroundService
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for webhook POSTs. Its primary handler enforces the SSRF policy.</summary>
    public const string HttpClientName = "A2A.PushNotifications";

    /// <summary>Counter of delivery attempts by outcome (tag <c>outcome</c>: <c>sent</c>, <c>retried</c>, <c>dead_lettered</c>).</summary>
    public const string DeliveriesCounterName = "a2a.push.deliveries";

    /// <summary>Counter of deliveries moved to the dead letters.</summary>
    public const string DeadLettersCounterName = "a2a.push.dead_letters";

    private static readonly Meter Meter = new("A2A");
    private static readonly Counter<long> Deliveries = Meter.CreateCounter<long>(DeliveriesCounterName,
        description: "Push delivery attempts by outcome: sent, retried or dead_lettered.");
    private static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>(DeadLettersCounterName,
        description: "Push deliveries that failed for good and were moved to the dead letters.");

    private readonly PushOutbox _outbox;
    private readonly PushConfigStore _configs;
    private readonly IPushSecretProtector _protector;
    private readonly PushDeliveryOptions _options;
    private readonly PushNotificationOptions _notifications;
    private readonly SemaphoreSlim _slots;
    private readonly PushWebhookSender _sender;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>Created by DI. The optional <paramref name="time"/> is the clock for due times and polling.</summary>
    public PushDeliveryService(PushOutbox outbox, PushConfigStore configs, IPushSecretProtector protector,
        IHttpClientFactory httpClientFactory, IOptions<A2AServerOptions> options, ILogger<PushDeliveryService>? logger = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options.Value.PushDelivery;
        _notifications = options.Value.PushNotifications;
        _slots = new SemaphoreSlim(Math.Max(1, _options.BatchSize));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = time ?? TimeProvider.System;
        _sender = new PushWebhookSender(httpClientFactory, options.Value.PushNotifications, _logger, _time);
    }

    /// <summary>Checks the options (<see cref="ValidateOptions"/>), then starts sending.</summary>
    /// <exception cref="InvalidOperationException">The lease is too short for one send.</exception>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateOptions();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> unless <see cref="PushNotificationOptions.Timeout"/> is positive and
    /// finite, <see cref="PushDeliveryOptions.PollInterval"/> is positive, and <see cref="PushDeliveryOptions.Lease"/> is longer
    /// than twice the timeout (DNS check plus POST) plus 5 seconds: a send must end before its lease does, or another
    /// instance could lease and send the same delivery at the same time.
    /// </summary>
    internal void ValidateOptions()
    {
        var timeout = _notifications.Timeout;
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new InvalidOperationException($"PushNotifications.Timeout ({timeout}) must be a positive, finite time span.");
        if (_options.PollInterval <= TimeSpan.Zero)
            throw new InvalidOperationException($"PushDelivery.PollInterval ({_options.PollInterval}) must be greater than zero.");
        // In 128-bit ticks, so a huge timeout cannot overflow.
        if ((Int128)_options.Lease.Ticks <= (2 * (Int128)timeout.Ticks) + TimeSpan.FromSeconds(5).Ticks)
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
                    _logger.LogError(ex, "Push outbox poll failed");
                }
            }
        }
        finally
        {
            // Sends see the cancelled token and stop; their leases run out and the deliveries are sent again later.
            await Task.WhenAll(running.Keys).ConfigureAwait(false);
        }
    }

    private async Task WaitForWorkAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var signal = _outbox.WaitForPendingAsync(cts.Token);
        var poll = Task.Delay(_options.PollInterval, _time, cts.Token);
        await Task.WhenAny(signal, poll).ConfigureAwait(false);
        cts.Cancel();
    }

    /// <summary>
    /// Leases as many due deliveries as there are free send slots (<see cref="PushDeliveryOptions.BatchSize"/> in all) and
    /// starts them. Each send holds one slot, so a slow receiver delays only its own task and config. Returns true when
    /// every free slot got a delivery.
    /// </summary>
    private async Task<bool> StartDueAsync(ConcurrentDictionary<Task, byte> running, CancellationToken ct)
    {
        var free = _slots.CurrentCount;
        if (free == 0) return false;
        var due = await _outbox.LeaseDueAsync(free, _options.Lease, ct).ConfigureAwait(false);
        foreach (var entry in due)
        {
            await _slots.WaitAsync(ct).ConfigureAwait(false); // only this loop takes slots, so one is free
            var send = SendInSlotAsync(entry, ct);
            running[send] = 0;
            _ = send.ContinueWith(t => running.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return due.Count > 0 && due.Count == free;
    }

    private async Task SendInSlotAsync(OutboxEntry entry, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            await DeliverAsync(entry, ct).ConfigureAwait(false);
        }
        finally
        {
            _slots.Release();
            _outbox.NotifyPending(); // a free slot, and maybe the next delivery of this task and config
        }
    }

    /// <summary>One pass for tests: leases up to a batch of due deliveries and sends them all. Returns how many were leased.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var due = await _outbox.LeaseDueAsync(Math.Max(1, _options.BatchSize), _options.Lease, ct).ConfigureAwait(false);
        if (due.Count > 0) await Task.WhenAll(due.Select(e => DeliverAsync(e, ct))).ConfigureAwait(false);
        return due.Count;
    }

    private async Task DeliverAsync(OutboxEntry entry, CancellationToken ct)
    {
        var d = entry.Delivery;
        var attempt = entry.Attempt + 1;
        A2ATelemetry.TryExtractTraceContext(d.TraceParent, null, out var parent);
        using var activity = ServerTelemetry.Source.StartActivity(A2ATelemetry.SpanNames.PushDelivery, ActivityKind.Client, parent);
        if (activity is not null)
        {
            A2ATelemetry.SetTask(activity, d.Task.TaskId);
            activity.SetTag(A2ATelemetry.Tags.PushConfigId, d.ConfigId);
            activity.SetTag(A2ATelemetry.Tags.PushSequence, d.Sequence);
            activity.SetTag(A2ATelemetry.Tags.PushAttempts, attempt);
            if (!string.IsNullOrEmpty(d.Task.Tenant)) activity.SetTag(A2ATelemetry.Tags.Tenant, d.Task.Tenant);
        }
        try
        {
            var record = await _configs.GetAsync(d.Task, d.ConfigId, ct).ConfigureAwait(false);
            if (record is null)
            {
                _logger.LogDebug("Push config {ConfigId} of task {TaskId} was deleted; dropping delivery {Sequence}", d.ConfigId, d.Task.TaskId, d.Sequence);
                if (!await _outbox.CompleteAsync(entry, CancellationToken.None).ConfigureAwait(false)) LogLostLease(d);
                return;
            }
            if (activity is not null && Uri.TryCreate(record.Config.Url, UriKind.Absolute, out var target)) activity.SetTag(A2ATelemetry.Tags.ServerAddress, target.Host);

            TaskPushNotificationConfig config;
            try
            {
                config = Unprotect(record.Config);
            }
            catch (CryptographicException ex)
            {
                _logger.LogError(ex, "Cannot unprotect secrets of webhook {ConfigId} for task {TaskId}", d.ConfigId, d.Task.TaskId);
                await DeadLetterAsync(entry, activity, "Cannot unprotect the config secrets", "SecretUnavailable").ConfigureAwait(false);
                return;
            }

            // Without a listener there is no span: pass on the producer's context, so the receiver still joins the trace.
            var traceContext = activity?.Context ?? parent;
            var result = await _sender.SendAsync(config, d, entry.Payload, activity, traceContext, ct).ConfigureAwait(false);
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
                        _logger.LogWarning(ex, "Sent push delivery {Sequence} of task {TaskId} to webhook {ConfigId}, but could not record it; it is sent again when its lease ends",
                            d.Sequence, d.Task.TaskId, d.ConfigId);
                        return;
                    }
                    if (completed) Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", "sent"));
                    else LogLostLease(d);
                    break;
                case PushSendOutcome.Retry:
                    await RetryOrDeadLetterAsync(entry, attempt, activity, result.Error!, result.ErrorType!).ConfigureAwait(false);
                    break;
                default:
                    await DeadLetterAsync(entry, activity, result.Error!, result.ErrorType!).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the lease runs out and another pass (or instance) sends it again.
            A2ATelemetry.RecordError(activity, new OperationCanceledException(), callerCanceled: true);
        }
        catch (Exception ex)
        {
            // Anything else (a failing store or protector) counts as a failed attempt, so a poison entry ends as a dead letter.
            _logger.LogError(ex, "Push delivery {Sequence} of task {TaskId} to webhook {ConfigId} failed", d.Sequence, d.Task.TaskId, d.ConfigId);
            A2ATelemetry.RecordError(activity, ex);
            try
            {
                await RetryOrDeadLetterAsync(entry, attempt, activity, "Internal error: " + ex.GetType().Name, ex.GetType().FullName ?? ex.GetType().Name).ConfigureAwait(false);
            }
            catch (Exception recordFailed)
            {
                // The outbox itself failed: the entry stays leased and is retried when the lease ends.
                _logger.LogError(recordFailed, "Could not record the failed push delivery {Sequence} of task {TaskId}", d.Sequence, d.Task.TaskId);
            }
        }
    }

    /// <summary>A retryable failure: reschedules with backoff, or dead-letters after <see cref="PushDeliveryOptions.MaxAttempts"/>.</summary>
    private async Task RetryOrDeadLetterAsync(OutboxEntry entry, int attempt, Activity? activity, string error, string errorType)
    {
        var d = entry.Delivery;
        if (attempt >= _options.MaxAttempts)
        {
            _logger.LogError("Giving up on webhook {ConfigId} for task {TaskId} after {Attempts} attempts", d.ConfigId, d.Task.TaskId, attempt);
            await DeadLetterAsync(entry, activity, error, "GaveUp").ConfigureAwait(false);
            return;
        }
        Fail(activity, errorType);
        activity?.AddEvent(new ActivityEvent(A2ATelemetry.Events.Retry));
        if (await _outbox.RescheduleAsync(entry, attempt, _time.GetUtcNow() + Backoff(attempt), error, CancellationToken.None).ConfigureAwait(false))
            Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", "retried"));
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
        Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", "dead_lettered"));
        DeadLetters.Add(1);
    }

    /// <summary>A result write found the entry gone or leased by someone else (this send outlived its lease).</summary>
    private void LogLostLease(PushDelivery d) =>
        _logger.LogDebug("Push delivery {Sequence} of task {TaskId} to webhook {ConfigId} lost its lease; result not recorded", d.Sequence, d.Task.TaskId, d.ConfigId);

    /// <summary>The wait after <paramref name="attempt"/> failed attempts: InitialBackoff · 2^(attempt-1), ±20 %, at most MaxBackoff.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var baseTicks = _options.InitialBackoff.Ticks * Math.Pow(2, Math.Min(attempt - 1, 62));
        var jittered = baseTicks * (0.8 + (0.4 * Random.Shared.NextDouble()));
        return TimeSpan.FromTicks((long)Math.Min(jittered, _options.MaxBackoff.Ticks));
    }

    /// <summary>Returns a copy of the stored config with its secrets in plaintext. Used only at delivery time.</summary>
    private TaskPushNotificationConfig Unprotect(TaskPushNotificationConfig stored)
    {
        var config = A2AJson.Clone(stored);
        if (!string.IsNullOrEmpty(config.Token)) config.Token = _protector.Unprotect(config.Token);
        if (config.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = _protector.Unprotect(auth.Credentials);
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
