using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using A2A.Client.Resilience;
using A2A.Json;

namespace A2A.Client.Transports;

/// <summary>Shared HTTP plumbing for the JSON-RPC and HTTP+JSON transports.</summary>
public abstract class HttpTransportBase
{
    /// <summary>Creates the base. <paramref name="options"/> defaults to a new <see cref="A2AClientOptions"/>.</summary>
    protected HttpTransportBase(HttpClient httpClient, A2AClientOptions? options)
    {
        Http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Options = options ?? new A2AClientOptions();
    }

    /// <summary>The HTTP client used for all calls.</summary>
    protected HttpClient Http { get; }

    /// <summary>The client options.</summary>
    protected A2AClientOptions Options { get; }

    /// <summary>Default time limit of one unary attempt.</summary>
    internal static readonly TimeSpan DefaultUnaryTimeout = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Sets the request headers in this order: SDK headers (<c>A2A-Version</c>, <c>A2A-Extensions</c>, <c>Accept</c>,
    /// <c>Idempotency-Key</c>, and <c>Last-Event-ID</c> on a stream resume), then <see cref="A2ARequestOptions.Headers"/>, then <see cref="A2ARequestOptions.ConfigureRequest"/>.
    /// The later steps can replace or remove the earlier ones, including <c>A2A-Version</c>.
    /// </summary>
    protected async Task ApplyHeadersAsync(HttpRequestMessage request, bool streaming, A2ARequestOptions? call, string? idempotencyKey, CancellationToken ct, string? lastEventId = null)
    {
        request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.Version, Options.ProtocolVersion);
        var extensions = call?.Extensions is { Count: > 0 } perCall ? Options.Extensions.Concat(perCall).Distinct() : Options.Extensions;
        var joined = string.Join(",", extensions);
        if (joined.Length > 0) request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.Extensions, joined);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? A2AProtocol.EventStreamMediaType : A2AProtocol.MediaType));
        if (!streaming) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.JsonMediaType));
        if (!string.IsNullOrEmpty(idempotencyKey)) request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.IdempotencyKey, idempotencyKey);
        if (!string.IsNullOrEmpty(lastEventId)) request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.LastEventId, lastEventId);

        if (call is null) return;
        foreach (var (name, value) in call.Headers)
        {
            bool isContentHeader;
            try
            {
                request.Headers.Remove(name);
                isContentHeader = !request.Headers.TryAddWithoutValidation(name, value);
            }
            catch (InvalidOperationException)
            {
                isContentHeader = true; // Remove throws for names that belong to the content (Content-Type, Content-Language, ...)
            }
            if (isContentHeader && request.Content is not null)
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (call.ConfigureRequest is { } configure) await configure(request, ct).ConfigureAwait(false);
    }

    /// <summary>The key for <c>SendMessage</c>: the caller's, else the message id. Fills an empty message id first.</summary>
    protected static string? PrepareSend(SendMessageRequest request, A2ARequestOptions? call)
    {
        if (request.Message is not { } message) return call?.IdempotencyKey;
        if (string.IsNullOrWhiteSpace(message.MessageId)) message.MessageId = Message.NewId();
        return string.IsNullOrEmpty(call?.IdempotencyKey) ? message.MessageId : call.IdempotencyKey;
    }

    /// <summary>
    /// Runs one unary operation: each attempt gets the call timeout, and failed attempts are retried by
    /// <see cref="RetryPolicy"/> under <paramref name="retry"/>, at most <see cref="A2ARequestOptions.MaxRetries"/>
    /// (else <see cref="A2AClientOptions.MaxRetries"/>) times. <paramref name="attempt"/> builds and sends a fresh request every time.
    /// </summary>
    private protected async Task<T> ExecuteUnaryAsync<T>(RetryMode retry, A2ARequestOptions? call, Func<CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        var timeout = call?.Timeout ?? DefaultUnaryTimeout;
        var maxAttempts = 1 + (call?.MaxRetries ?? Options.MaxRetries);
        for (var n = 1; ; n++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await WithTimeoutAsync(timeout, attempt, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (n < maxAttempts && !ct.IsCancellationRequested && RetryPolicy.ShouldRetry(retry, ex, out _))
            {
                RetryPolicy.ShouldRetry(retry, ex, out var retryAfter);
                await Task.Delay(RetryPolicy.Delay(n + 1, retryAfter), Options.TimeProvider, ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<T> WithTimeoutAsync<T>(TimeSpan timeout, Func<CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        if (timeout == Timeout.InfiniteTimeSpan) return await attempt(ct).ConfigureAwait(false);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await attempt(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TaskCanceledException($"The request timed out after {timeout.TotalSeconds:0.###} s.", new TimeoutException(null, ex), cts.Token);
        }
    }

    /// <summary>Retry-After of a response: the header (whole seconds), else <c>metadata.retryAfter</c> of the error details.</summary>
    protected static TimeSpan? RetryAfterOf(HttpResponseMessage? response, IEnumerable<JsonElement>? details)
    {
        if (response?.Headers.RetryAfter is { } header)
        {
            if (header.Delta is { } delta) return delta;
            if (header.Date is { } date) return date - DateTimeOffset.UtcNow;
        }
        if (details is null) return null;
        foreach (var d in details)
        {
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object
                && m.TryGetProperty("retryAfter", out var r) && r.ValueKind == JsonValueKind.String
                && long.TryParse(r.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }
        return null;
    }

    /// <summary>Creates a UTF-8 JSON request body with the given media type.</summary>
    protected static HttpContent JsonBody<T>(T body, string mediaType)
    {
        var content = new ByteArrayContent(A2AJson.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return content;
    }

    /// <summary>True when the response media type is <c>*/json</c> or <c>*+json</c>.</summary>
    protected static bool IsJson(HttpResponseMessage response)
    {
        var mt = response.Content.Headers.ContentType?.MediaType;
        return mt is not null && (mt.EndsWith("/json", StringComparison.OrdinalIgnoreCase) || mt.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the response media type is <c>text/event-stream</c>.</summary>
    protected static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, A2AProtocol.EventStreamMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Error for a non-success response with no parseable A2A error body.</summary>
    protected static A2AException HttpError(HttpResponseMessage response, string? body)
    {
        var status = (int)response.StatusCode;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => A2AErrorCode.Unauthenticated,
            HttpStatusCode.Forbidden => A2AErrorCode.PermissionDenied,
            HttpStatusCode.NotFound => A2AErrorCode.MethodNotFound,
            _ => A2AErrorCode.Internal,
        };
        var msg = $"HTTP {status} {response.ReasonPhrase}";
        if (!string.IsNullOrWhiteSpace(body) && body.Length < 2048) msg += $": {body}";
        return new A2AException(code, msg) { HttpStatus = status, RetryAfter = RetryAfterOf(response, null) };
    }

    /// <summary>Creates an InvalidAgentResponse error for a response that breaks the protocol.</summary>
    protected static A2AException ProtocolError(string message, Exception? inner = null) =>
        new(A2AErrorCode.InvalidAgentResponse, message, inner);

    /// <summary>Deserializes <paramref name="element"/>. Throws a protocol error that names <paramref name="what"/> when it is malformed.</summary>
    protected static T Parse<T>(JsonElement element, string what)
    {
        try { return A2AJson.Deserialize<T>(element); }
        catch (JsonException ex) { throw ProtocolError($"Malformed {what} in agent response: {ex.Message}", ex); }
    }
}
