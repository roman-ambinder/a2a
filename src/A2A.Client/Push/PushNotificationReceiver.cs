using System.Security.Cryptography;
using System.Text;

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
}

/// <summary>
/// Helper for webhook receivers: checks the notification token in constant time and drops repeated deliveries.
/// Thread-safe. Keep one instance per receiver.
/// </summary>
public sealed class PushNotificationReceiver(TimeProvider? time = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
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
        if (string.IsNullOrEmpty(id)) return PushValidationResult.Valid;

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_seen.Count > 0)
            {
                List<string>? expired = null;
                foreach (var (key, at) in _seen)
                    if (now - at >= Window) (expired ??= []).Add(key);
                if (expired is not null) foreach (var key in expired) _seen.Remove(key);
            }
            if (_seen.ContainsKey(id)) return PushValidationResult.Duplicate;
            _seen[id] = now;
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
