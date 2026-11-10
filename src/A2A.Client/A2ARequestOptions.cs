namespace A2A.Client;

/// <summary>
///     Options for one client call. Pass it as the <c>options</c> argument of any <see cref="IA2AClient" /> method.
/// </summary>
/// <remarks>
///     <para>
///         Header order on every HTTP request: the SDK sets <c>A2A-Version</c>, <c>A2A-Extensions</c>, <c>Accept</c> and
///         <c>Idempotency-Key</c> first. Then <see cref="Headers" /> are applied (a key replaces the SDK value of the same
///         name).
///         Then <see cref="ConfigureRequest" /> runs last and sees the final request. Both can therefore overwrite or
///         remove
///         <c>A2A-Version</c>. The SDK does not stop you; do not do it.
///     </para>
///     <para>The same options object is applied again on every automatic retry.</para>
/// </remarks>
public sealed class A2ARequestOptions
{
    /// <summary>
    ///     The time limit of one unary attempt when <see cref="Timeout" /> is not set: 100 seconds. Every transport uses
    ///     it.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    private int? _maxRetries;

    /// <summary>Extra HTTP headers for this call.</summary>
    public IDictionary<string, string> Headers { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     <c>Idempotency-Key</c> for <c>SendMessage</c> and <c>SendStreamingMessage</c>. Default: the message id.
    ///     Ignored by other methods. Servers built with this SDK refuse a key (or message id) longer than 256 characters.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    ///     Time limit for one attempt of a unary call. Default 100 seconds (<see cref="DefaultTimeout" />). Streaming calls
    ///     have no
    ///     time limit and ignore this. Use <see cref="Timeout.InfiniteTimeSpan" /> to disable it.
    /// </summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    ///     Automatic retries after the first attempt of this unary call. Default: <see cref="A2AClientOptions.MaxRetries" />
    ///     (2, so 3 attempts in all); 0 disables retries. Streaming calls are never retried.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A retry happens only on HTTP 503 or 429, on an error the server marked <c>metadata.retryable</c> = "true", or
    ///         on a
    ///         transport failure. <c>CreateTaskPushNotificationConfig</c> and <c>DeleteTaskPushNotificationConfig</c> are
    ///         never retried.
    ///     </para>
    ///     <para>
    ///         For <c>SendMessage</c> a transport failure is retried only when the request provably never reached the server
    ///         (<see cref="HttpRequestException.HttpRequestError" /> is <see cref="HttpRequestError.ConnectionError" />,
    ///         <see cref="HttpRequestError.NameResolutionError" />, <see cref="HttpRequestError.ProxyTunnelError" /> or
    ///         <see cref="HttpRequestError.SecureConnectionError" />). A failure after the request was sent, such as a
    ///         connection reset
    ///         mid-response, is surfaced: a plain A2A v1.0 server without <c>Idempotency-Key</c> support could otherwise
    ///         create the task twice.
    ///     </para>
    /// </remarks>
    public int? MaxRetries
    {
        get => _maxRetries;
        set => _maxRetries = value is null or >= 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "MaxRetries cannot be negative.");
    }

    /// <summary>Extension URIs for this call. Added to <see cref="A2AClientOptions.Extensions" />.</summary>
    public IList<string>? Extensions { get; set; }

    /// <summary>
    ///     Last hook before a request is sent, for example to sign it or add a per-call credential. Runs after
    ///     <see cref="Headers" />, once per attempt.
    /// </summary>
    public Func<HttpRequestMessage, CancellationToken, ValueTask>? ConfigureRequest { get; set; }
}