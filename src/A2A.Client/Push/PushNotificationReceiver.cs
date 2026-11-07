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
public sealed class PushNotificationReceiver(TimeProvider? time = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxTolerance = TimeSpan.FromDays(3650); // keeps the arithmetic in range

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    /// <summary>Remembered ids and the time each is forgotten.</summary>
    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>
    /// Validates a delivery. The token may arrive in <c>X-A2A-Notification-Token</c> or as <c>Authorization: Bearer</c>.
    /// Header names are matched case-insensitively. A delivery is remembered only once it is valid.
    /// </summary>
    public PushValidationResult Validate(IReadOnlyDictionary<string, string> headers, string expectedToken)
    {
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        var matched = false;
        foreach (var candidate in Candidates(headers))
            matched |= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), expected);
        if (!matched || expected.Length == 0) return PushValidationResult.InvalidToken;

        var id = Find(headers, A2AProtocol.Headers.DeliveryId);
        return string.IsNullOrEmpty(id) ? PushValidationResult.Valid : Remember(id, null);
    }

    /// <summary>The longest age (and distance into the future) of a signed delivery accepted by default.</summary>
    public static readonly TimeSpan DefaultSignatureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifies a delivery signed as Standard Webhooks (<see cref="WebhookSignature"/>): the <c>webhook-signature</c> header must
    /// hold a <c>v1</c> signature (several may be given, space-separated) over <c>{webhook-id}.{webhook-timestamp}.{body}</c> made
    /// with <paramref name="secret"/> (the decoded key, see <see cref="WebhookSignature.TryParseSecret"/>), compared in constant
    /// time, and <c>webhook-timestamp</c> must be within <paramref name="tolerance"/> (default 5 minutes) of now. A delivery is
    /// remembered by its <c>webhook-id</c> only once it is valid, so a replay within the memory window is <see cref="PushValidationResult.Duplicate"/>.
    /// <paramref name="body"/> must be the raw request body bytes. Header names are matched case-insensitively.
    /// </summary>
    public PushValidationResult VerifySignature(IReadOnlyDictionary<string, string> headers, ReadOnlySpan<byte> body, ReadOnlySpan<byte> secret, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (tolerance is { } t && (t <= TimeSpan.Zero || t == Timeout.InfiniteTimeSpan))
            throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive and finite.");
        var id = Find(headers, WebhookSignature.IdHeader);
        var timestamp = Find(headers, WebhookSignature.TimestampHeader);
        var signature = Find(headers, WebhookSignature.SignatureHeader);
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(signature) || secret.IsEmpty
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || !WebhookSignature.Matches(signature, secret, id, seconds, body))
            return PushValidationResult.InvalidSignature;

        var allowed = tolerance ?? DefaultSignatureTolerance;
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (seconds > now + allowed.TotalSeconds || seconds < now - allowed.TotalSeconds) return PushValidationResult.Stale;
        // A delivery is acceptable from ts - tolerance to ts + tolerance, so its id is kept until ts + tolerance (+1 s: the
        // check above works in whole seconds), however long the usual memory window is.
        var lastAcceptable = DateTimeOffset.FromUnixTimeSeconds(seconds) + (allowed > MaxTolerance ? MaxTolerance : allowed) + TimeSpan.FromSeconds(1);
        return Remember("webhook:" + id, lastAcceptable); // apart from token deliveries, so a receiver may use both checks on one delivery
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
        if (!string.IsNullOrEmpty(token)) yield return token;
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
