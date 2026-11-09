using A2A.Client;
using ModelContextProtocol.Server;

namespace A2A.Mcp;

/// <summary>Serve a remote A2A agent through MCP.</summary>
public static class A2AClientMcpExtensions
{
    private const string GetTaskName = "get_task";
    private const string CancelTaskName = "cancel_task";

    /// <summary>
    /// Makes MCP tools from the skills of a remote A2A agent, for any MCP server (for example <c>WithTools(tools)</c>). This is a
    /// snapshot of the card: skills added later need a new call. Each skill becomes a tool that takes <c>message</c> (and optional
    /// <c>contextId</c> and <c>taskId</c>). With <see cref="A2AMcpToolOptions.IncludeTaskTools"/>, <c>get_task</c> and <c>cancel_task</c>
    /// are added.
    /// </summary>
    /// <param name="client">The client for the remote agent. Its credentials are the only ones used. Nothing from the MCP caller is forwarded.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="card">The card to read skills from. Null means <see cref="A2AClient.AgentCard"/>.</param>
    /// <exception cref="InvalidOperationException">There is no card: pass one, or create the client with one.</exception>
    public static IReadOnlyList<McpServerTool> AsMcpTools(this A2AClient client, A2AMcpToolOptions? options = null, AgentCard? card = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        options ??= new A2AMcpToolOptions();
        card ??= client.AgentCard ?? throw new InvalidOperationException("There is no Agent Card. Pass one, or create the A2AClient with one.");

        var prefix = options.ToolNamePrefix;
        var getTaskName = prefix + GetTaskName;
        var cancelTaskName = prefix + CancelTaskName;
        var bridge = new A2AMcpBridge(client, options, options.IncludeTaskTools ? getTaskName : null);

        // The task tool names are taken first, so a skill with the same id gets a suffix.
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (options.IncludeTaskTools)
        {
            used.Add(getTaskName);
            used.Add(cancelTaskName);
        }

        var tools = new List<McpServerTool>();
        foreach (var skill in card.Skills ?? [])
        {
            if (string.IsNullOrEmpty(skill.Id)) continue;
            if (options.SkillFilter is { } filter && !filter(skill)) continue;
            var name = UniqueName(prefix, skill.Id, used);
            var description = options.SkillDescription?.Invoke(skill) ?? A2AMcpToolOptions.DefaultSkillDescription;
            tools.Add(new A2AMcpTool(bridge, A2AMcpToolKind.Skill, name, description, skill.Id));
        }

        if (options.IncludeTaskTools)
        {
            tools.Add(new A2AMcpTool(bridge, A2AMcpToolKind.GetTask, getTaskName,
                "Gets the current state and result of a task that an earlier tool call started. Pass the taskId from that result."));
            tools.Add(new A2AMcpTool(bridge, A2AMcpToolKind.CancelTask, cancelTaskName,
                "Asks the remote agent to cancel a task that an earlier tool call started. Pass the taskId from that result."));
        }
        return tools;
    }

    private static string UniqueName(string prefix, string skillId, HashSet<string> used)
    {
        var name = A2AMcpNames.ToolName(prefix, skillId);
        if (used.Add(name)) return name;
        for (var n = 2; ; n++)
        {
            var suffix = "_" + n;
            var candidate = A2AMcpNames.ToolName(prefix, skillId, suffix.Length) + suffix;
            if (used.Add(candidate)) return candidate;
        }
    }
}
