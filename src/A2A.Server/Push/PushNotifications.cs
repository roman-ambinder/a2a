using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using A2A.Diagnostics;
using A2A.Push;
using A2A.Security.Http;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using A2A.Server.Telemetry;
using Microsoft.Extensions.Logging;

namespace A2A.Server.Push;

/// <summary>
///     Settings of push notification delivery: webhook URL policy (SSRF guard) and the timeout of one attempt
///     (retries: <see cref="PushDeliveryOptions" />).
/// </summary>
public sealed class PushNotificationOptions
{
    /// <summary>Allow http:// webhook URLs. Default false (spec §13.2: SHOULD use HTTPS).</summary>
    public bool AllowInsecureUrls { get; set; }

    /// <summary>
    ///     Allow webhooks that resolve to loopback, private, link-local, CGNAT, or unique-local addresses. Default false (SSRF
    ///     guard).
    ///     The check runs at connect time against the resolved IP, which also blocks DNS rebinding.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }

    /// <summary>If non-empty, only these hosts may be used as webhook targets.</summary>
    public ISet<string> AllowedHosts { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Networks that bypass the private-range block (for example an internal webhook range). Empty by default.</summary>
    public IList<(IPAddress Network, int PrefixLength)> AllowedNetworks { get; } =
        new List<(IPAddress Network, int PrefixLength)>();

    /// <summary>
    ///     Proxy for webhook delivery. When null (default) the system proxy is ignored. Requests sent through a proxy rely on
    ///     the proxy
    ///     for egress policy (including DNS rebinding at the proxy); requests the proxy bypasses get the normal IP check.
    /// </summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>
    ///     Timeout of one webhook POST, and of the DNS check before it. Retries are set in
    ///     <see cref="PushDeliveryOptions" />. Default 15 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     Send the current state to a webhook that is registered late. When a push config is created
    ///     (CreateTaskPushNotificationConfig) for a task that is terminal or waiting for input or authorization, one delivery
    ///     of
    ///     the task snapshot is queued for that config, so it does not wait for an event that will never come. The engine also
    ///     checks the task's webhooks again after an event that ends or interrupts the task, so a webhook registered while
    ///     that
    ///     event was committed is not missed. Default true; false keeps the old behavior (a webhook only sees later events).
    /// </summary>
    public bool CatchUpOnRegistration { get; set; } = true;
}

/// <summary>Validates webhook URLs against the SSRF policy.</summary>
public static class PushUrlValidator
{
    /// <summary>The one message for every DNS-based rejection, so a caller cannot probe internal DNS by comparing error texts.</summary>
    public const string UrlNotAllowed = "Push notification URL is not allowed.";

    internal static readonly TimeSpan DnsTimeout = TimeSpan.FromSeconds(5);

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
        if (options.AllowedHosts.Count > 0 && !options.AllowedHosts.Contains(host) &&
            !options.AllowedHosts.Contains(uri.Host))
            throw A2AException.InvalidParams("Push notification host is not allowed");
        if (!options.AllowPrivateNetworks)
        {
            var ip = PrivateAddressGuard.TryGetLiteral(uri);
            if (ip is not null)
            {
                if (PrivateAddressGuard.IsBlocked(ip, options.AllowedNetworks))
                    throw A2AException.InvalidParams("Push notification url must not target a private network address");
            }
            else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                     host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            {
                throw A2AException.InvalidParams("Push notification url must not target loopback");
            }
            else if (PrivateAddressGuard.IsAmbiguousNumericHost(host))
            {
                throw A2AException.InvalidParams("Push notification url must not target a private network address");
            }
        }
    }

    /// <summary>
    ///     Registration check: <see cref="Validate" /> plus DNS resolution and the IP check (spec 7.6). Every DNS-based
    ///     failure
    ///     (private, unresolvable) throws the same <see cref="UrlNotAllowed" /> InvalidParams error.
    /// </summary>
    public static async ValueTask ValidateAsync(string url, PushNotificationOptions options,
        CancellationToken cancellationToken = default)
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
        catch (Exception ex) when (ex is HttpRequestException or SocketException or ArgumentException)
        {
            throw A2AException.InvalidParams(UrlNotAllowed);
        }
    }

    /// <summary>
    ///     Full check run before each delivery attempt: the static rules of <see cref="Validate" /> plus DNS resolution and
    ///     the IP check.
    ///     Throws <see cref="A2AException" /> for policy violations (permanent) and <see cref="HttpRequestException" /> when
    ///     resolution fails or finds a private address.
    /// </summary>
    internal static async ValueTask ValidateForDeliveryAsync(string url, PushNotificationOptions options,
        CancellationToken cancellationToken)
    {
        Validate(url, options);
        var uri = new Uri(url);
        await PrivateAddressGuard.EnsureHostAllowedAsync(uri.Host, options.AllowPrivateNetworks,
            options.AllowedNetworks, "Webhook", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True for addresses that must not be reached from the agent by default.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        return PrivateAddressGuard.IsPrivate(ip);
    }

    /// <summary>Primary handler for the push HttpClient: enforces the IP policy on every connection and disables redirects.</summary>
    public static SocketsHttpHandler CreateHandler(PushNotificationOptions options)
    {
        return PrivateAddressGuard.CreateHandler(options.AllowPrivateNetworks, "Webhook",
            options.AllowedNetworks.ToArray(), options.Proxy);
    }
}

/// <summary>The result of one webhook attempt.</summary>
internal enum PushSendOutcome
{
    /// <summary>The receiver answered 2xx.</summary>
    Sent,

    /// <summary>A failure worth retrying: 408, 429, 5xx, a timeout or a connection error.</summary>
    Retry,

    /// <summary>A failure that will not change: the URL policy or the IP check refused the target, or another 4xx status.</summary>
    Permanent
}

/// <param name="Outcome">What happened.</param>
/// <param name="Error">For a failure, a short description without secrets or the URL (stored with the delivery).</param>
/// <param name="ErrorType">For a failure, the <c>error.type</c> span tag.</param>
/// <param name="StatusCode">The receiver's HTTP status, when it answered.</param>
/// <param name="RetryAfter">The wait the receiver asked for in its <c>Retry-After</c> header, if any.</param>
internal readonly record struct PushSendResult(PushSendOutcome Outcome, string? Error = null, string? ErrorType = null,
    int? StatusCode = null, TimeSpan? RetryAfter = null);

/// <summary>
///     Sends one webhook POST for one delivery (spec §4.3.3, §13.2), guarded by the SSRF policy: the URL and its resolved
///     addresses are checked again before every attempt (the DNS answer or the allowlist may have changed since the config
///     was
///     created), and the push HttpClient checks the IP again at connect time. Adds <c>X-A2A-Notification-Token</c> (also
///     sent as
///     a bearer <c>Authorization</c> unless the config has its own), the <c>X-A2A-Delivery-Id</c> of the delivery (the
///     same on
///     every attempt), the <c>X-A2A-Notification-Sequence</c> (the event store sequence) and the W3C <c>traceparent</c>.
///     Retries and their timing belong to <see cref="PushDeliveryService" />.
/// </summary>
internal sealed class PushWebhookSender(
    HttpMessageInvoker http,
    PushNotificationOptions options,
    ILogger logger,
    TimeProvider? time = null,
    ILogger? securityLogger = null)
{
    /// <summary>A refused webhook is a security event. It never carries the URL.</summary>
    private void Refused(PushDelivery delivery)
    {
        SecurityEventLog.Record(securityLogger, A2ATelemetry.SecurityEventNames.WebhookRefused, "PushDelivery",
            delivery.Task.Tenant, delivery.Task.Owner, delivery.Task.TaskId);
    }

    public async Task<PushSendResult> SendAsync(TaskPushNotificationConfig config, PushDelivery delivery, int attempt,
        ReadOnlyMemory<byte> body,
        Activity? activity, ActivityContext traceContext, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                using var vcts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                vcts.CancelAfter(options.Timeout);
                await PushUrlValidator.ValidateForDeliveryAsync(config.Url, options, vcts.Token).ConfigureAwait(false);
            }
            catch (A2AException ex)
            {
                ServerLog.PushWebhookSkipped(logger, config.Id, delivery.Task.TaskId, ex.Message);
                Refused(delivery);
                return new PushSendResult(PushSendOutcome.Permanent, "URL not allowed: " + ex.Message, "UrlNotAllowed");
            }

            // A2A 1.1 (§4.3.1 protocolVersion): a 1.1 config gets the event with its generation, which equals the delivery's
            // sequence (the stored bytes are the 1.0 shape). Done before the signature, which covers the bytes sent.
            var v11 = A2AProtocol.IsVersion11OrLater(config.ProtocolVersion);
            if (v11 && delivery.Sequence > 0) body = EventEncoder.WithGeneration(body.ToArray(), delivery.Sequence);
            using var request = new HttpRequestMessage(HttpMethod.Post, config.Url)
                { Content = new ReadOnlyMemoryContent(body) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(A2AProtocol.MediaType);
            // The body is an A2A StreamResponse of the config's version; say so, as on every other A2A request (a2a-js #647).
            request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.Version,
                v11 ? A2AProtocol.Version11 : A2AProtocol.Version);
            // The webhook-signature scheme carries the signing secret, never an Authorization header: the bearer token (if any) applies.
            var signing =
                config.Authentication is { } a && string.Equals(a.Scheme, WebhookSignature.Scheme,
                    StringComparison.OrdinalIgnoreCase)
                    ? config.Authentication
                    : null;
            if (signing is not null)
            {
                if (!WebhookSignature.TryParseSecret(signing.Credentials, out var secret))
                {
                    ServerLog.PushWebhookSkipped(logger, config.Id, delivery.Task.TaskId, "no usable signing secret");
                    return new PushSendResult(PushSendOutcome.Permanent, "Invalid webhook signing secret",
                        "InvalidSigningSecret");
                }

                var timestamp =
                    (time ?? TimeProvider.System).GetUtcNow()
                    .ToUnixTimeSeconds(); // fresh on every attempt; the id is stable
                request.Headers.TryAddWithoutValidation(WebhookSignature.IdHeader, delivery.DeliveryId);
                request.Headers.TryAddWithoutValidation(WebhookSignature.TimestampHeader,
                    timestamp.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation(WebhookSignature.SignatureHeader,
                    WebhookSignature.Sign(secret, delivery.DeliveryId, timestamp, body.Span));
            }

            if (signing is null && config.Authentication is { Scheme.Length: > 0 } auth)
                request.Headers.TryAddWithoutValidation("Authorization",
                    string.IsNullOrEmpty(auth.Credentials) ? auth.Scheme : $"{auth.Scheme} {auth.Credentials}");
            else if (!string.IsNullOrEmpty(config.Token))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {config.Token}");
            if (!string.IsNullOrEmpty(config.Token))
                request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.NotificationToken, config.Token);
            request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.DeliveryId, delivery.DeliveryId);
            request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.NotificationSequence,
                delivery.Sequence.ToString(CultureInfo.InvariantCulture));
            A2ATelemetry.InjectTraceContext(traceContext,
                (name, value) => request.Headers.TryAddWithoutValidation(name, value));

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(options.Timeout);
            using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            activity?.SetTag(A2ATelemetry.Tags.HttpStatusCode, status);
            if (response.IsSuccessStatusCode) return new PushSendResult(PushSendOutcome.Sent);

            var code = status.ToString(CultureInfo.InvariantCulture);
            ServerLog.PushWebhookStatus(logger, config.Id, delivery.Task.TaskId, status, attempt);
            var retryable = status is 408 or 429 || status >= 500;
            return new PushSendResult(retryable ? PushSendOutcome.Retry : PushSendOutcome.Permanent, "HTTP " + code,
                code, status, RetryAfterOf(response, (time ?? TimeProvider.System).GetUtcNow()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex) when (ex is BlockedAddressException ||
                                              ex.InnerException is BlockedAddressException)
        {
            ServerLog.PushWebhookBlocked(logger, config.Id, delivery.Task.TaskId);
            Refused(delivery);
            return new PushSendResult(PushSendOutcome.Permanent, "URL not allowed: the target address is not allowed",
                "UrlNotAllowed");
        }
        catch (HttpRequestException ex)
        {
            ServerLog.PushWebhookAttemptFailed(logger, ex, config.Id, delivery.Task.TaskId, attempt);
            return new PushSendResult(PushSendOutcome.Retry, "Request failed: " + ex.HttpRequestError,
                ex.GetType().FullName);
        }
        catch (OperationCanceledException ex)
        {
            ServerLog.PushWebhookAttemptFailed(logger, ex, config.Id, delivery.Task.TaskId, attempt);
            return new PushSendResult(PushSendOutcome.Retry, "Timed out", "Timeout");
        }
    }

    /// <summary>The receiver's <c>Retry-After</c> as a wait (a date becomes the time until it, at least zero), or null.</summary>
    internal static TimeSpan? RetryAfterOf(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        return null;
    }
}
