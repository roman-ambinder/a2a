using System.Runtime.CompilerServices;
using System.Text;

namespace A2A.Client.Sse;

/// <summary>A dispatched Server-Sent Event.</summary>
/// <param name="Data">The data field.</param>
/// <param name="EventType">The event field, if any.</param>
/// <param name="Id">The last event id seen on the stream so far (WHATWG semantics: it carries over to later events).</param>
public sealed record SseEvent(string Data, string? EventType, string? Id)
{
    /// <summary>The id field of this event only. Null when this event carried no <c>id:</c> line. Not part of equality.</summary>
    public string? FrameId { get; init; }

    /// <inheritdoc/>
    public bool Equals(SseEvent? other) => other is not null && Data == other.Data && EventType == other.EventType && Id == other.Id;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Data, EventType, Id);
}

/// <summary>Server-Sent Events parser (WHATWG HTML §9.2.6). Handles CRLF/LF/CR, multi-line data, comments, and BOM.</summary>
public static class SseReader
{
    /// <summary>Reads events from <paramref name="stream"/> until it ends.</summary>
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        var data = new StringBuilder();
        var hasData = false;
        string? eventType = null;
        string? lastId = null;
        string? frameId = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
#if NET8_0_OR_GREATER
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
#else
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
#endif
            if (line is null)
            {
                // End of stream: per spec, an incomplete event is discarded.
                yield break;
            }

            if (line.Length == 0)
            {
                if (hasData)
                {
                    var payload = data.ToString();
                    yield return new SseEvent(payload, eventType, lastId) { FrameId = frameId };
                }
                data.Clear();
                hasData = false;
                eventType = null;
                frameId = null;
                continue;
            }

            if (line[0] == ':') continue; // comment / keep-alive

            var colon = line.IndexOf(':');
            string field, value;
            if (colon < 0) { field = line; value = string.Empty; }
            else
            {
                field = line[..colon];
                value = colon + 1 < line.Length && line[colon + 1] == ' ' ? line[(colon + 2)..] : line[(colon + 1)..];
            }

            switch (field)
            {
                case "data":
                    if (hasData) data.Append('\n');
                    data.Append(value);
                    hasData = true;
                    break;
                case "event":
                    eventType = value;
                    break;
                case "id":
                    if (!value.Contains('\0')) lastId = frameId = value;
                    break;
                // "retry" and unknown fields are ignored.
            }
        }
    }
}
