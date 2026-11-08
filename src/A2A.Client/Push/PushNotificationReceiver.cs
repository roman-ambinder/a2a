using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using A2A.Push;

namespace A2A.Client.Push;

/// <summary>Outcome of <see cref="PushNotificationReceiver.Validate"/>.</summary>
public enum PushValidationResult
{
    /// <summary>Token matches and the delivery id has not been seen recently.</summary>
    Valid,

    /// <summary>No token header matched the expected token.</summary>
    InvalidToken,

    /// <summary>The delivery id was already accepted in the last 10 minutes (a retry).</summary>
    Duplicate,

    /// <summary>The signature headers are missing or malformed, or no signature matches the body and the secret.</summary>
    InvalidSignature,

    /// <summary>The signature is valid, but its timestamp is outside the tolerance (a replay of an old delivery, or a wrong clock).</summary>
    Stale,
}

/// <summary>
/// Helper for webhook receivers: checks the notification token in constant time and drops repeated deliveries.
/// Thread-safe. Keep one instance per receiver.
/// </summary>
/// <remarks>
/// Prefer <see cref="HandleAsync"/>: it remembers a delivery only after your handler succeeded. <see cref="Validate"/> and
/// <see cref="VerifySignature"/> remember the id at once, so a failure after them loses the retry.
/// </remarks>
public sealed class PushNotificationReceiver
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxTolerance = TimeSpan.FromDays(3650); // keeps the arithmetic in range

    private readonly TimeProvider _time;
    private readonly IPushDeliveryStore _store;

    /// <summary>Creates a receiver that keeps handled ids in memory (<see cref="InMemoryPushDeliveryStore"/>).</summary>
    public PushNotificationReceiver(TimeProvider? time = null) : this(new InMemoryPushDeliveryStore(), time) { }

    /// <summary>Creates a receiver whose <see cref="HandleAsync"/> keeps handled ids in <paramref name="store"/>, so they can survive a restart.</summary>
    public PushNotificationReceiver(IPushDeliveryStore store, TimeProvider? time = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How long an id claimed by <see cref="HandleAsync"/> counts as in progress before another call may take it over (the handler or its process died). Default 5 minutes.</summary>
    public TimeSpan ProcessingLease { get; init; } = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    /// <summary>Remembered ids and the time each is forgotten.</summary>
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Validates a delivery. The token may arrive in <c>X-A2A-Notification-Token</c> or as <c>Authorization: Bearer</c>.
    /// Header names are matched case-insensitively. A delivery is remembered only once it is valid.
    /// </summary>
    /// <remarks>
    /// Warning: a valid delivery is marked as seen at once, before your code handles it. If your code then fails and you
    /// answer 5xx, the sender retries with the same id, this method says <see cref="PushValidationResult.Duplicate"/>, and the event is lost.
    /// Use <see cref="HandleAsync"/> instead, or make sure nothing can fail after this call returns <see cref="PushValidationResult.Valid"/>
    /// (for example, save the event first, then call it).
    /// </remarks>
    public PushValidationResult Validate(IReadOnlyDictionary<string, string> headers, string expectedToken)
    {
        if (!TokenMatches(headers, expectedToken)) return PushValidationResult.InvalidToken;

        var id = Find(headers, A2AProtocol.Headers.DeliveryId);
        return id is null || id.Length == 0 ? PushValidationResult.Valid : Remember(id, null);
    }

    /// <summary>The longest age (and distance into the future) of a signed delivery accepted by default.</summary>
    public static readonly TimeSpan DefaultSignatureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifies a delivery signed as Standard Webhooks (<see cref="WebhookSignature"/>): the <c>webhook-signature</c> header must
    /// hold a <c>v1</c> signature (several may be given, space-separated) over <c>{webhook-id}.{webhook-timestamp}.{body}</c> made
    /// with <paramref name="secret"/> (the decoded key, see <see cref="WebhookSignature.TryParseSecret"/>), compared in constant
    /// time, and <c>webhook-timestamp</c> must be within <paramref name="tolerance"/> (default 5 minutes) of now. A delivery is
    /// remembered by its <c>webhook-id</c> only once it is valid, so a replay within the memory window is <see cref="PushValidationResult.Duplicate"/>.
    /// The memory grows with the tolerance: each id is kept until its timestamp plus <paramref name="tolerance"/> (at least ten
    /// minutes), so a longer tolerance keeps more ids in memory.
    /// <paramref name="body"/> must be the raw request body bytes. Header names are matched case-insensitively.
    /// </summary>
    /// <remarks>
    /// Warning: a valid delivery is marked as seen at once, before your code handles it. If your code then fails and you
    /// answer 5xx, the sender retries with the same id, this method says <see cref="PushValidationResult.Duplicate"/>, and the event is lost.
    /// Use <see cref="HandleAsync"/> instead, or make sure nothing can fail after this call returns <see cref="PushValidationResult.Valid"/>
    /// (for example, save the event first, then call it).
    /// </remarks>
    public PushValidationResult VerifySignature(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret, TimeSpan? tolerance = null)
    {
        var result = CheckSignature(headers, body, secret, tolerance, out var id, out var lastAcceptable);
        return result != PushValidationResult.Valid ? result : Remember("webhook:" + id, lastAcceptable); // apart from token deliveries, so a receiver may use both checks on one delivery
    }

    /// <summary>Checks the signature and timestamp only. Remembers nothing.</summary>
    private PushValidationResult CheckSignature(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret, TimeSpan? tolerance, out string id, out DateTimeOffset lastAcceptable)
    {
        lastAcceptable = default;
        id = string.Empty;
        ArgumentNullException.ThrowIfNull(headers);
        if (tolerance is { } t && (t <= TimeSpan.Zero || t == Timeout.InfiniteTimeSpan))
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive and finite.");
        var webhookId = Find(headers, WebhookSignature.IdHeader);
        var timestamp = Find(headers, WebhookSignature.TimestampHeader);
        var signature = Find(headers, WebhookSignature.SignatureHeader);
        if (webhookId is null || webhookId.Length == 0 || signature is null || signature.Length == 0 || secret.IsEmpty
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !WebhookSignature.Matches(signature, secret, webhookId, seconds, body))
            return PushValidationResult.InvalidSignature;
        id = webhookId;

        var allowed = tolerance ?? DefaultSignatureTolerance;
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (seconds > now + allowed.TotalSeconds || seconds < now - allowed.TotalSeconds) return PushValidationResult.Stale;
        // A delivery is acceptable from ts - tolerance to ts + tolerance, so its id is kept until ts + tolerance (+1 s: the
        // check above works in whole seconds), however long the usual memory window is.
        lastAcceptable = DateTimeOffset.FromUnixTimeSeconds(seconds) + (allowed > MaxTolerance ? MaxTolerance : allowed) + TimeSpan.FromSeconds(1);
        return PushValidationResult.Valid;
    }

    private static bool TokenMatches(IReadOnlyDictionary<string, string> headers, string expectedToken)
    {
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var matched = false;
        foreach (var candidate in Candidates(headers))
            matched |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), expected);
        return matched && expected.Length != 0;
    }

    /// <summary>
    /// Checks a delivery, runs <paramref name="handler"/>, and tells you what to answer. The delivery id counts as done only after
    /// the handler succeeds: if the handler throws, the id is forgotten, so the sender's retry reaches the handler again.
    /// </summary>
    /// <param name="headers">The request headers. Names are matched case-insensitively.</param>
    /// <param name="body">The raw request body bytes (not re-serialized JSON).</param>
    /// <param name="handler">Your code. It gets the parsed event, the delivery id and the sequence. Save the event before it returns.</param>
    /// <param name="expectedToken">The notification token to require (see <see cref="Validate"/>), or null to skip the token check.</param>
    /// <param name="signingSecret">The decoded signing secret to require (see <see cref="VerifySignature"/>), or empty to skip the signature check.</param>
    /// <param name="signatureTolerance">The tolerance for the signature timestamp. Default <see cref="DefaultSignatureTolerance"/>.</param>
    /// <param name="cancellationToken">Cancels the call. A cancelled handler forgets the id and the cancellation is thrown.</param>
    /// <remarks>
    /// At least one of <paramref name="expectedToken"/> and <paramref name="signingSecret"/> is required; when both are given, both must match.
    /// Status codes: <see cref="PushHandleOutcome.Handled"/> and <see cref="PushHandleOutcome.Duplicate"/> give 200;
    /// <see cref="PushHandleOutcome.InProgress"/> gives 503 (retryable: a sender retries only 408, 429 and 5xx, and drops other 4xx);
    /// <see cref="PushHandleOutcome.HandlerFailed"/> gives 500; <see cref="PushHandleOutcome.Unauthorized"/> gives 401;
    /// a body that is not a <c>StreamResponse</c> gives 400. A delivery without an id cannot be de-duplicated, so the handler runs every time.
    /// </remarks>
    public async Task<PushHandleResult> HandleAsync(
        IReadOnlyDictionary<string, string> headers,
        ReadOnlyMemory<byte> body,
        Func<PushDelivery, CancellationToken, Task> handler,
        string? expectedToken = null,
        ReadOnlyMemory<byte> signingSecret = default,
        TimeSpan? signatureTolerance = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(handler);
        if (expectedToken is null && signingSecret.IsEmpty)
            throw new ArgumentException("Give an expected token, a signing secret, or both.", nameof(expectedToken));
        if (expectedToken is not null && !TokenMatches(headers, expectedToken))
            return new PushHandleResult(PushHandleOutcome.Unauthorized, 401);

        var keepUntil = _time.GetUtcNow() + Window;
        if (!signingSecret.IsEmpty)
        {
            var check = CheckSignature(headers, body.Span, signingSecret.Span, signatureTolerance, out _, out var lastAcceptable);
            if (check != PushValidationResult.Valid) return new PushHandleResult(PushHandleOutcome.Unauthorized, 401);
            if (lastAcceptable > keepUntil) keepUntil = lastAcceptable;
        }

        var id = Find(headers, A2AProtocol.Headers.DeliveryId);
        if (string.IsNullOrEmpty(id)) id = Find(headers, WebhookSignature.IdHeader);
        if (string.IsNullOrEmpty(id)) id = null;
        var sequenceText = Find(headers, A2AProtocol.Headers.NotificationSequence);
        long? sequence = long.TryParse(sequenceText, NumberStyles.None, CultureInfo.InvariantCulture, out var seq) ? seq : null;

        StreamResponse evt;
        try { evt = A2A.Json.A2AJson.Deserialize<StreamResponse>(Encoding.UTF8.GetString(body.ToArray())); }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new PushHandleResult(PushHandleOutcome.BadRequest, 400, id);
        }

        var key = id is null ? null : "delivery:" + id;
        if (key is not null)
        {
            var now = _time.GetUtcNow();
            var claim = await _store.TryBeginAsync(key, now, now + ProcessingLease, cancellationToken).ConfigureAwait(false);
            if (claim == PushDeliveryClaim.AlreadyHandled) return new PushHandleResult(PushHandleOutcome.Duplicate, 200, id);
            if (claim == PushDeliveryClaim.InProgress) return new PushHandleResult(PushHandleOutcome.InProgress, 503, id);
        }

        try
        {
            await handler(new PushDelivery(evt, id, sequence), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (key is not null) await _store.ReleaseAsync(key, CancellationToken.None).ConfigureAwait(false);
            if (e is OperationCanceledException && cancellationToken.IsCancellationRequested) throw;
            return new PushHandleResult(PushHandleOutcome.HandlerFailed, 500, id, e);
        }

        if (key is not null) await _store.CompleteAsync(key, keepUntil, CancellationToken.None).ConfigureAwait(false);
        return new PushHandleResult(PushHandleOutcome.Handled, 200, id);
    }

    /// <summary>Remembers a delivery id for ten minutes, or until <paramref name="keepUntil"/> when later. Duplicate when it was remembered already.</summary>
    private PushValidationResult Remember(string id, DateTimeOffset? keepUntil)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_seen.Count > 0)
            {
                List<string>? expired = null;
                foreach (var (key, until) in _seen)
                    if (now >= until) (expired ??= []).Add(key);
                if (expired is not null) foreach (var key in expired) _seen.Remove(key);
            }
            if (_seen.ContainsKey(id)) return PushValidationResult.Duplicate;
            var expiry = now + Window;
            _seen[id] = keepUntil is { } k && k > expiry ? k : expiry;
        }
        return PushValidationResult.Valid;
    }

    private static IEnumerable<string> Candidates(IReadOnlyDictionary<string, string> headers)
    {
        var token = Find(headers, A2AProtocol.Headers.NotificationToken);
        if (token is { Length: > 0 }) yield return token;
        var auth = Find(headers, "Authorization");
        if (auth is not null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) yield return auth[7..].Trim();
    }

    private static string? Find(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var (key, value) in headers)
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        return null;
    }
}
