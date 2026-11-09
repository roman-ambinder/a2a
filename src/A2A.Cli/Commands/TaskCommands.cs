using A2A.Json;

namespace A2A.Cli.Commands;

/// <summary><c>a2a get</c>, <c>a2a list</c> and <c>a2a cancel</c>.</summary>
internal static class TaskCommands
{
    public static async Task<int> GetAsync(CommandContext c, CancellationToken cancellationToken)
    {
        var request = new GetTaskRequest { Id = c.Args.Positionals[0], HistoryLength = c.Args.Int("history-length", 0) };
        var task = await c.Client.GetTaskAsync(request, c.RequestOptions(), cancellationToken).ConfigureAwait(false);
        PrintTask(c, task, withHistory: true);
        return ExitCodes.Success;
    }

    public static async Task<int> CancelAsync(CommandContext c, CancellationToken cancellationToken)
    {
        var request = new CancelTaskRequest { Id = c.Args.Positionals[0] };
        var task = await c.Client.CancelTaskAsync(request, c.RequestOptions(), cancellationToken).ConfigureAwait(false);
        PrintTask(c, task, withHistory: false);
        return ExitCodes.Success;
    }

    public static async Task<int> ListAsync(CommandContext c, CancellationToken cancellationToken)
    {
        var request = new ListTasksRequest
        {
            ContextId = c.Args.Value("context-id"),
            Status = ParseState(c.Args.Value("status")),
            PageSize = c.Args.Int("page-size", 1),
            PageToken = c.Args.Value("page-token"),
            IncludeArtifacts = c.Args.Has("include-artifacts") ? true : null,
        };
        var response = await c.Client.ListTasksAsync(request, c.RequestOptions(), cancellationToken).ConfigureAwait(false);
        if (c.Out.Json)
        {
            c.Out.Line(A2AJson.Serialize(response));
            return ExitCodes.Success;
        }
        foreach (var task in response.Tasks) c.Out.Line(Render.TaskLine(task, c.Out));
        c.Out.Line($"{response.Tasks.Count} task(s) shown, {response.TotalSize} in all.");
        if (response.NextPageToken.Length > 0) c.Out.Line($"Next page: --page-token {c.Out.One(response.NextPageToken, 500)}");
        return ExitCodes.Success;
    }

    private static void PrintTask(CommandContext c, AgentTask task, bool withHistory)
    {
        if (c.Out.Json) c.Out.Line(A2AJson.Serialize(task));
        else foreach (var line in Render.TaskText(task, c.Out, withHistory)) c.Out.Line(line);
    }

    /// <summary>Accepts the state name in any case, with or without dashes, underscores or the TASK_STATE_ prefix.</summary>
    internal static TaskState? ParseState(string? text)
    {
        if (text is null) return null;
        var name = text.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (name.StartsWith("taskstate", StringComparison.OrdinalIgnoreCase)) name = name["taskstate".Length..];
        foreach (var state in Enum.GetValues<TaskState>())
            if (state != TaskState.Unspecified && string.Equals(state.ToString(), name, StringComparison.OrdinalIgnoreCase)) return state;
        throw CliException.Usage("Option '--status' must be one of: submitted, working, completed, failed, canceled, input-required, rejected, auth-required.");
    }
}
