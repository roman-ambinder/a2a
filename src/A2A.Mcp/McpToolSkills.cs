using ModelContextProtocol.Client;

namespace A2A.Mcp;

/// <summary>Describes MCP tools as A2A skills, for the Agent Card of an agent that runs them.</summary>
public static class McpToolSkills
{
    /// <summary>
    ///     The description of a skill when no <c>describe</c> callback is given. It is fixed text, because an MCP tool
    ///     description is remote text.
    /// </summary>
    public const string DefaultDescription = "Runs a tool of an MCP server. Send the arguments as one JSON data part.";

    /// <summary>
    ///     Makes one skill per tool: the id and name are the tool name, the description is <see cref="DefaultDescription" />
    ///     unless
    ///     <paramref name="describe" /> is given, the tag is <c>mcp</c> and the input mode is <c>application/json</c>. Only
    ///     the tools you
    ///     pass are listed, so filter to the allowlist of <see cref="McpToolAgentOptions" /> first.
    /// </summary>
    /// <param name="tools">The tools to list.</param>
    /// <param name="describe">
    ///     Returns the description for a tool. Use <c>t =&gt; t.Description</c> only for a server you
    ///     trust.
    /// </param>
    public static IReadOnlyList<AgentSkill> ToAgentSkills(IEnumerable<McpClientTool> tools,
        Func<McpClientTool, string>? describe = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var skills = new List<AgentSkill>();
        foreach (var tool in tools)
            skills.Add(new AgentSkill
            {
                Id = tool.Name,
                Name = tool.Name,
                Description = describe?.Invoke(tool) ?? DefaultDescription,
                Tags = ["mcp"],
                InputModes = ["application/json"]
            });
        return skills;
    }
}