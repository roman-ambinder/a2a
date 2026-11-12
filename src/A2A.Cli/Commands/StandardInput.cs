namespace A2A.Cli.Commands;

/// <summary>Reads standard input so that Ctrl+C stops the wait.</summary>
internal static class StandardInput
{
    /// <summary>
    ///     Reads all of <paramref name="reader" />. <c>Console.In</c> ignores the token and blocks even in its async read, so
    ///     the read runs on its own thread and the wait races <paramref name="cancellationToken" />: the first Ctrl+C ends the
    ///     command (exit 130) while the abandoned read stays blocked until the process exits.
    /// </summary>
    public static Task<string> ReadToEndAsync(TextReader reader, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(reader.ReadToEnd, CancellationToken.None).WaitAsync(cancellationToken);
    }
}
