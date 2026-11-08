// Bearer tokens used by the a2a-itk ACTS runner (docs/conformance/05_sdk-integration.md, section 4).
// These are fixed test values from the runner, not secrets.
using System.Security.Claims;
using System.Text.Json;
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
        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            // Default pass: only the extended card needs a caller. The ACTS runner sends a token on abstract operations and none
            // on raw steps, and the SDK scopes tasks to their owner, so a task started with a token is correctly invisible to a
            // raw step without one. Treating every other request as anonymous keeps all steps on one owner.
            if (!Enforced && !await IsExtendedCardRequestAsync().ConfigureAwait(false))
                return AuthenticateResult.NoResult();
            string? header = Request.Headers.Authorization;
            if (header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return AuthenticateResult.NoResult();
            var token = header["Bearer ".Length..].Trim();
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "acts-user") };
            if (token == ValidToken) claims.Add(new Claim(ScopeClaim, "a2a"));
            else if (token != InsufficientToken) return AuthenticateResult.NoResult();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
        }

        /// <summary>HTTP+JSON path, gRPC path, or a JSON-RPC POST whose method is GetExtendedAgentCard.</summary>
        private async Task<bool> IsExtendedCardRequestAsync()
        {
            var path = Request.Path.Value ?? "";
            if (path.EndsWith("/extendedAgentCard", StringComparison.Ordinal) || path.EndsWith("/GetExtendedAgentCard", StringComparison.Ordinal))
                return true;
            if (!HttpMethods.IsPost(Request.Method) || Request.ContentLength == 0 || !Request.HasJsonContentType()) return false; // never touch gRPC bodies
            try
            {
                Request.EnableBuffering();
                using var doc = await JsonDocument.ParseAsync(Request.Body, cancellationToken: Context.RequestAborted).ConfigureAwait(false);
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("method", out var method)
                    && method.ValueKind == JsonValueKind.String
                    && method.GetString() == "GetExtendedAgentCard";
            }
            catch (JsonException) { return false; }
            finally { if (Request.Body.CanSeek) Request.Body.Position = 0; }
        }
    }

    /// <summary>Auth pass: refuses anonymous callers with 401. Always: denies authenticated callers without the scope (403).</summary>
    internal sealed class ScopeAuthorizationHandler : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken)
        {
            if (Enforced && !call.IsAuthenticated) throw new A2AException(A2AErrorCode.Unauthenticated);
            return ValueTask.FromResult(operation.Kind != A2AOperation.Extension && (!call.IsAuthenticated || call.User?.HasClaim(ScopeClaim, "a2a") == true));
        }
    }
}
