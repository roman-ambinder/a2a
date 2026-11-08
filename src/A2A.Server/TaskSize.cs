using System.Text;

namespace A2A.Server;

/// <summary>
/// Estimates how large a task is, without serializing it. Counts the content of the parts of the history and of the
/// artifacts: UTF-8 bytes of text, base64 length of raw bytes, the length of a URL, and the JSON text length of data.
/// Names, ids and metadata are not counted.
/// </summary>
internal static class TaskSize
{
    public static long Measure(AgentTask task)
    {
        long total = 0;
        if (task.History is { } history)
            foreach (var message in history)
                total += Measure(message.Parts);
        if (task.Artifacts is { } artifacts)
            foreach (var artifact in artifacts)
                total += Measure(artifact.Parts);
        return total;
    }

    private static long Measure(List<Part> parts)
    {
        long total = 0;
        foreach (var part in parts)
        {
            total += part switch
            {
                TextPart text => Encoding.UTF8.GetByteCount(text.Text),
                RawPart raw => ((long)raw.Raw.Length + 2) / 3 * 4,
                UrlPart url => url.Url.OriginalString.Length,
                DataPart data => data.Data.GetRawText().Length,
                _ => 0,
            };
        }
        return total;
    }
}
