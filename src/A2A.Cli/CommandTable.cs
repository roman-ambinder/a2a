using System.Reflection;
using System.Text;
using A2A.Cli.Commands;

namespace A2A.Cli;

/// <summary>The commands, their options and the help text.</summary>
internal static class CommandTable
{
    private static readonly OptionSpec[] Shared =
    [
        new("agent", 'a', "url", "Agent base URL or full Agent Card URL. Default: env A2A_AGENT_URL."),
        new("json", null, null, "Print JSON (one line per event for streams)."),
        new("timeout", null, "seconds",
            "Time limit. Default 100 for one call and the card fetch. Streams and downloads have none unless given."),
        new("retries", null, "n", "Retries after the first attempt of a call."),
        new("tenant", null, "name", "Tenant to send in each request."),
        new("transport", null, "jsonrpc|rest", "Use only this binding."),
        new("allow-http", null, null,
            "Allow a plain HTTP agent URL. Credentials still go only over HTTPS or to loopback.")
    ];

    private static readonly OptionSpec[] Auth =
    [
        new("token", null, "token", "Bearer token. Env: A2A_TOKEN (safer: options show in process lists)."),
        new("api-key", null, "key", "API key. Env: A2A_API_KEY."),
        new("client-id", null, "id", "OAuth2 client id (client credentials). Env: A2A_CLIENT_ID."),
        new("client-secret", null, "secret", "OAuth2 client secret. Env: A2A_CLIENT_SECRET."),
        new("token-endpoint", null, "url", "OAuth2 token endpoint. Default: the card's, when on the card's origin."),
        new("scope", null, "scope", "OAuth2 scope. Can be repeated.", true)
    ];

    private static readonly OptionSpec[] Trust =
    [
        new("trust-jwks", null, "file", "JSON Web Key Set file whose keys are trusted for this agent's origin."),
        new("trust-jku-host", null, "host", "Host that may serve the signing keys (jku). Can be repeated.", true),
        new("require-signature", null, null, "Fail unless the card has a valid signature.")
    ];

    private static readonly OptionSpec ContextId = new("context-id", null, "id", "Continue this conversation context.");
    private static readonly OptionSpec TaskId = new("task-id", null, "id", "Send the message to this task.");

    private static readonly OptionSpec HistoryLength =
        new("history-length", null, "n", "Number of history messages to ask for.");

    public static IReadOnlyList<CommandSpec> All { get; } =
    [
        Make("card", "Fetch and verify the Agent Card.", "", 0, 0, [], CardCommand.RunAsync, false),
        Make("send", "Send a message and wait for the result.", "[options] <text|->", 1, 1,
        [
            ContextId, TaskId, HistoryLength,
            new OptionSpec("no-wait", null, null, "Return at once, without waiting for the task to finish.")
        ], MessageCommands.SendAsync),
        Make("stream", "Send a message and print events as they arrive.", "[options] <text|->", 1, 1,
            [ContextId, TaskId, HistoryLength], MessageCommands.StreamAsync),
        Make("subscribe", "Print the events of a running task.", "[options] <taskId>", 1, 1, [],
            MessageCommands.SubscribeAsync),
        Make("get", "Show one task.", "[options] <taskId>", 1, 1, [HistoryLength], TaskCommands.GetAsync),
        Make("list", "List tasks.", "[options]", 0, 0,
        [
            ContextId,
            new OptionSpec("status", null, "state",
                "Only tasks in this state (for example working or input-required)."),
            new OptionSpec("page-size", null, "n", "Tasks per page."),
            new OptionSpec("page-token", null, "token", "Page token from an earlier list."),
            new OptionSpec("include-artifacts", null, null, "Include artifacts in the listed tasks.")
        ], TaskCommands.ListAsync),
        Make("cancel", "Ask the agent to cancel a task.", "[options] <taskId>", 1, 1, [], TaskCommands.CancelAsync),
        Make("download", "Save the files behind the URL parts of a task's artifacts.", "[options] <taskId>", 1, 1,
        [
            new OptionSpec("artifact", null, "id", "Only this artifact."),
            new OptionSpec("output", 'o', "dir", "Folder to save into. Default: the current folder."),
            new OptionSpec("max-bytes", null, "n", "Largest file accepted. Default 1073741824 (1 GiB)."),
            new OptionSpec("force", null, null, "Replace files that already exist."),
            new OptionSpec("allow-private-storage", null, null,
                "Allow the storage URL to be a private address or plain HTTP (for example MinIO).")
        ], DownloadCommand.RunAsync)
    ];

    public static string Version =>
        typeof(CommandTable).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    public static CommandSpec? Find(string name)
    {
        return All.FirstOrDefault(c => c.Name == name);
    }

    private static CommandSpec Make(string name, string summary, string usage, int min, int max, OptionSpec[] own,
        Func<CommandContext, CancellationToken, Task<int>> run, bool needsInterface = true)
    {
        return new CommandSpec(name, summary, usage, min, max, [.. own, .. Shared, .. Auth, .. Trust], run,
            needsInterface);
    }

    public static string GeneralHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("a2a: a command line client for A2A (Agent2Agent) agents.");
        sb.AppendLine();
        sb.AppendLine("Usage: a2a <command> [options] [arguments]");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        foreach (var c in All) sb.AppendLine($"  {c.Name,-10} {c.Summary}");
        sb.AppendLine($"  {"keys",-10} {Commands.KeysCommand.Summary}");
        sb.AppendLine();
        sb.AppendLine("Run 'a2a <command> --help' for the options of a command. 'a2a --version' shows the version.");
        sb.AppendLine("Every command except keys needs --agent <url> (or the env variable A2A_AGENT_URL).");
        sb.AppendLine();
        sb.Append(ExitCodes.HelpText);
        return sb.ToString();
    }

    public static string CommandHelp(CommandSpec spec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Usage: a2a {spec.Name} {spec.Usage}".TrimEnd());
        sb.AppendLine(spec.Summary);
        var shared = new HashSet<OptionSpec>(Shared.Concat(Auth).Concat(Trust));
        Section(sb, "Options:", spec.Options.Where(o => !shared.Contains(o)));
        Section(sb, "Connection options:", Shared);
        Section(sb, "Credentials (a credential is sent only when the card asks for one):", Auth);
        Section(sb, "Agent Card trust:", Trust);
        sb.AppendLine("Options may come anywhere after the command. Use -- before text that starts with a dash.");
        sb.AppendLine();
        sb.Append(ExitCodes.HelpText);
        return sb.ToString();
    }

    private static void Section(StringBuilder sb, string title, IEnumerable<OptionSpec> options)
    {
        var list = options.ToList();
        if (list.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine(title);
        foreach (var o in list)
        {
            var left = (o.Short is { } s ? $"-{s}, " : "    ") + "--" + o.Name +
                       (o.Metavar is null ? "" : $" <{o.Metavar}>");
            sb.AppendLine($"  {left,-34} {o.Help}");
        }

        sb.AppendLine();
    }
}