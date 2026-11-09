using A2A.Client;

namespace A2A.Cli;

/// <summary>What a command needs to run.</summary>
internal sealed record CommandContext(ParsedArgs Args, ConnectionSettings Settings, CliEnvironment Env, Output Out, AgentConnection Connection)
{
    public A2AClient Client => Connection.Client ?? throw new InvalidOperationException("The command has no client.");

    /// <summary>Options for a unary call: the --timeout limit for each attempt.</summary>
    public A2ARequestOptions RequestOptions() => new() { Timeout = Settings.Timeout ?? A2ARequestOptions.DefaultTimeout };

    /// <summary>A token that also ends after --timeout, for commands that run longer (streams, downloads). No limit unless the option is given.</summary>
    public CancellationTokenSource OverallTimeout(CancellationToken cancellationToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (Settings.Timeout is { } t) cts.CancelAfter(t);
        return cts;
    }
}
