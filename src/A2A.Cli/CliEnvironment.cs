namespace A2A.Cli;

/// <summary>Everything the tool takes from the outside world, so tests can replace it.</summary>
/// <param name="Out">Standard output.</param>
/// <param name="Error">Standard error.</param>
/// <param name="In">Standard input.</param>
/// <param name="GetEnvironmentVariable">Reads an environment variable.</param>
internal sealed record CliEnvironment(TextWriter Out, TextWriter Error, TextReader In, Func<string, string?> GetEnvironmentVariable)
{
    /// <summary>Test seam: the handler for all agent requests. Default: a <see cref="SocketsHttpHandler"/> that does not follow redirects.</summary>
    public HttpMessageHandler? Handler { get; init; }

    /// <summary>Test seam: the handler for OAuth2 token requests.</summary>
    public HttpMessageHandler? OAuthTokenHandler { get; init; }
}
