using System.Net;
using System.Text.Json;
using A2A.Json;

namespace A2A.TestKit;

/// <summary>
/// A fake webhook endpoint. Register <see cref="DefaultUrl"/> as the webhook of a task; the host sends its deliveries here
/// instead of to the network. Records every delivery and answers with <see cref="Respond"/>.
/// </summary>
public sealed class TestPushReceiver : HttpMessageHandler
{
    /// <summary>A webhook URL to register with the agent. Any URL works, because the host never opens a connection.</summary>
    public const string DefaultUrl = "http://push.test/webhook";

    private readonly Recorder<ReceivedPush> _deliveries = new();

    /// <summary>Every delivery received so far, oldest first. A copy: it does not change later.</summary>
    public IReadOnlyList<ReceivedPush> Deliveries => _deliveries.Snapshot();

    /// <summary>The parsed event of every delivery, in order. Throws <see cref="A2AAssertionException"/> when a delivery body is not a valid event.</summary>
    public IReadOnlyList<StreamResponse> Events => _deliveries.Snapshot().Select(d => d.Event).ToArray();

    /// <summary>The status code to answer a delivery with. Default: 200 OK for every delivery. A 5xx makes the host retry.</summary>
    public Func<ReceivedPush, HttpStatusCode> Respond { get; set; } = _ => HttpStatusCode.OK;

    /// <summary>Creates the invoker the host uses to send deliveries. Disposing it does not dispose this receiver.</summary>
    public HttpMessageInvoker CreateInvoker() => new(this, disposeHandler: false);

    /// <summary>Forgets the deliveries received so far.</summary>
    public void Clear() => _deliveries.Clear();

    /// <summary>
    /// Waits for the first delivery that matches <paramref name="predicate"/> (any delivery when null), looking at the ones
    /// received already and at future ones. Does not poll.
    /// </summary>
    /// <param name="predicate">Which delivery to wait for.</param>
    /// <param name="timeout">How long to wait, in real time. Default 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="A2AAssertionException">No delivery matched in time. The message lists what did arrive.</exception>
    public Task<ReceivedPush> WaitForAsync(Func<ReceivedPush, bool>? predicate = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        return _deliveries.WaitAsync<ReceivedPush>(
            all =>
            {
                foreach (var d in all)
                    if (predicate is null || predicate(d)) return (true, d);
                return (false, null!);
            },
            limit,
            all => $"No push delivery matched within {limit}. {Describe(all)}",
            cancellationToken);
    }

    /// <summary>Waits until at least <paramref name="count"/> deliveries were received, and returns the first <paramref name="count"/>. Does not poll.</summary>
    /// <param name="count">How many deliveries to wait for.</param>
    /// <param name="timeout">How long to wait, in real time. Default 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="A2AAssertionException">Fewer deliveries arrived in time. The message lists what did arrive.</exception>
    public Task<IReadOnlyList<ReceivedPush>> WaitForCountAsync(int count, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        return _deliveries.WaitAsync<IReadOnlyList<ReceivedPush>>(
            all => all.Count >= count ? (true, all.Take(count).ToArray()) : (false, []),
            limit,
            all => $"Expected {count} push deliveries within {limit}, but {all.Count} arrived. {Describe(all)}",
            cancellationToken);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var delivery = new ReceivedPush(request, body);
        _deliveries.Add(delivery);
        return new HttpResponseMessage(Respond(delivery));
    }

    private static string Describe(IReadOnlyList<ReceivedPush> all) =>
        all.Count == 0 ? "Nothing arrived." : "Arrived: " + string.Join("; ", all.Select(d => d.ToString()));
}

/// <summary>One webhook call received by a <see cref="TestPushReceiver"/>.</summary>
public sealed class ReceivedPush
{
    private readonly object _gate = new();
    private StreamResponse? _event;
    private A2AAssertionException? _parseError;

    internal ReceivedPush(HttpRequestMessage request, string body)
    {
        Url = request.RequestUri ?? new Uri(TestPushReceiver.DefaultUrl);
        Method = request.Method;
        Body = body;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in request.Headers) headers[h.Key] = string.Join(", ", h.Value);
        if (request.Content is not null)
            foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(", ", h.Value);
        Headers = headers;
    }

    /// <summary>The URL the host called.</summary>
    public Uri Url { get; }

    /// <summary>The HTTP method. Always POST for a host delivery.</summary>
    public HttpMethod Method { get; }

    /// <summary>The request headers, content headers included. Names are case-insensitive. Several values are joined with a comma.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>The request body as text.</summary>
    public string Body { get; }

    /// <summary>The event in the body, parsed with the SDK serializer.</summary>
    /// <exception cref="A2AAssertionException">The body is not a valid <see cref="StreamResponse"/>.</exception>
    public StreamResponse Event
    {
        get
        {
            lock (_gate)
            {
                if (_event is not null) return _event;
                if (_parseError is not null) throw _parseError;
                try
                {
                    return _event = A2AJson.Deserialize<StreamResponse>(Body);
                }
                catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    _parseError = new A2AAssertionException($"The push body is not a valid StreamResponse ({e.Message}). Body: {Truncate(Body)}", e);
                    throw _parseError;
                }
            }
        }
    }

    /// <summary>The <c>X-A2A-Delivery-Id</c> header: the same on every retry of one delivery. Null when missing.</summary>
    public string? DeliveryId => Header(A2AProtocol.Headers.DeliveryId);

    /// <summary>The <c>X-A2A-Notification-Sequence</c> header: the event's sequence number in its task. Null when missing or not a number.</summary>
    public long? Sequence => long.TryParse(Header(A2AProtocol.Headers.NotificationSequence), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The <c>X-A2A-Notification-Token</c> header: the token registered with the webhook. Null when missing.</summary>
    public string? Token => Header(A2AProtocol.Headers.NotificationToken);

    /// <summary>A one-line description for failure messages.</summary>
    public override string ToString()
    {
        string what;
        try { what = TestEvents.Describe([Event]); }
        catch (A2AAssertionException) { what = "(not a StreamResponse)"; }
        return $"#{Sequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"} {what}";
    }

    private string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "...";
}
