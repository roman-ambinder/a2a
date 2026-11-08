namespace A2A.Server.AspNetCore;

/// <summary>Options for <c>MapA2A(pattern, configure)</c>.</summary>
public sealed class A2AEndpointOptions
{
    /// <summary>
    /// True (the default) to also map the public Agent Card at <c>/.well-known/agent-card.json</c> (and the JWKS when a signing
    /// key is set). The card is anonymous and is never inside the group <c>MapA2A</c> returns. Set false when the host maps the
    /// card elsewhere.
    /// </summary>
    public bool MapAgentCard { get; set; } = true;

    /// <summary>The name of an ASP.NET Core authorization policy to require on the A2A endpoints (not on the card). Null: none.</summary>
    public string? AuthorizationPolicy { get; set; }
}
