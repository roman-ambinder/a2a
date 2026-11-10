using System.Globalization;

namespace A2A.Server.Streaming;

/// <summary>
///     The SSE event id of a task event: <c>{taskId}:{sequence}</c>. Clients send the last one they saw back in the
///     <c>Last-Event-ID</c> header to resume a stream.
/// </summary>
public static class StreamCursor
{
    /// <summary>Formats the cursor for event <paramref name="seq" /> of task <paramref name="taskId" />.</summary>
    public static string Format(string taskId, long seq)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{taskId}:{seq}");
    }

    /// <summary>
    ///     Parses a cursor for task <paramref name="expectedTaskId" />. False when <paramref name="value" /> is null,
    ///     malformed,
    ///     has a sequence below 1, or names another task.
    /// </summary>
    public static bool TryParse(string? value, string expectedTaskId, out long seq)
    {
        if (TryParse(value, out var taskId, out seq) &&
            string.Equals(taskId, expectedTaskId, StringComparison.Ordinal)) return true;
        seq = 0;
        return false;
    }

    /// <summary>
    ///     Parses a cursor without knowing the task. False when <paramref name="value" /> is null, malformed, or has a
    ///     sequence below 1.
    /// </summary>
    internal static bool TryParse(string? value, out string taskId, out long seq)
    {
        taskId = "";
        seq = 0;
        if (string.IsNullOrEmpty(value)) return false;
        var colon = value.LastIndexOf(':');
        if (colon <= 0) return false;
        if (!long.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 1) return false;
        taskId = value[..colon];
        seq = parsed;
        return true;
    }
}