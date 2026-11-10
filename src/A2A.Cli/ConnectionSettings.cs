using System.Globalization;

namespace A2A.Cli;

/// <summary>The shared options of every command, with the environment fallbacks applied.</summary>
internal sealed record ConnectionSettings
{
    public const string AgentUrlVariable = "A2A_AGENT_URL";
    public const string TokenVariable = "A2A_TOKEN";
    public const string ApiKeyVariable = "A2A_API_KEY";
    public const string ClientIdVariable = "A2A_CLIENT_ID";
    public const string ClientSecretVariable = "A2A_CLIENT_SECRET";

    public required Uri Agent { get; init; }
    public bool Json { get; init; }
    public TimeSpan? Timeout { get; init; }
    public int? Retries { get; init; }
    public string? Tenant { get; init; }
    public string? Transport { get; init; }
    public bool AllowHttp { get; init; }
    public string? Token { get; init; }
    public string? ApiKey { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public Uri? TokenEndpoint { get; init; }
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public string? TrustJwksPath { get; init; }
    public IReadOnlyList<string> JkuHosts { get; init; } = [];
    public bool RequireSignature { get; init; }

    public bool HasCredential => Token is not null || ApiKey is not null || ClientId is not null;

    public IEnumerable<string?> Secrets => [Token, ApiKey, ClientSecret];

    /// <summary>Reads the shared options. An option beats the environment variable.</summary>
    public static ConnectionSettings From(ParsedArgs a, Func<string, string?> getEnv)
    {
        string? Pick(string option, string variable)
        {
            return a.Value(option) ?? (getEnv(variable) is { Length: > 0 } v ? v : null);
        }

        var agentText = Pick("agent", AgentUrlVariable)
                        ?? throw CliException.Usage($"No agent URL. Use --agent or set {AgentUrlVariable}.");
        var agent = ParseUrl(agentText, "--agent");

        var transport = a.Value("transport")?.ToLowerInvariant();
        if (transport is not (null or "jsonrpc" or "rest"))
            throw CliException.Usage("Option '--transport' must be 'jsonrpc' or 'rest'.");

        var token = Pick("token", TokenVariable);
        if (token is not null && !StaticBearerCredentialProvider.IsValid(token))
            throw CliException.Usage("The token has control characters.");
        var apiKey = Pick("api-key", ApiKeyVariable);
        var clientId = Pick("client-id", ClientIdVariable);
        var clientSecret = Pick("client-secret", ClientSecretVariable);
        if (clientId is null != clientSecret is null)
            throw CliException.Usage("--client-id and --client-secret must be given together.");

        if (a.Values("trust-jku-host").Any(string.IsNullOrWhiteSpace))
            throw CliException.Usage("Option '--trust-jku-host' needs a host name.");

        return new ConnectionSettings
        {
            Agent = agent,
            Json = a.Has("json"),
            Timeout = a.Seconds("timeout") is { } s ? TimeSpan.FromSeconds(s) : null,
            Retries = a.Int("retries", 0),
            Tenant = a.Value("tenant"),
            Transport = transport,
            AllowHttp = a.Has("allow-http"),
            Token = token,
            ApiKey = apiKey,
            ClientId = clientId,
            ClientSecret = clientSecret,
            TokenEndpoint = a.Value("token-endpoint") is { } te ? ParseUrl(te, "--token-endpoint") : null,
            Scopes = a.Values("scope"),
            TrustJwksPath = a.Value("trust-jwks"),
            JkuHosts = a.Values("trust-jku-host"),
            RequireSignature = a.Has("require-signature")
        };
    }

    // The text of a bad URL is never repeated: it may hold a password.
    private static Uri ParseUrl(string text, string option)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw CliException.Usage($"Option '{option}' needs an absolute http or https URL.");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            throw CliException.Usage($"Option '{option}' must not have a user name or password in the URL.");
        return uri;
    }

    public static string FormatSeconds(TimeSpan t)
    {
        return t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
    }
}