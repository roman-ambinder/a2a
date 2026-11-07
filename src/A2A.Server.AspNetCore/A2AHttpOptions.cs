using A2A.Signing;

namespace A2A.Server.AspNetCore;

/// <summary>HTTP-specific settings.</summary>
public sealed class A2AHttpOptions
{
    /// <summary>When set, the served Agent Card is signed with this key and its public key is published at <see cref="JwksPath"/>.</summary>
    public AgentCardSigningKey? SigningKey { get; set; }

    /// <summary>Path where the public key set of <see cref="SigningKey"/> is served. Default <c>/.well-known/jwks.json</c>.</summary>
    public string JwksPath { get; set; } = "/.well-known/jwks.json";

    /// <summary>Cache-Control max-age for the Agent Card. Default 5 minutes.</summary>
    public TimeSpan AgentCardMaxAge { get; set; } = TimeSpan.FromMinutes(5);
}
