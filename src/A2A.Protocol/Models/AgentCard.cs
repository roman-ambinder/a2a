using System.Text.Json;
using System.Text.Json.Serialization;

namespace A2A;

/// <summary>Self-describing manifest of an agent (spec §4.4.1).</summary>
public sealed class AgentCard
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    /// <summary>Ordered list of interfaces; the first entry is preferred.</summary>
    public List<AgentInterface> SupportedInterfaces { get; set; } = new();

    public AgentProvider? Provider { get; set; }
    public string Version { get; set; } = string.Empty;
    public string? DocumentationUrl { get; set; }
    public AgentCapabilities Capabilities { get; set; } = new();
    public Dictionary<string, SecurityScheme>? SecuritySchemes { get; set; }
    public List<SecurityRequirement>? SecurityRequirements { get; set; }
    public List<string> DefaultInputModes { get; set; } = new();
    public List<string> DefaultOutputModes { get; set; } = new();
    public List<AgentSkill> Skills { get; set; } = new();
    public List<AgentCardSignature>? Signatures { get; set; }
    public string? IconUrl { get; set; }
}

/// <summary>Well-known protocol binding identifiers.</summary>
public static class ProtocolBindings
{
    public const string JsonRpc = "JSONRPC";
    public const string Grpc = "GRPC";
    public const string HttpJson = "HTTP+JSON";
}

/// <summary>A URL + binding + protocol version combination (spec §4.4.6).</summary>
public sealed class AgentInterface
{
    public string Url { get; set; } = string.Empty;
    public string ProtocolBinding { get; set; } = string.Empty;

    /// <summary>Opaque routing value. When set, clients send it as <c>tenant</c> on every request.</summary>
    public string? Tenant { get; set; }

    public string ProtocolVersion { get; set; } = A2AProtocol.Version;
}

public sealed class AgentProvider
{
    public string Url { get; set; } = string.Empty;
    public string Organization { get; set; } = string.Empty;
}

/// <summary>Optional features the agent supports (spec §4.4.3). Unset means "not supported".</summary>
public sealed class AgentCapabilities
{
    public bool? Streaming { get; set; }
    public bool? PushNotifications { get; set; }
    public List<AgentExtension>? Extensions { get; set; }
    public bool? ExtendedAgentCard { get; set; }
}

/// <summary>A protocol extension the agent supports (spec §4.4.4).</summary>
public sealed class AgentExtension
{
    public string Uri { get; set; } = string.Empty;
    public string? Description { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Required { get; set; }

    public Dictionary<string, JsonElement>? Params { get; set; }
}

public sealed class AgentSkill
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = new();
    public List<string>? Examples { get; set; }
    public List<string>? InputModes { get; set; }
    public List<string>? OutputModes { get; set; }
    public List<SecurityRequirement>? SecurityRequirements { get; set; }
}

/// <summary>JWS signature over the canonical Agent Card (spec §4.4.7, §8.4).</summary>
public sealed class AgentCardSignature
{
    /// <summary>Base64url-encoded protected header.</summary>
    public string Protected { get; set; } = string.Empty;

    /// <summary>Base64url-encoded signature.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>Unprotected header. Not covered by the signature and ignored by the verifier.</summary>
    public Dictionary<string, JsonElement>? Header { get; set; }
}

public sealed class StringList
{
    public List<string> List { get; set; } = new();
}

/// <summary>Map of security scheme name to required scopes.</summary>
public sealed class SecurityRequirement
{
    public Dictionary<string, StringList> Schemes { get; set; } = new();
}

/// <summary>Discriminated union of security schemes (OpenAPI 3.2 style). Exactly one member is set.</summary>
public sealed class SecurityScheme
{
    public ApiKeySecurityScheme? ApiKeySecurityScheme { get; set; }
    public HttpAuthSecurityScheme? HttpAuthSecurityScheme { get; set; }
    public OAuth2SecurityScheme? Oauth2SecurityScheme { get; set; }
    public OpenIdConnectSecurityScheme? OpenIdConnectSecurityScheme { get; set; }
    public MutualTlsSecurityScheme? MtlsSecurityScheme { get; set; }

    public static SecurityScheme Bearer(string? bearerFormat = "JWT", string? description = null) =>
        new() { HttpAuthSecurityScheme = new HttpAuthSecurityScheme { Scheme = "Bearer", BearerFormat = bearerFormat, Description = description } };

    public static SecurityScheme ApiKey(string name, string location = "header", string? description = null) =>
        new() { ApiKeySecurityScheme = new ApiKeySecurityScheme { Name = name, Location = location, Description = description } };
}

public sealed class ApiKeySecurityScheme
{
    public string? Description { get; set; }

    /// <summary>"query", "header", or "cookie".</summary>
    public string Location { get; set; } = "header";

    public string Name { get; set; } = string.Empty;
}

public sealed class HttpAuthSecurityScheme
{
    public string? Description { get; set; }
    public string Scheme { get; set; } = "Bearer";
    public string? BearerFormat { get; set; }
}

public sealed class OAuth2SecurityScheme
{
    public string? Description { get; set; }
    public OAuthFlows Flows { get; set; } = new();
    public string? Oauth2MetadataUrl { get; set; }
}

public sealed class OpenIdConnectSecurityScheme
{
    public string? Description { get; set; }
    public string OpenIdConnectUrl { get; set; } = string.Empty;
}

public sealed class MutualTlsSecurityScheme
{
    public string? Description { get; set; }
}

/// <summary>Union of OAuth flows. Exactly one member is set.</summary>
public sealed class OAuthFlows
{
    public AuthorizationCodeOAuthFlow? AuthorizationCode { get; set; }
    public ClientCredentialsOAuthFlow? ClientCredentials { get; set; }

    [Obsolete("Deprecated by the A2A spec. Use authorization code + PKCE.")]
    public ImplicitOAuthFlow? Implicit { get; set; }

    [Obsolete("Deprecated by the A2A spec. Use authorization code + PKCE or device code.")]
    public PasswordOAuthFlow? Password { get; set; }

    public DeviceCodeOAuthFlow? DeviceCode { get; set; }
}

public sealed class AuthorizationCodeOAuthFlow
{
    public string AuthorizationUrl { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string? RefreshUrl { get; set; }
    public Dictionary<string, string> Scopes { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PkceRequired { get; set; }
}

public sealed class ClientCredentialsOAuthFlow
{
    public string TokenUrl { get; set; } = string.Empty;
    public string? RefreshUrl { get; set; }
    public Dictionary<string, string> Scopes { get; set; } = new();
}

public sealed class ImplicitOAuthFlow
{
    public string? AuthorizationUrl { get; set; }
    public string? RefreshUrl { get; set; }
    public Dictionary<string, string>? Scopes { get; set; }
}

public sealed class PasswordOAuthFlow
{
    public string? TokenUrl { get; set; }
    public string? RefreshUrl { get; set; }
    public Dictionary<string, string>? Scopes { get; set; }
}

public sealed class DeviceCodeOAuthFlow
{
    public string DeviceAuthorizationUrl { get; set; } = string.Empty;
    public string TokenUrl { get; set; } = string.Empty;
    public string? RefreshUrl { get; set; }
    public Dictionary<string, string> Scopes { get; set; } = new();
}
