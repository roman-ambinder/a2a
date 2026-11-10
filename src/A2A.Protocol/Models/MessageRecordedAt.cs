using System.Globalization;
using System.Text.Json;

namespace A2A;

/// <summary>Reads the time the server recorded a history message (<see cref="A2AProtocol.MessageRecordedAtKey" />).</summary>
public static class MessageRecordedAtExtensions
{
    /// <summary>
    ///     When the server added the message to the task history, in UTC. Null when the key is absent, its value is not a
    ///     string,
    ///     or it is not a valid ISO 8601 time. Does not throw on a bad shape.
    /// </summary>
    /// <remarks>Informational. Order messages by their position in the history, not by this time.</remarks>
    public static DateTimeOffset? GetRecordedAt(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Metadata is not { } metadata ||
            !metadata.TryGetValue(A2AProtocol.MessageRecordedAtKey, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        string? s;
        try
        {
            s = value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at.ToUniversalTime()
            : null;
    }
}