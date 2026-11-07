using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using A2A.Json;
using A2A.Security.Http;
using A2A.Server.Storage;
using A2A.Server.Streaming;
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
    public static async ValueTask ValidateAsync(string url, PushNotificationOptions options, CancellationToken ct = default)
    {
        Validate(url, options);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(options.Timeout < DnsTimeout ? options.Timeout : DnsTimeout);
        try
        {
            await ValidateForDeliveryAsync(url, options, cts.Token).AsTask().WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
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
    /// Called while the handler holds the task's lock, once per event in sequence order. Implementations must return quickly:
    /// they must not block, and must not call back into <see cref="A2ARequestHandler"/> for the same task (that would deadlock).
    /// </remarks>
    void Enqueue(TaskKey task, SequencedEvent evt);
}

/// <summary>
/// Delivers notifications per task in order, at least once, with retries and exponential backoff (spec §4.3.3, §13.2).
/// Adds <c>X-A2A-Notification-Token</c> (also sent as a bearer <c>Authorization</c> unless the config has its own), an <c>X-A2A-Delivery-Id</c> that stays the same across retries, and an <c>X-A2A-Notification-Sequence</c> number (the event store sequence) for deduplication and ordering.
/// </summary>
public sealed class HttpPushNotificationSender : IPushNotificationSender, IDisposable
{
    private readonly HttpMessageInvoker _http;
    private readonly PushConfigStore _store;
    private readonly IPushSecretProtector _protector;
    private readonly PushNotificationOptions _options;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly Dictionary<TaskKey, Queue<SequencedEvent>> _queues = new();
    private readonly ConcurrentDictionary<Task, byte> _workers = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Creates the sender. In an ASP.NET Core app the DI container does this.</summary>
    public HttpPushNotificationSender(HttpMessageInvoker http, PushConfigStore store, IPushSecretProtector protector,
        A2AServerOptions options, ILogger<HttpPushNotificationSender>? logger = null)
    {
        _http = http;
        _store = store;
        _protector = protector;
        _options = options.PushNotifications;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public void Enqueue(TaskKey task, SequencedEvent evt)
    {
        lock (_gate)
        {
            if (_queues.TryGetValue(task, out var q))
            {
                q.Enqueue(evt);
                return;
            }
            q = new Queue<SequencedEvent>();
            q.Enqueue(evt);
            _queues[task] = q;
        }
        var worker = Task.Run(() => DrainAsync(task));
        _workers[worker] = 0;
        worker.ContinueWith(t => _workers.TryRemove(t, out _), TaskScheduler.Default);
    }

    /// <summary>Completes when all queued notifications have been attempted. For tests and graceful shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        while (!_workers.IsEmpty) await Task.WhenAll(_workers.Keys).ConfigureAwait(false);
    }

    private async Task DrainAsync(TaskKey task)
    {
        while (true)
        {
            SequencedEvent item;
            lock (_gate)
            {
                var q = _queues[task];
                if (q.Count == 0)
                {
                    _queues.Remove(task);
                    return;
                }
                item = q.Dequeue();
            }

            try
            {
                var records = await _store.ListAsync(task, _shutdown.Token).ConfigureAwait(false);
                foreach (var record in records)
                {
                    TaskPushNotificationConfig config;
                    try
                    {
                        config = Unprotect(record.Config);
                    }
                    catch (System.Security.Cryptography.CryptographicException ex)
                    {
                        _logger.LogError(ex, "Cannot unprotect secrets of webhook {ConfigId} for task {TaskId}; skipping", record.Config.Id, task.TaskId);
                        continue;
                    }
                    await DeliverAsync(config, item.Sequence, item.Event).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Push notification processing failed for task {TaskId}", task.TaskId);
            }
        }
    }

    /// <summary>Returns a copy of the stored config with its secrets in plaintext. Used only at delivery time.</summary>
    private TaskPushNotificationConfig Unprotect(TaskPushNotificationConfig stored)
    {
        var config = A2AJson.Clone(stored);
        if (!string.IsNullOrEmpty(config.Token)) config.Token = _protector.Unprotect(config.Token);
        if (config.Authentication is { Credentials.Length: > 0 } auth) auth.Credentials = _protector.Unprotect(auth.Credentials);
        return config;
    }

    private async Task DeliverAsync(TaskPushNotificationConfig config, long seq, StreamResponse evt)
    {
        var deliveryId = Guid.NewGuid().ToString("N");
        var body = A2AJson.SerializeToUtf8Bytes(evt);
        var delay = _options.InitialBackoff;

        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxAttempts); attempt++)
        {
            try
            {
                // Re-validated on every attempt (not only at create): the host's DNS answer can change, and the allowlist may have.
                try
                {
                    using var vcts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    vcts.CancelAfter(_options.Timeout);
                    await PushUrlValidator.ValidateForDeliveryAsync(config.Url, _options, vcts.Token).ConfigureAwait(false);
                }
                catch (A2AException ex)
                {
                    _logger.LogWarning("Skipping webhook {ConfigId} for task {TaskId}: {Reason}", config.Id, config.TaskId, ex.Message);
                    return;
                }
                catch (BlockedAddressException ex)
                {
                    _logger.LogWarning("Skipping webhook {ConfigId} for task {TaskId}: {Reason}", config.Id, config.TaskId, ex.Message);
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, config.Url) { Content = new ByteArrayContent(body) };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue(A2AProtocol.MediaType);
                if (config.Authentication is { Scheme.Length: > 0 } auth)
                    request.Headers.TryAddWithoutValidation("Authorization", string.IsNullOrEmpty(auth.Credentials) ? auth.Scheme : $"{auth.Scheme} {auth.Credentials}");
                else if (!string.IsNullOrEmpty(config.Token))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.Token}");
                if (!string.IsNullOrEmpty(config.Token)) request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.NotificationToken, config.Token);
                request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.DeliveryId, deliveryId);
                request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.NotificationSequence, seq.ToString(System.Globalization.CultureInfo.InvariantCulture));

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                cts.CancelAfter(_options.Timeout);
                using var response = await _http.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;

                var status = (int)response.StatusCode;
                var retryable = status is 408 or 429 || status >= 500;
                _logger.LogWarning("Webhook {ConfigId} for task {TaskId} returned {Status} (attempt {Attempt})", config.Id, config.TaskId, status, attempt);
                if (!retryable) return;
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex is BlockedAddressException || ex.InnerException is BlockedAddressException)
            {
                _logger.LogWarning("Skipping webhook {ConfigId} for task {TaskId}: target address is not allowed", config.Id, config.TaskId);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                _logger.LogWarning(ex, "Webhook {ConfigId} for task {TaskId} failed (attempt {Attempt})", config.Id, config.TaskId, attempt);
            }

            if (attempt < _options.MaxAttempts)
            {
                await Task.Delay(delay, _shutdown.Token).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(delay.Ticks * 2);
            }
        }
        _logger.LogError("Giving up on webhook {ConfigId} for task {TaskId} after {Attempts} attempts", config.Id, config.TaskId, _options.MaxAttempts);
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
