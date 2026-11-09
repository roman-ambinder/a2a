using System.Globalization;
using System.Text;

namespace A2A.TestKit;

/// <summary>Helpers to collect and print streams of <see cref="StreamResponse"/> events.</summary>
public static class TestEvents
{
    /// <summary>
    /// Reads a stream to its end and returns the events. Never hangs: after <paramref name="timeout"/> (real time) it throws
    /// with the events received so far.
    /// </summary>
    /// <param name="stream">The stream, for example from <c>SendStreamingMessageAsync</c>.</param>
    /// <param name="timeout">How long to wait for the whole stream. Default 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="A2AAssertionException">The stream did not end in time.</exception>
    public static async Task<IReadOnlyList<StreamResponse>> CollectAsync(this IAsyncEnumerable<StreamResponse> stream, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var events = new List<StreamResponse>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var enumerator = stream.GetAsyncEnumerator(cts.Token);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var remaining = limit - watch.Elapsed;
                bool more;
                try
                {
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                    more = await enumerator.MoveNextAsync().AsTask().WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    await cts.CancelAsync().ConfigureAwait(false);
                    throw new A2AAssertionException($"The stream did not end within {limit}. Events so far: {Describe(events)}");
                }
                if (!more) return events;
                events.Add(enumerator.Current);
            }
        }
        finally
        {
            try { await enumerator.DisposeAsync().ConfigureAwait(false); }
            catch (Exception e) when (e is NotSupportedException or OperationCanceledException or InvalidOperationException) { /* the stream was still running when it was abandoned */ }
        }
    }

    /// <summary>
    /// Describes events on one line, for example <c>Task(SUBMITTED), Status(WORKING), Artifact(result), Status(COMPLETED)</c>.
    /// A message shows its text. An artifact shows its name, or its id when it has no name.
    /// </summary>
    public static string Describe(IEnumerable<StreamResponse> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var text = string.Join(", ", events.Select(Describe));
        return text.Length == 0 ? "(no events)" : text;
    }

    internal static string Describe(StreamResponse e) => e switch
    {
        TaskEvent t => $"Task({StateName(t.Task.Status.State)})",
        MessageEvent m => $"Message({Quote(m.Message.GetText())})",
        StatusUpdateEvent s => $"Status({StateName(s.StatusUpdate.Status.State)})",
        ArtifactUpdateEvent a => $"Artifact({a.ArtifactUpdate.Artifact.Name ?? a.ArtifactUpdate.Artifact.ArtifactId})",
        _ => e.GetType().Name,
    };

    internal static string Describe(SendMessageResponse r) => r switch
    {
        TaskResponse t => $"a task ({StateName(t.Task.Status.State)})",
        MessageResponse m => $"a message ({Quote(m.Message.GetText())})",
        _ => r.GetType().Name,
    };

    internal static string StateName(TaskState state)
    {
        var name = state.ToString();
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i])) sb.Append('_');
            sb.Append(char.ToUpper(name[i], CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    internal static string StateList(IEnumerable<TaskState> states)
    {
        var text = string.Join(", ", states.Select(StateName));
        return text.Length == 0 ? "(none)" : text;
    }

    private static string Quote(string text) => "\"" + (text.Length <= 40 ? text : text[..40] + "...") + "\"";
}
