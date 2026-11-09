using A2A.Json;

namespace A2A.Cli.Commands;

/// <summary><c>a2a send</c>, <c>a2a stream</c> and <c>a2a subscribe</c>.</summary>
internal static class MessageCommands
{
    public static async Task<int> SendAsync(CommandContext c, CancellationToken cancellationToken)
    {
        var request = await BuildRequestAsync(c, c.Args.Has("no-wait"), cancellationToken).ConfigureAwait(false);
        var response = await c.Client.SendMessageAsync(request, c.RequestOptions(), cancellationToken).ConfigureAwait(false);
        if (c.Out.Json) c.Out.Line(A2AJson.Serialize(response));
        else foreach (var line in Render.SendText(response, c.Out)) c.Out.Line(line);
        return ExitCodes.ForTaskState((response as TaskResponse)?.Task.Status.State);
    }

    public static async Task<int> StreamAsync(CommandContext c, CancellationToken cancellationToken)
    {
        var request = await BuildRequestAsync(c, false, cancellationToken).ConfigureAwait(false);
        using var overall = c.OverallTimeout(cancellationToken);
        return await PrintEventsAsync(c, c.Client.SendStreamingMessageAsync(request, c.RequestOptions(), overall.Token), overall.Token).ConfigureAwait(false);
    }

    public static async Task<int> SubscribeAsync(CommandContext c, CancellationToken cancellationToken)
    {
        using var overall = c.OverallTimeout(cancellationToken);
        var request = new SubscribeToTaskRequest { Id = c.Args.Positionals[0] };
        return await PrintEventsAsync(c, c.Client.SubscribeToTaskAsync(request, c.RequestOptions(), overall.Token), overall.Token).ConfigureAwait(false);
    }

    private static async Task<int> PrintEventsAsync(CommandContext c, IAsyncEnumerable<StreamResponse> events, CancellationToken cancellationToken)
    {
        TaskState? last = null;
        await foreach (var e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            c.Out.Line(c.Out.Json ? A2AJson.Serialize(e) : Render.EventLine(e, c.Out));
            last = e switch
            {
                TaskEvent t => t.Task.Status.State,
                StatusUpdateEvent s => s.StatusUpdate.Status.State,
                _ => last,
            };
            // A task that waits for input or authorization sends nothing more until someone answers, so stop here.
            if (last is { } state && (state.IsTerminal() || state.IsInterrupted())) break;
        }
        return ExitCodes.ForTaskState(last);
    }

    private static async Task<SendMessageRequest> BuildRequestAsync(CommandContext c, bool noWait, CancellationToken cancellationToken)
    {
        var text = c.Args.Positionals[0];
        if (text == "-") text = await c.Env.In.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text)) throw CliException.Usage("The message text is empty.");

        var history = c.Args.Int("history-length", 0);
        return new SendMessageRequest
        {
            Message = Message.FromUser(text, c.Args.Value("context-id"), c.Args.Value("task-id")),
            Configuration = history is null && !noWait ? null : new SendMessageConfiguration { HistoryLength = history, ReturnImmediately = noWait },
        };
    }
}
