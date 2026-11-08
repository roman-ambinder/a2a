using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using A2A.Security.Http;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Server.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Push;

/// <summary>Settings of push notification delivery: webhook URL policy (SSRF guard), timeouts and retries.</summary>
public sealed class PushNotificationOptions
{
    /// <summary>Allow http:// webhook URLs. Default false (spec §13.2: SHOULD use HTTPS).</summary>
    public bool AllowInsecureUrls { get; set; }

    /// <summary>
    /// Allow webhooks that resolve to loopback, private, link-local, CGNAT, or unique-local addresses. Default false (SSRF guard).
    /// The check runs at connect time against the resolved IP, which also blocks DNS rebinding.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>If non-empty, only these hosts may be used as webhook targets.</summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Networks that bypass the private-range block (for example an internal webhook range). Empty by default.</summary>
    public IList<(IPAddress Network, int PrefixLength)> AllowedNetworks { get; } = new List<(IPAddress Network, int PrefixLength)>();

    /// <summary>
    /// Proxy for webhook delivery. When null (default) the system proxy is ignored. Requests sent through a proxy rely on the proxy
    /// for egress policy (including DNS rebinding at the proxy); requests the proxy bypasses get the normal IP check.
    /// </summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>Timeout of one delivery attempt. Default 15 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Delivery attempts per notification, including the first. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait before the first retry. Default 1 second; it grows exponentially.</summary>
    public TimeSpan InitialBackoff { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a task's delivery worker waits for the next event before it stops. Default 30 seconds. The next event starts a
    /// new worker. <see cref="Timeout.InfiniteTimeSpan"/> keeps workers until the sender is disposed; zero stops a worker as soon
    /// as its queue is empty. A task without push configs never keeps an idle worker.
    /// </summary>
    public TimeSpan WorkerIdleTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Validates webhook URLs against the SSRF policy.</summary>
public static class PushUrlValidator
{
    /// <summary>Static checks done when a config is registered. Throws InvalidParams.</summary>
    public static void Validate(string url, PushNotificationOptions options)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw A2AException.InvalidParams("Push notification url must be absolute");
        if (uri.Scheme != Uri.UriSchemeHttps && !(options.AllowInsecureUrls && uri.Scheme == Uri.UriSchemeHttp))
            throw A2AException.InvalidParams("Push notification url must use https");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw A2AException.InvalidParams("Push notification url must not contain credentials");
        var host = PrivateAddressGuard.TrimHost(uri.Host);
        if (options.AllowedHosts.Count > 0 && !options.AllowedHosts.Contains(host) && !options.AllowedHosts.Contains(uri.Host))
            throw A2AException.InvalidParams("Push notification host is not allowed");
        if (!options.AllowPrivateNetworks)
        {
            var ip = PrivateAddressGuard.TryGetLiteral(uri);
            if (ip is not null)
            {
                if (PrivateAddressGuard.IsBlocked(ip, options.AllowedNetworks))
                    throw A2AException.InvalidParams("Push notification url must not target a private network address");
            }
            else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
                throw A2AException.InvalidParams("Push notification url must not target loopback");
            else if (PrivateAddressGuard.IsAmbiguousNumericHost(host))
                throw A2AException.InvalidParams("Push notification url must not target a private network address");
        }
    }

    internal static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The one message for every DNS-based rejection, so a caller cannot probe internal DNS by comparing error texts.</summary>
    public const string UrlNotAllowed = "Push notification URL is not allowed.";

    /// <summary>
    /// Registration check: <see cref="Validate"/> plus DNS resolution and the IP check (spec 7.6). Every DNS-based failure
    /// (private, unresolvable) throws the same <see cref="UrlNotAllowed"/> InvalidParams error.
    /// </summary>
    public static async ValueTask ValidateAsync(string url, PushNotificationOptions options, CancellationToken cancellationToken = default)
    {
        Validate(url, options);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(options.Timeout < DnsTimeout ? options.Timeout : DnsTimeout);
        try
        {
            await ValidateForDeliveryAsync(url, options, cts.Token).AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw A2AException.InvalidParams(UrlNotAllowed); // DNS too slow
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Net.Sockets.SocketException or ArgumentException)
        {
            throw A2AException.InvalidParams(UrlNotAllowed);
        }
    }

    /// <summary>
    /// Full check run before each delivery attempt: the static rules of <see cref="Validate"/> plus DNS resolution and the IP check.
    /// Throws <see cref="A2AException"/> for policy violations (permanent) and <see cref="HttpRequestException"/> when resolution fails or finds a private address.
    /// </summary>
    internal static async ValueTask ValidateForDeliveryAsync(string url, PushNotificationOptions options, CancellationToken ct)
    {
        Validate(url, options);
        var uri = new Uri(url);
        await PrivateAddressGuard.EnsureHostAllowedAsync(uri.Host, options.AllowPrivateNetworks, options.AllowedNetworks, "Webhook", ct).ConfigureAwait(false);
    }

    /// <summary>True for addresses that must not be reached from the agent by default.</summary>
    public static bool IsPrivate(IPAddress ip) => PrivateAddressGuard.IsPrivate(ip);

    /// <summary>Primary handler for the push HttpClient: enforces the IP policy on every connection and disables redirects.</summary>
    public static SocketsHttpHandler CreateHandler(PushNotificationOptions options) =>
        PrivateAddressGuard.CreateHandler(options.AllowPrivateNetworks, "Webhook", options.AllowedNetworks.ToArray(), options.Proxy);
}

/// <summary>Sends task events to registered webhooks.</summary>
public interface IPushNotificationSender
{
    /// <summary>Queues delivery of a stored task event to the task's push configs.</summary>
    /// <remarks>
    /// Called while the execution engine holds the task's lock, once per event in sequence order. Implementations must return quickly:
    /// they must not block, and must not call back into <see cref="A2ARequestHandler"/> for the same task (that would deadlock).
    /// The event carries its encoded bytes in <see cref="SequencedEvent.Json"/> (the same bytes the event store keeps); send
    /// them as they are, and encode with <see cref="EventEncoder.EnsureEncoded"/> only when they are empty.
    /// </remarks>
    void Enqueue(TaskKey task, SequencedEvent evt);
}

/// <summary>
/// Delivers notifications per task in order, at least once, with retries and exponential backoff (spec §4.3.3, §13.2).
/// Adds <c>X-A2A-Notification-Token</c> (also sent as a bearer <c>Authorization</c> unless the config has its own), an <c>X-A2A-Delivery-Id</c> that stays the same across retries, and an <c>X-A2A-Notification-Sequence</c> number (the event store sequence) for deduplication and ordering.
/// </summary>
/// <remarks>
/// Each task with queued events has one delivery worker that reads a bounded per-task channel
/// (capacity <see cref="StreamingOptions.ChannelCapacity"/>). The worker takes the waiting events as a batch, lists the task's
/// configs once per batch, and lists them again inside the batch only after <see cref="ConfigChanged"/>. It stops after
/// <see cref="PushNotificationOptions.WorkerIdleTimeout"/> without events, or as soon as its queue is empty when the task has no configs.
/// </remarks>
public sealed class HttpPushNotificationSender : IPushNotificationSender, IPushConfigChangeListener, IDisposable
{
    /// <summary>
    /// Service key under which <c>AddA2AServer</c> registers the <see cref="HttpMessageInvoker"/> used for webhook delivery (SSRF-guarded by default).
    /// Register your own keyed singleton under this key to replace it deliberately; an unkeyed <see cref="HttpMessageInvoker"/> is never used.
    /// </summary>
    public const string InvokerServiceKey = "A2A.PushNotifications";

    private readonly HttpMessageInvoker _http;
    private readonly PushConfigStore _store;
    private readonly IPushSecretProtector _protector;
    private readonly PushNotificationOptions _options;
    private readonly StreamingOptions _streaming;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<TaskKey, Worker> _workers = new();
    private readonly Func<TaskKey, Worker> _newWorker;
    private readonly ConcurrentDictionary<Task, byte> _runs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _stopping;

    // Events enqueued and not yet attempted; WhenIdleAsync waits for zero. Only reaching zero takes _idleGate.
    private readonly object _idleGate = new();
    private TaskCompletionSource _idle = NewIdleSource(completed: true);
    private int _pending;

    /// <summary>Creates the sender. In an ASP.NET Core app the DI container does this.</summary>
    /// <param name="http">Sends the webhook requests.</param>
    /// <param name="store">Source of the push configs of each task.</param>
    /// <param name="protector">Unprotects stored secrets at delivery time.</param>
    /// <param name="options">Server options; <see cref="A2AServerOptions.PushNotifications"/> and <see cref="StreamingOptions.ChannelCapacity"/> are used.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="time">Clock for the worker idle timeout. Default <see cref="TimeProvider.System"/>.</param>
    public HttpPushNotificationSender(HttpMessageInvoker http, PushConfigStore store, IPushSecretProtector protector,
        A2AServerOptions options, ILogger<HttpPushNotificationSender>? logger = null, TimeProvider? time = null)
    {
        _http = http;
        _store = store;
        _protector = protector;
        _options = options.PushNotifications;
        _streaming = options.Streaming;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _stopping = _shutdown.Token;
        _newWorker = key => new Worker(key, Math.Max(1, _streaming.ChannelCapacity));
    }

    /// <summary>
    /// One task's delivery queue. <see cref="Sync"/> guards <see cref="Overflow"/>, <see cref="Started"/>, <see cref="Exited"/>
    /// and every write to <see cref="Channel"/>. Invariant (under <see cref="Sync"/>): <see cref="Overflow"/> is empty unless the
    /// channel is full, or the worker has just read from it and is about to refill; so channel items are always older than overflow items.
    /// </summary>
    private sealed class Worker(TaskKey key, int capacity)
    {
        public readonly TaskKey Key = key;
        public readonly int Capacity = capacity;
        public readonly Channel<SequencedEvent> Channel = System.Threading.Channels.Channel.CreateBounded<SequencedEvent>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
        public readonly object Sync = new();
        public readonly Queue<SequencedEvent> Overflow = new();
        public bool Started;
        public bool Exited;
        public int ConfigVersion;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never waits: the event goes into the task's bounded channel with <c>TryWrite</c>; when the channel is full it goes into
    /// the task's overflow queue, which the worker moves into the channel as space frees up. Per-task order is kept and nothing is dropped.
    /// </remarks>
    public void Enqueue(TaskKey task, SequencedEvent evt)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _pending);
        while (true)
        {
            var worker = _workers.GetOrAdd(task, _newWorker);
            bool start;
            lock (worker.Sync)
            {
                if (worker.Exited) continue; // it left the map as it exited; the next GetOrAdd makes a fresh one
                if (worker.Overflow.Count > 0 || !worker.Channel.Writer.TryWrite(evt)) worker.Overflow.Enqueue(evt);
                start = !worker.Started;
                worker.Started = true;
            }
            if (start) StartWorker(worker);
            return;
        }
    }

    /// <summary>
    /// Tells the task's running worker that its configs changed, so it lists them again before its next event.
    /// <see cref="PushConfigService"/> calls this after a config is created, deleted or saved inline. A task without a running
    /// worker needs nothing: the next worker lists the configs on its first batch.
    /// </summary>
    public void ConfigChanged(TaskKey key)
    {
        if (_workers.TryGetValue(key, out var worker)) Interlocked.Increment(ref worker.ConfigVersion);
    }

    /// <summary>Number of live per-task workers. For tests.</summary>
    internal int ActiveWorkers => _workers.Count;

    /// <summary>Completes when all queued notifications have been attempted. For tests and graceful shutdown.</summary>
    /// <remarks>After <see cref="Dispose"/> it completes when every worker has stopped.</remarks>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            Task idle;
            lock (_idleGate)
            {
                if (Volatile.Read(ref _pending) <= 0) break;
                if (_idle.Task.IsCompleted) _idle = NewIdleSource(completed: false);
                idle = _idle.Task;
            }
            await idle.ConfigureAwait(false);
        }
        if (Volatile.Read(ref _disposed) != 0)
            while (!_runs.IsEmpty) await Task.WhenAll(_runs.Keys).ConfigureAwait(false);
    }

    private static TaskCompletionSource NewIdleSource(bool completed)
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (completed) source.SetResult();
        return source;
    }

    private void Attempted(int count)
    {
        if (count == 0 || Interlocked.Add(ref _pending, -count) > 0) return;
        lock (_idleGate)
        {
            if (Volatile.Read(ref _pending) <= 0) _idle.TrySetResult();
        }
    }

    private void StartWorker(Worker worker)
    {
        var run = Task.Run(() => RunAsync(worker));
        _runs[run] = 0;
        run.ContinueWith(static (t, runs) => ((ConcurrentDictionary<Task, byte>)runs!).TryRemove(t, out _), _runs,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunAsync(Worker worker)
    {
        var reader = worker.Channel.Reader;
        // Sized by the first batch, not up front: a task without configs gets a new worker for nearly every event.
        List<SequencedEvent>? batch = null;
        var linger = true;
        try
        {
            while (true)
            {
                if (!reader.TryPeek(out _))
                {
                    // Linger for the next event only while the task has configs.
                    var arrived = linger && await WaitForEventAsync(reader).ConfigureAwait(false);
                    if (!arrived && TryExit(worker)) return;
                    continue; // an event arrived (or arrived as we tried to exit)
                }

                if (batch is null) batch = new List<SequencedEvent>(Math.Clamp(reader.Count, 1, worker.Capacity));
                else batch.Clear();
                while (batch.Count < worker.Capacity && reader.TryRead(out var evt)) batch.Add(evt);
                Refill(worker);
                linger = await DeliverBatchAsync(worker, batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Disposed: stop.
        }
        catch (Exception ex)
        {
            ServerLog.PushWorkerFailed(_logger, ex, worker.Key.TaskId);
        }
        Abandon(worker);
    }

    /// <summary>Waits for the next event until the idle timeout. False when the timeout passed first.</summary>
    private async ValueTask<bool> WaitForEventAsync(ChannelReader<SequencedEvent> reader)
    {
        var timeout = _options.WorkerIdleTimeout;
        if (timeout <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) return false;
        using var idle = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(idle.Token, _stopping);
        try
        {
            return await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Moves overflow events into the channel's free slots, oldest first.</summary>
    private static void Refill(Worker worker)
    {
        lock (worker.Sync)
        {
            while (worker.Overflow.Count > 0 && worker.Channel.Writer.TryWrite(worker.Overflow.Peek())) worker.Overflow.Dequeue();
        }
    }

    /// <summary>
    /// Leaves the map unless an event is waiting. Under <see cref="Worker.Sync"/>, so an <see cref="Enqueue"/> either wrote before
    /// (this sees it and keeps running) or sees <see cref="Worker.Exited"/> after (and starts a new worker). No event is lost.
    /// </summary>
    private bool TryExit(Worker worker)
    {
        lock (worker.Sync)
        {
            if (worker.Overflow.Count > 0 || worker.Channel.Reader.TryPeek(out _)) return false;
            worker.Exited = true;
            _workers.TryRemove(new KeyValuePair<TaskKey, Worker>(worker.Key, worker));
            return true;
        }
    }

    /// <summary>After shutdown or a failure: leaves the map and counts the events it will not attempt, so <see cref="WhenIdleAsync"/> does not hang.</summary>
    private void Abandon(Worker worker)
    {
        int dropped;
        lock (worker.Sync)
        {
            if (worker.Exited) return;
            worker.Exited = true;
            _workers.TryRemove(new KeyValuePair<TaskKey, Worker>(worker.Key, worker));
            dropped = worker.Overflow.Count;
            worker.Overflow.Clear();
            while (worker.Channel.Reader.TryRead(out _)) dropped++;
        }
        Attempted(dropped);
    }

    /// <summary>Delivers a batch in order to every config of the task. Returns true when the task has configs (the worker then lingers).</summary>
    private async Task<bool> DeliverBatchAsync(Worker worker, List<SequencedEvent> batch)
    {
        List<TaskPushNotificationConfig>? configs = null;
        var listedVersion = 0;
        var i = 0;
        try
        {
            for (; i < batch.Count; i++)
            {
                var item = batch[i];
                try
                {
                    // Listed once per batch; listed again only when ConfigChanged ran since. The version is read before listing,
                    // so a change made during the listing triggers one more.
                    var version = Volatile.Read(ref worker.ConfigVersion);
                    if (configs is null || version != listedVersion)
                    {
                        configs = await ListConfigsAsync(worker.Key).ConfigureAwait(false);
                        listedVersion = version;
                    }
                    if (configs.Count == 0) continue;
                    // The event's stored bytes are the body for every config; encoded here only for an event made without them.
                    var body = EventEncoder.EnsureEncoded(item).Json;
                    foreach (var config in configs)
                        await DeliverAsync(config, item.Sequence, body).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    ServerLog.PushProcessingFailed(_logger, ex, worker.Key.TaskId);
                }
                finally
                {
                    Attempted(1);
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            Attempted(batch.Count - i - 1);
            throw;
        }
        return configs is { Count: > 0 };
    }

    /// <summary>The task's configs with secrets unprotected (copies; the store's instances are not changed). Configs whose secrets cannot be unprotected are logged and skipped.</summary>
    private async Task<List<TaskPushNotificationConfig>> ListConfigsAsync(TaskKey task)
    {
        var records = await _store.ListAsync(task, _stopping).ConfigureAwait(false);
        var configs = new List<TaskPushNotificationConfig>(records.Count);
        foreach (var record in records)
        {
            try
            {
                configs.Add(Unprotect(record.Config));
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                ServerLog.PushUnprotectFailed(_logger, ex, record.Config.Id, task.TaskId);
            }
        }
        return configs;
    }

    /// <summary>Returns a copy of the stored config with its secrets in plaintext; the stored config (owned by the store) is not changed. Used only at delivery time.</summary>
    private TaskPushNotificationConfig Unprotect(TaskPushNotificationConfig stored)
    {
        var config = stored.DeepCopy();
        if (!string.IsNullOrEmpty(config.Token)) config.Token = _protector.Unprotect(config.Token);
        if (config.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = _protector.Unprotect(auth.Credentials);
        return config;
    }

    /// <summary>Delivers one event to one webhook, with retries, and counts the final result (<c>a2a.server.push.deliveries</c>).</summary>
    private async Task DeliverAsync(TaskPushNotificationConfig config, long seq, ReadOnlyMemory<byte> body)
    {
        bool delivered;
        try
        {
            delivered = await DeliverCoreAsync(config, seq, body).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            throw; // stopping: no final result
        }
        catch
        {
            CountDelivery(false);
            throw;
        }
        CountDelivery(delivered);
    }

    private static void CountDelivery(bool delivered)
    {
        if (A2AServerTelemetry.PushDeliveries.Enabled)
            A2AServerTelemetry.PushDeliveries.Add(1, new KeyValuePair<string, object?>(A2AServerTelemetry.OutcomeTag, delivered ? "delivered" : "failed"));
    }

    /// <summary>The delivery with retries. True when the webhook accepted the event; false when it was skipped, refused, or every attempt failed.</summary>
    private async Task<bool> DeliverCoreAsync(TaskPushNotificationConfig config, long seq, ReadOnlyMemory<byte> body)
    {
        var deliveryId = Guid.NewGuid().ToString("N");
        var delay = _options.InitialBackoff;

        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxAttempts); attempt++)
        {
            try
            {
                // Re-validated on every attempt (not only at create): the host's DNS answer can change, and the allowlist may have.
                try
                {
                    using var vcts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
                    vcts.CancelAfter(_options.Timeout);
                    await PushUrlValidator.ValidateForDeliveryAsync(config.Url, _options, vcts.Token).ConfigureAwait(false);
                }
                catch (A2AException ex)
                {
                    ServerLog.PushWebhookSkipped(_logger, config.Id, config.TaskId, ex.Message);
                    return false;
                }
                catch (BlockedAddressException ex)
                {
                    ServerLog.PushWebhookSkipped(_logger, config.Id, config.TaskId, ex.Message);
                    return false;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, config.Url) { Content = new ReadOnlyMemoryContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(A2AProtocol.MediaType);
                if (config.Authentication is { Scheme.Length: > 0 } auth)
                    request.Headers.TryAddWithoutValidation("Authorization", string.IsNullOrEmpty(auth.Credentials) ? auth.Scheme : $"{auth.Scheme} {auth.Credentials}");
                else if (!string.IsNullOrEmpty(config.Token))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.Token}");
                if (!string.IsNullOrEmpty(config.Token)) request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.NotificationToken, config.Token);
                request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.DeliveryId, deliveryId);
                request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.NotificationSequence, seq.ToString(System.Globalization.CultureInfo.InvariantCulture));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
                cts.CancelAfter(_options.Timeout);
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;

                var status = (int)response.StatusCode;
                var retryable = status is 408 or 429 || status >= 500;
                ServerLog.PushWebhookStatus(_logger, config.Id, config.TaskId, status, attempt);
                if (!retryable) return false;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex is BlockedAddressException || ex.InnerException is BlockedAddressException)
            {
                ServerLog.PushWebhookBlocked(_logger, config.Id, config.TaskId);
                return false;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                ServerLog.PushWebhookAttemptFailed(_logger, ex, config.Id, config.TaskId, attempt);
            }

            if (attempt < _options.MaxAttempts)
            {
                await Task.Delay(delay, _stopping).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * 2);
            }
        }
        ServerLog.PushWebhookGaveUp(_logger, config.Id, config.TaskId, _options.MaxAttempts);
        return false;
    }

    private int _disposed;

    /// <summary>Stops delivery and releases resources. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
