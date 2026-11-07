using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Json;

namespace A2A;

/// <summary>Self-describing manifest of an agent (spec §4.4.1).</summary>
public sealed class AgentCard : IJsonOnDeserialized
{
    /// <summary>Human-readable name of the agent.</summary>
    public required string Name { get; set; } = string.Empty;
    /// <summary>What the agent does, for people choosing an agent.</summary>
    public required string Description { get; set; } = string.Empty;

    /// <summary>Ordered list of interfaces; the first entry is preferred.</summary>
    public required List<AgentInterface> SupportedInterfaces { get; set; } = new();

    /// <summary>The organization that runs the agent. Null when not stated.</summary>
    public AgentProvider? Provider { get; set; }
    /// <summary>Version of the agent, as the operator defines it. Not the protocol version.</summary>
    public required string Version { get; set; } = string.Empty;
    /// <summary>URL of documentation for the agent. Null when none.</summary>
    public string? DocumentationUrl { get; set; }
    /// <summary>Optional features the agent supports.</summary>
    public required AgentCapabilities Capabilities { get; set; } = new();
    /// <summary>Security schemes the agent accepts, by name. Null when the agent needs no authentication.</summary>
    public Dictionary<string, SecurityScheme>? SecuritySchemes { get; set; }
    /// <summary>Alternative sets of schemes that grant access to the agent. A caller satisfies any one entry. Null when none.</summary>
    public List<SecurityRequirement>? SecurityRequirements { get; set; }
    /// <summary>Media types the agent accepts as input when a skill does not say otherwise.</summary>
    public required List<string> DefaultInputModes { get; set; } = new();
    /// <summary>Media types the agent produces when a skill does not say otherwise.</summary>
    public required List<string> DefaultOutputModes { get; set; } = new();
    /// <summary>Skills the agent offers.</summary>
    public required List<AgentSkill> Skills { get; set; } = new();
    /// <summary>JWS signatures over the card. Null when the card is unsigned.</summary>
    public List<AgentCardSignature>? Signatures { get; set; }
    /// <summary>URL of an icon for the agent. Null when none.</summary>
    public string? IconUrl { get; set; }

    // The generated metadata builds objects from constructor arguments, so a member the peer omitted (see A2AJson.RelaxRequired)
    // arrives as null instead of keeping its property initializer. Restore the defaults.
    void IJsonOnDeserialized.OnDeserialized()
    {
        Name ??= string.Empty;
        Description ??= string.Empty;
        SupportedInterfaces ??= new();
        Version ??= string.Empty;
        Capabilities ??= new();
        DefaultInputModes ??= new();
        DefaultOutputModes ??= new();
        Skills ??= new();
    }
}

/// <summary>Well-known protocol binding identifiers.</summary>
public static class ProtocolBindings
{
    /// <summary>JSON-RPC 2.0 over HTTP.</summary>
    public const string JsonRpc = "JSONRPC";
    /// <summary>gRPC.</summary>
    public const string Grpc = "GRPC";
    /// <summary>HTTP with JSON bodies (REST-style).</summary>
    public const string HttpJson = "HTTP+JSON";
}

/// <summary>A URL + binding + protocol version combination (spec §4.4.6).</summary>
public sealed class AgentInterface
{
    /// <summary>Absolute URL of the endpoint for this binding.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Binding of the endpoint. One of the values in <see cref="ProtocolBindings"/>.</summary>
    public string ProtocolBinding { get; set; } = string.Empty;

    /// <summary>Opaque routing value. When set, clients send it as <c>tenant</c> on every request.</summary>
    public string? Tenant { get; set; }

    /// <summary>Protocol version the endpoint speaks. Defaults to <see cref="A2AProtocol.Version"/>.</summary>
    public string ProtocolVersion { get; set; } = A2AProtocol.Version;
}

/// <summary>The organization that provides an agent (spec §4.4.2).</summary>
public sealed class AgentProvider
{
    /// <summary>Website of the organization.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Name of the organization.</summary>
    public string Organization { get; set; } = string.Empty;
}

/// <summary>Optional features the agent supports (spec §4.4.3). Unset means "not supported".</summary>
public sealed class AgentCapabilities
{
    /// <summary>True if the agent supports streaming. Null means not stated.</summary>
    public bool? Streaming { get; set; }
    /// <summary>True if the agent supports push notifications. Null means not stated.</summary>
    public bool? PushNotifications { get; set; }
    /// <summary>Extensions the agent supports. Null when none.</summary>
    public List<AgentExtension>? Extensions { get; set; }
    /// <summary>True if the agent serves an extended card to authenticated callers. Null means not stated.</summary>
    public bool? ExtendedAgentCard { get; set; }
}

/// <summary>A protocol extension the agent supports (spec §4.4.4).</summary>
public sealed class AgentExtension
{
    /// <summary>URI that identifies the extension.</summary>
    public string Uri { get; set; } = string.Empty;
    /// <summary>What the extension does. Null when not stated.</summary>
    public string? Description { get; set; }

    /// <summary>True if clients must support this extension to talk to the agent. Defaults to false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Required { get; set; }

    /// <summary>Extension-specific settings. Null when none.</summary>
    public Dictionary<string, JsonElement>? Params { get; set; }
}

/// <summary>A capability the agent advertises (spec §4.4.5).</summary>
public sealed class AgentSkill
{
    /// <summary>Identifier of the skill, unique within the card.</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>Human-readable name of the skill.</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>What the skill does.</summary>
    public string Description { get; set; } = string.Empty;
    /// <summary>Keywords that describe the skill. Empty by default.</summary>
    public List<string> Tags { get; set; } = new();
    /// <summary>Example prompts that show how to use the skill. Null when none.</summary>
    public List<string>? Examples { get; set; }
    /// <summary>Input media types for this skill. Null means use the card's defaults.</summary>
    public List<string>? InputModes { get; set; }
    /// <summary>Output media types for this skill. Null means use the card's defaults.</summary>
    public List<string>? OutputModes { get; set; }
    /// <summary>Security requirements for this skill. Null means use the card's requirements.</summary>
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

/// <summary>A list of strings. Wrapper used as the value type of <see cref="SecurityRequirement.Schemes"/>.</summary>
public sealed class StringList
{
    /// <summary>The strings, for example the required OAuth scopes. Empty by default.</summary>
    public List<string> List { get; set; } = new();
}

/// <summary>Map of security scheme name to required scopes.</summary>
public sealed class SecurityRequirement
{
    /// <summary>Required scopes by scheme name. An empty list means the scheme needs no scopes.</summary>
    public Dictionary<string, StringList> Schemes { get; set; } = new();
}

/// <summary>Discriminated union of security schemes (OpenAPI 3.2 style). A closed union; exactly one case.</summary>
[JsonConverter(typeof(SecuritySchemeConverter))]
public abstract record SecurityScheme
{
    private protected SecurityScheme() { }

    /// <summary>Creates an HTTP bearer scheme. <paramref name="bearerFormat"/> defaults to <c>"JWT"</c>.</summary>
    public static HttpAuthScheme Bearer(string? bearerFormat = "JWT", string? description = null) =>
        new(new HttpAuthSecurityScheme { Scheme = "Bearer", BearerFormat = bearerFormat, Description = description });

    /// <summary>Creates an API key scheme. <paramref name="location"/> is <c>"header"</c>, <c>"query"</c> or <c>"cookie"</c>; it defaults to <c>"header"</c>.</summary>
    public static ApiKeyScheme ApiKey(string name, string location = "header", string? description = null) =>
        new(new ApiKeySecurityScheme { Name = name, Location = location, Description = description });
}

/// <summary>API key scheme (wire member <c>apiKeySecurityScheme</c>).</summary>
[JsonConverter(typeof(OneOfCaseConverter<SecurityScheme, ApiKeyScheme, SecuritySchemeConverter>))]
public sealed record ApiKeyScheme(ApiKeySecurityScheme Value) : SecurityScheme;

/// <summary>HTTP auth scheme (wire member <c>httpAuthSecurityScheme</c>).</summary>
[JsonConverter(typeof(OneOfCaseConverter<SecurityScheme, HttpAuthScheme, SecuritySchemeConverter>))]
public sealed record HttpAuthScheme(HttpAuthSecurityScheme Value) : SecurityScheme;

/// <summary>OAuth 2.0 scheme (wire member <c>oauth2SecurityScheme</c>).</summary>
[JsonConverter(typeof(OneOfCaseConverter<SecurityScheme, OAuth2Scheme, SecuritySchemeConverter>))]
public sealed record OAuth2Scheme(OAuth2SecurityScheme Value) : SecurityScheme;

/// <summary>OpenID Connect scheme (wire member <c>openIdConnectSecurityScheme</c>).</summary>
[JsonConverter(typeof(OneOfCaseConverter<SecurityScheme, OpenIdConnectScheme, SecuritySchemeConverter>))]
public sealed record OpenIdConnectScheme(OpenIdConnectSecurityScheme Value) : SecurityScheme;

/// <summary>Mutual TLS scheme (wire member <c>mtlsSecurityScheme</c>).</summary>
[JsonConverter(typeof(OneOfCaseConverter<SecurityScheme, MutualTlsScheme, SecuritySchemeConverter>))]
public sealed record MutualTlsScheme(MutualTlsSecurityScheme Value) : SecurityScheme;

/// <summary>Settings of an API key security scheme.</summary>
public sealed class ApiKeySecurityScheme
{
    /// <summary>What the scheme is for. Null when not stated.</summary>
    public string? Description { get; set; }

    /// <summary>"query", "header", or "cookie".</summary>
    public string Location { get; set; } = "header";

    /// <summary>Name of the header, query parameter or cookie that carries the key.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>Settings of an HTTP authentication security scheme.</summary>
public sealed class HttpAuthSecurityScheme
{
    /// <summary>What the scheme is for. Null when not stated.</summary>
    public string? Description { get; set; }
    /// <summary>HTTP auth scheme name from the IANA registry. Defaults to <c>"Bearer"</c>.</summary>
    public string Scheme { get; set; } = "Bearer";
    /// <summary>Hint about the token format, for example <c>"JWT"</c>. Null when not stated.</summary>
    public string? BearerFormat { get; set; }
}

/// <summary>Settings of an OAuth 2.0 security scheme.</summary>
public sealed class OAuth2SecurityScheme
{
    /// <summary>What the scheme is for. Null when not stated.</summary>
    public string? Description { get; set; }
    /// <summary>The OAuth flows the agent supports.</summary>
    public OAuthFlows Flows { get; set; } = new();
    /// <summary>URL of the authorization server metadata (RFC 8414). Null when not stated.</summary>
    public string? Oauth2MetadataUrl { get; set; }
}

/// <summary>Settings of an OpenID Connect security scheme.</summary>
public sealed class OpenIdConnectSecurityScheme
{
    /// <summary>What the scheme is for. Null when not stated.</summary>
    public string? Description { get; set; }
    /// <summary>URL of the OpenID Connect discovery document.</summary>
    public string OpenIdConnectUrl { get; set; } = string.Empty;
}

/// <summary>Settings of a mutual TLS security scheme.</summary>
public sealed class MutualTlsSecurityScheme
{
    /// <summary>What the scheme is for. Null when not stated.</summary>
    public string? Description { get; set; }
}

/// <summary>Union of OAuth flows. Exactly one member is set.</summary>
public sealed class OAuthFlows
{
    /// <summary>Authorization code flow.</summary>
    public AuthorizationCodeOAuthFlow? AuthorizationCode { get; set; }
    /// <summary>Client credentials flow.</summary>
    public ClientCredentialsOAuthFlow? ClientCredentials { get; set; }

    /// <summary>Implicit flow. Deprecated by the spec.</summary>
    [Obsolete("Deprecated by the A2A spec. Use authorization code + PKCE.")]
    public ImplicitOAuthFlow? Implicit { get; set; }

    /// <summary>Resource owner password flow. Deprecated by the spec.</summary>
    [Obsolete("Deprecated by the A2A spec. Use authorization code + PKCE or device code.")]
    public PasswordOAuthFlow? Password { get; set; }

    /// <summary>Device code flow (RFC 8628).</summary>
    public DeviceCodeOAuthFlow? DeviceCode { get; set; }
}

/// <summary>OAuth 2.0 authorization code flow.</summary>
public sealed class AuthorizationCodeOAuthFlow
{
    /// <summary>URL of the authorization endpoint.</summary>
    public string AuthorizationUrl { get; set; } = string.Empty;
    /// <summary>URL of the token endpoint.</summary>
    public string TokenUrl { get; set; } = string.Empty;
    /// <summary>URL used to refresh tokens. Null when not stated.</summary>
    public string? RefreshUrl { get; set; }
    /// <summary>Available scopes, as scope name to description.</summary>
    public Dictionary<string, string> Scopes { get; set; } = new();

    /// <summary>True if the flow requires PKCE. Defaults to false.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PkceRequired { get; set; }
}

/// <summary>OAuth 2.0 client credentials flow.</summary>
public sealed class ClientCredentialsOAuthFlow
{
    /// <summary>URL of the token endpoint.</summary>
    public string TokenUrl { get; set; } = string.Empty;
    /// <summary>URL used to refresh tokens. Null when not stated.</summary>
    public string? RefreshUrl { get; set; }
    /// <summary>Available scopes, as scope name to description.</summary>
    public Dictionary<string, string> Scopes { get; set; } = new();
}

/// <summary>OAuth 2.0 implicit flow. Deprecated by the spec.</summary>
public sealed class ImplicitOAuthFlow
{
    /// <summary>URL of the authorization endpoint.</summary>
    public string? AuthorizationUrl { get; set; }
    /// <summary>URL used to refresh tokens. Null when not stated.</summary>
    public string? RefreshUrl { get; set; }
    /// <summary>Available scopes, as scope name to description.</summary>
    public Dictionary<string, string>? Scopes { get; set; }
}

/// <summary>OAuth 2.0 resource owner password flow. Deprecated by the spec.</summary>
public sealed class PasswordOAuthFlow
{
    /// <summary>URL of the token endpoint.</summary>
    public string? TokenUrl { get; set; }
    /// <summary>URL used to refresh tokens. Null when not stated.</summary>
    public string? RefreshUrl { get; set; }
    /// <summary>Available scopes, as scope name to description.</summary>
    public Dictionary<string, string>? Scopes { get; set; }
}

/// <summary>OAuth 2.0 device code flow (RFC 8628).</summary>
public sealed class DeviceCodeOAuthFlow
{
    /// <summary>URL of the device authorization endpoint.</summary>
    public string DeviceAuthorizationUrl { get; set; } = string.Empty;
    /// <summary>URL of the token endpoint.</summary>
    public string TokenUrl { get; set; } = string.Empty;
    /// <summary>URL used to refresh tokens. Null when not stated.</summary>
    public string? RefreshUrl { get; set; }
    /// <summary>Available scopes, as scope name to description.</summary>
    public Dictionary<string, string> Scopes { get; set; } = new();
}
