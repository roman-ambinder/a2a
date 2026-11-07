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
/// Sends the push deliveries of the <see cref="PushOutbox"/> (spec §4.3.3, §13.2). Each pass leases due deliveries, reads
/// each event from the <see cref="EventStore"/> by its sequence, sends it to its push config and records the result:
/// sent (removed), failed and retryable (rescheduled with exponential backoff and jitter, <see cref="PushDeliveryOptions"/>)
/// or failed for good (dead-lettered: a URL refused by the SSRF policy, a 4xx other than 408/429, an event no longer
/// retained, or the last attempt). Delivery is at least once and in order per task and push config; several instances can
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
    private readonly EventStore _events;
    private readonly PushConfigStore _configs;
    private readonly IPushSecretProtector _protector;
    private readonly PushDeliveryOptions _options;
    private readonly PushWebhookSender _sender;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>Created by DI. The optional <paramref name="time"/> is the clock for due times and polling.</summary>
    public PushDeliveryService(PushOutbox outbox, EventStore events, PushConfigStore configs, IPushSecretProtector protector,
        IHttpClientFactory httpClientFactory, IOptions<A2AServerOptions> options, ILogger<PushDeliveryService>? logger = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _configs = configs ?? throw new ArgumentNullException(nameof(configs));
        _protector = protector ?? throw new ArgumentNullException(nameof(protector));
        _options = options.Value.PushDelivery;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _sender = new PushWebhookSender(httpClientFactory, options.Value.PushNotifications, _logger);
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Activity.Current = null; // each delivery names its parent explicitly
        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait first: at startup the stores may still be creating their schema.
            await WaitForWorkAsync(stoppingToken).ConfigureAwait(false);
            try
            {
                while (await RunOnceAsync(stoppingToken).ConfigureAwait(false) >= Math.Max(1, _options.BatchSize))
                {
                    // A full batch: more may be due.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Push outbox poll failed");
            }
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

    /// <summary>One pass: leases due deliveries and sends them in parallel. Returns how many were leased.</summary>
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
                await _outbox.CompleteAsync(d, CancellationToken.None).ConfigureAwait(false);
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
                await DeadLetterAsync(d, activity, "Cannot unprotect the config secrets", "SecretUnavailable").ConfigureAwait(false);
                return;
            }

            if (await ReadEventAsync(d, ct).ConfigureAwait(false) is not { } evt)
            {
                _logger.LogError("Event {Sequence} of task {TaskId} is no longer retained; cannot deliver it to webhook {ConfigId}", d.Sequence, d.Task.TaskId, d.ConfigId);
                await DeadLetterAsync(d, activity, "The event is no longer retained", "EventNotRetained").ConfigureAwait(false);
                return;
            }

            // Without a listener there is no span: pass on the producer's context, so the receiver still joins the trace.
            var traceContext = activity?.Context ?? parent;
            var result = await _sender.SendAsync(config, d, A2AJson.SerializeToUtf8Bytes(evt), activity, traceContext, ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case PushSendOutcome.Sent:
                    await _outbox.CompleteAsync(d, CancellationToken.None).ConfigureAwait(false);
                    Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", "sent"));
                    break;
                case PushSendOutcome.Retry when attempt < _options.MaxAttempts:
                    Fail(activity, result.ErrorType!);
                    activity?.AddEvent(new ActivityEvent(A2ATelemetry.Events.Retry));
                    await _outbox.RescheduleAsync(d, attempt, _time.GetUtcNow() + Backoff(attempt), result.Error!, CancellationToken.None).ConfigureAwait(false);
                    Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", "retried"));
                    break;
                case PushSendOutcome.Retry:
                    _logger.LogError("Giving up on webhook {ConfigId} for task {TaskId} after {Attempts} attempts", d.ConfigId, d.Task.TaskId, attempt);
                    await DeadLetterAsync(d, activity, result.Error!, "GaveUp").ConfigureAwait(false);
                    break;
                default:
                    await DeadLetterAsync(d, activity, result.Error!, result.ErrorType!).ConfigureAwait(false);
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
            // A store failed: leave the entry leased; it is retried when the lease ends.
            _logger.LogError(ex, "Push delivery {Sequence} of task {TaskId} to webhook {ConfigId} failed", d.Sequence, d.Task.TaskId, d.ConfigId);
            A2ATelemetry.RecordError(activity, ex);
        }
    }

    private async Task DeadLetterAsync(PushDelivery d, Activity? activity, string error, string errorType)
    {
        Fail(activity, errorType);
        await _outbox.DeadLetterAsync(d, error, CancellationToken.None).ConfigureAwait(false);
        Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", "dead_lettered"));
        DeadLetters.Add(1);
    }

    /// <summary>The wait after <paramref name="attempt"/> failed attempts: InitialBackoff · 2^(attempt-1), ±20 %, at most MaxBackoff.</summary>
    private TimeSpan Backoff(int attempt)
    {
        var baseTicks = _options.InitialBackoff.Ticks * Math.Pow(2, Math.Min(attempt - 1, 62));
        var jittered = baseTicks * (0.8 + (0.4 * Random.Shared.NextDouble()));
        return TimeSpan.FromTicks((long)Math.Min(jittered, _options.MaxBackoff.Ticks));
    }

    private async Task<StreamResponse?> ReadEventAsync(PushDelivery d, CancellationToken ct)
    {
        await foreach (var e in _events.ReadAfterAsync(d.Task, d.Sequence - 1, ct).ConfigureAwait(false))
            return e.Sequence == d.Sequence ? e.Event : null;
        return null;
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
