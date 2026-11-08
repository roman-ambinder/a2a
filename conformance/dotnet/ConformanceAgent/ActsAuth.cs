// Bearer tokens used by the a2a-itk ACTS runner (docs/conformance/05_sdk-integration.md, section 4).
// These are fixed test values from the runner, not secrets.
using System.Security.Claims;
using System.Text.Encodings.Web;
using A2A;
using A2A.Json;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace ConformanceAgent;

internal static class ActsAuth
{
    public const string SchemeName = "ActsBearer";
    public const string ValidToken = "itk-valid-token";
    public const string InsufficientToken = "itk-insufficient-token";
    private const string SchemeId = "bearerAuth";
    private const string ScopeClaim = "scope";

    /// <summary>ITK_ACTS_AUTH set: the card declares a scheme and every operation requires it.</summary>
    public static bool Enforced => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ITK_ACTS_AUTH"));

    public static Dictionary<string, SecurityScheme>? SecuritySchemes() => Enforced
        ? new() { [SchemeId] = new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "Bearer", BearerFormat = "opaque", Description = "ACTS runner token" }) }
        : null;

    public static List<SecurityRequirement>? SecurityRequirements() => Enforced
        ? [new SecurityRequirement { Schemes = new() { [SchemeId] = new StringList() } }]
        : null;

    /// <summary>The extended card: the public card plus one skill only an authenticated caller sees.</summary>
    public static AgentCard Extend(AgentCard publicCard)
    {
        var copy = A2AJson.Deserialize<AgentCard>(A2AJson.Serialize(publicCard));
        copy.Skills = [.. copy.Skills, new AgentSkill { Id = "extended", Name = "Extended skill", Description = "Only on the extended card", Tags = ["acts"] }];
        return copy;
    }

    /// <summary>
    /// Valid token: authenticated with the "a2a" scope. Insufficient token: authenticated, no scope (gets 403).
    /// Anything else: anonymous (gets 401 where authentication is required).
    /// </summary>
    internal sealed class BearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            string? header = Request.Headers.Authorization;
            if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(AuthenticateResult.NoResult());
            var token = header["Bearer ".Length..].Trim();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "acts-user") };
            if (token == ValidToken) claims.Add(new Claim(ScopeClaim, "a2a"));
            else if (token != InsufficientToken) return Task.FromResult(AuthenticateResult.NoResult());
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }

    /// <summary>Denies authenticated callers without the scope. Anonymous callers are left to AllowAnonymous.</summary>
    internal sealed class ScopeAuthorizationHandler : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.Extension && (!call.IsAuthenticated || call.User?.HasClaim(ScopeClaim, "a2a") == true));
    }
}
