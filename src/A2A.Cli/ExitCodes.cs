namespace A2A.Cli;

/// <summary>Process exit codes of the <c>a2a</c> tool. They are part of the documented interface.</summary>
internal static class ExitCodes
{
    /// <summary>The command worked.</summary>
    public const int Success = 0;

    /// <summary>An unexpected or local error, such as a file that already exists.</summary>
    public const int Error = 1;

    /// <summary>The command line is wrong.</summary>
    public const int Usage = 2;

    /// <summary>The network failed, the connection failed, or <c>--timeout</c> ran out.</summary>
    public const int Network = 3;

    /// <summary>The agent refused the credentials, or wants credentials the tool does not have.</summary>
    public const int Auth = 4;

    /// <summary>The task, or the artifact file, does not exist.</summary>
    public const int NotFound = 5;

    /// <summary>The agent answered with another A2A or protocol error.</summary>
    public const int Protocol = 6;

    /// <summary>The Agent Card failed verification or its pin check.</summary>
    public const int CardVerification = 7;

    /// <summary>An artifact download failed its hash, size or origin check.</summary>
    public const int Integrity = 8;

    /// <summary>The task ended failed, rejected or canceled.</summary>
    public const int TaskFailed = 9;

    /// <summary>The task is waiting for input or authorization.</summary>
    public const int TaskInterrupted = 10;

    /// <summary>The user cancelled with Ctrl+C.</summary>
    public const int Cancelled = 130;

    /// <summary>The table shown by <c>--help</c>.</summary>
    public const string HelpText = """
                                   Exit codes:
                                     0    success
                                     1    unexpected or local error (for example a file already exists)
                                     2    usage error
                                     3    network, connection or timeout error
                                     4    authentication or authorization error
                                     5    not found (task, or no matching artifact)
                                     6    other A2A or protocol error from the agent
                                     7    Agent Card verification or pin failure
                                     8    artifact integrity, size or origin failure
                                     9    task ended failed, rejected or canceled (send, stream, subscribe)
                                     10   task is waiting for input or authorization (send, stream, subscribe)
                                     130  cancelled with Ctrl+C
                                   """;

    /// <summary>The exit code for a task state seen at the end of send, stream or subscribe.</summary>
    public static int ForTaskState(TaskState? state)
    {
        return state switch
        {
            TaskState.Failed or TaskState.Rejected or TaskState.Canceled => TaskFailed,
            TaskState.InputRequired or TaskState.AuthRequired => TaskInterrupted,
            _ => Success
        };
    }
}

/// <summary>An error with a chosen exit code and a message that is safe to show.</summary>
internal sealed class CliException(int exitCode, string message) : Exception(message)
{
    /// <summary>The exit code to return.</summary>
    public int ExitCode { get; } = exitCode;

    /// <summary>A usage error (exit code 2).</summary>
    public static CliException Usage(string message)
    {
        return new CliException(ExitCodes.Usage, message);
    }
}