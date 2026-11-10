namespace A2A.Mcp;

/// <summary>Options for <see cref="McpToolAgentExecutor" />.</summary>
public sealed class McpToolAgentOptions
{
    private readonly Dictionary<string, string?> _allowed = new(StringComparer.Ordinal);
    private string _artifactName = "result";
    private TimeSpan _progressInterval = TimeSpan.FromSeconds(1);
    private TimeSpan _toolTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     The tool that runs when the caller selects no skill. With one allowed tool, that tool is the default. Default
    ///     null.
    /// </summary>
    public string? DefaultTool { get; set; }

    /// <summary>
    ///     Time limit for one tool call. Default 5 minutes. <see cref="System.Threading.Timeout.InfiniteTimeSpan" />
    ///     disables it. On timeout the task fails and the MCP call is canceled.
    /// </summary>
    public TimeSpan ToolTimeout
    {
        get => _toolTimeout;
        set
        {
            if (value != Timeout.InfiniteTimeSpan)
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _toolTimeout = value;
        }
    }

    /// <summary>The name of the artifact that holds the tool result. Default <c>result</c>.</summary>
    public string ArtifactName
    {
        get => _artifactName;
        set => _artifactName = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    ///     The least time between two progress updates sent to the A2A caller. At least 100 ms. Default 1 second. Durable
    ///     stores write on
    ///     every event, so progress is throttled. <see cref="System.Threading.Timeout.InfiniteTimeSpan" /> turns progress
    ///     forwarding off.
    /// </summary>
    public TimeSpan ProgressInterval
    {
        get => _progressInterval;
        set
        {
            if (value != Timeout.InfiniteTimeSpan)
                ArgumentOutOfRangeException.ThrowIfLessThan(value, TimeSpan.FromMilliseconds(100));
            _progressInterval = value;
        }
    }

    /// <summary>
    ///     Copy the text of an MCP tool error into the failed status. Default false: a fixed text is sent and the detail
    ///     is logged. The text comes from the MCP server and can hold internal details.
    /// </summary>
    public bool IncludeToolErrorText { get; set; }

    internal IReadOnlyDictionary<string, string?> AllowedTools => _allowed;

    /// <summary>
    ///     Allows an MCP tool to be run. Only allowed tools run: the list is the allowlist. <paramref name="textArgument" />
    ///     names the
    ///     argument of the tool that receives plain text, so a caller can send text instead of a JSON object. Calling it again
    ///     for the same tool replaces the earlier entry.
    /// </summary>
    /// <param name="name">The tool name. Not empty.</param>
    /// <param name="textArgument">The argument that gets plain text, or null when the tool only takes a JSON object.</param>
    /// <returns>These options, for chaining.</returns>
    public McpToolAgentOptions AllowTool(string name, string? textArgument = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (textArgument is not null) ArgumentException.ThrowIfNullOrWhiteSpace(textArgument);
        _allowed[name] = textArgument;
        return this;
    }

    internal void ThrowIfNoTools()
    {
        if (_allowed.Count == 0)
            throw new ArgumentException("No tool is allowed. Call AllowTool at least once.", "options");
    }
}