using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A;
using A2A.Client;
using A2A.Client.Peers;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

// The client side of A2A: OAuth2 client credentials, an API key, retries, peer identity and a custom operation.
// One process hosts a small agent and a DEMO token endpoint on 127.0.0.1, and the clients call them.
// No real secrets, no external calls. The client id, client secret and API key below are demo values.

const int port = 5347;
const string DemoClientId = "demo-client";     // demo value
const string DemoClientSecret = "demo-secret"; // demo value: a real secret comes from a vault or the environment
const string DemoApiKey = "demo-api-key";      // demo value
const string ReportsUri = "https://example.com/extensions/reports/v1"; // an extension id; nothing is fetched from it
var agentUrl = new Uri($"http://127.0.0.1:{port}/");
var tokenUrl = $"http://127.0.0.1:{port}/demo/token";

var builder = WebApplication.CreateBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port)); // loopback only

// The card offers two ways in, in this order: OAuth2 client credentials, or an API key in X-Api-Key.
var card = new AgentCard
{
    Name = "Client Features Agent",
    Description = "Says who called it, and how.",
    Version = "1.0.0",
    SupportedInterfaces =
        [new AgentInterface { Url = $"http://127.0.0.1:{port}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }],
    SecuritySchemes = new()
    {
        // The token URL is on the card's origin, so the client may use it without being told (see the OAuth2 client below).
        ["oauth"] = new OAuth2Scheme(new OAuth2SecurityScheme
        {
            Flows = new OAuthFlows
            {
                ClientCredentials = new ClientCredentialsOAuthFlow
                {
                    TokenUrl = tokenUrl, Scopes = { ["agent.invoke"] = "Call the agent" }
                }
            }
        }),
        ["apiKey"] = SecurityScheme.ApiKey("X-Api-Key")
    },
    SecurityRequirements =
    [
        new SecurityRequirement { Schemes = { ["oauth"] = new StringList { List = ["agent.invoke"] } } },
        new SecurityRequirement { Schemes = { ["apiKey"] = new StringList() } }
    ],
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills = [new AgentSkill { Id = "whoami", Name = "Who am I", Description = "Names the caller", Tags = ["demo"] }]
};

var tokens = new DemoTokenIssuer(DemoClientId, DemoClientSecret);
var faults = new FaultInjector();
builder.Services.AddSingleton(tokens);
builder.Services.AddSingleton(new DemoApiKeyValue(DemoApiKey));
builder.Services.AddAuthentication(DemoAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DemoAuthHandler>(DemoAuthHandler.SchemeName, null);
builder.Services.AddAuthorization(o => o.AddPolicy("caller", p => p.RequireAuthenticatedUser()));

builder.Services.AddA2A(a => a
    .AddAgentCard(card)
    .AddAgentExecutor<WhoAmIAgent>()
    // A custom operation. The default card provider adds ReportsUri to the card's extensions by itself.
    .AddExtensionOperation("reports.create", ReportsUri,
        ReportJson.Default.ReportRequest, ReportJson.Default.ReportResult,
        (context, request, cancellationToken) => ValueTask.FromResult(
            new ReportResult($"https://example.com/reports/{request.Year}/{request.Name}?for={context.Call.Principal}")),
        authorizationPolicy: "caller"));

var app = builder.Build();
// Fault injection for the retry check: answers 503 to the next A2A request when armed, and counts A2A requests.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/a2a"))
    {
        Interlocked.Increment(ref faults.Attempts);
        if (Volatile.Read(ref faults.FailNext) > 0 && Interlocked.Decrement(ref faults.FailNext) >= 0)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
    }

    await next(context);
});
app.UseAuthentication();
app.UseA2A();
app.UseAuthorization();
// DEMO ONLY. This is not an OAuth server: no client registry, no signed tokens, no revocation. It exists so the sample
// can run without an identity provider. In production the token endpoint is your identity provider.
app.MapPost("/demo/token", tokens.HandleAsync);
app.MapA2A("/a2a", o => o.AuthorizationPolicy = "caller"); // the card stays anonymous
await app.StartAsync();
Console.WriteLine($"Agent card at {agentUrl}.well-known/agent-card.json, demo token endpoint at {tokenUrl}");

var failures = new List<string>();

void Expect(string what, object? actual, object? expected, bool print = true)
{
    if (print) Console.WriteLine($"{what}: {actual}");
    if (!Equals(actual, expected)) failures.Add($"{what}: expected '{expected}', got '{actual}'");
}

async Task Step(string name, Func<Task> body)
{
    Console.WriteLine();
    Console.WriteLine($"--- {name}");
    try
    {
        await body();
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
    }
}

// Two named clients. The SDK picks the provider from the card when it creates each client, and sends the credential
// only to the card's origin. Security defaults stay on: credentials need HTTPS, but loopback is exempt (both
// RequireHttps options say so), and the endpoint policy (MatchCardNetwork) accepts a card on a loopback host that
// names a loopback endpoint. Nothing is turned off for this sample.
var services = new ServiceCollection();
services.AddA2AClient("oauth", o => o.AgentUrl = agentUrl)
    .AddOAuth2ClientCredentials(o =>
    {
        o.ClientId = DemoClientId;
        o.ClientSecret = DemoClientSecret;
        // No TokenEndpoint: the card's tokenUrl is on the card's origin, so the provider uses it and binds to this agent.
        // A real identity provider is usually on another origin; then set TokenEndpoint.
        o.Scopes = ["agent.invoke"];
    });
services.AddA2AClient("api-key", o => o.AgentUrl = agentUrl).AddApiKey(DemoApiKey); // header name comes from the card
// The client has its own container, apart from the agent's host, as it would in a separate app.
#pragma warning disable ASP0000 // not the web host's container, so no singleton is copied
await using var provider = services.BuildServiceProvider();
#pragma warning restore ASP0000
var clients = provider.GetRequiredService<IA2AClientFactory>();

// 1. OAuth2 client credentials: the client gets a token from the demo endpoint and sends it as a bearer token.
await Step("OAuth2 client credentials", async () =>
{
    var oauthClient = await clients.GetClientAsync("oauth");
    Expect("reply", await AskAsync(oauthClient), "demo-client via oauth2, bearer token #1 from the demo token endpoint");
    Expect("tokens issued", tokens.Issued, 1);
});

// 2. API key: the card's first requirement (OAuth2) has no provider here, so the client takes the second one.
A2AClient? client = null;
await Step("API key", async () =>
{
    client = await clients.GetClientAsync("api-key");
    Expect("reply", await AskAsync(client), "api-key-user via api-key");
});

// 3. Retries: the agent answers 503 once. The client retries (MaxRetries is 2 by default) and the call succeeds.
await Step("Retry after a 503", async () =>
{
    client ??= await clients.GetClientAsync("api-key");
    faults.Arm(1);
    Expect("reply", await AskAsync(client), "api-key-user via api-key");
    Expect("attempts", faults.Attempts, 2);
});

// 4. Peer identity: who the agent is, as the SDK sees it. The card name is display text, not identity.
await Step("Peer identity", async () =>
{
    using var http = new HttpClient();
    var resolver = new A2ACardResolver(http);
    #region Read
    var resolved = await resolver.ResolveAsync(agentUrl);
    var identity = resolved.Identity;

    Console.WriteLine($"origin: {identity.Origin}");          // the origin of the card URL, lower case
    Console.WriteLine($"key id: {identity.KeyId ?? "none"}"); // the kid of the trusted key that verified the card, or null
    Console.WriteLine($"assurance: {identity.Assurance}");    // OriginOnly (no key) or Verified
    Console.WriteLine($"identity: {identity}");               // for logs: "... (origin only)", or origin#kid
    #endregion
    // This card is not signed, so the identity is the origin alone. Sign the card to get a key id (agent card signing).
    Expect("origin", identity.Origin, $"http://127.0.0.1:{port}", print: false);
    Expect("key id", identity.KeyId, null, print: false);
    Expect("assurance", identity.Assurance, PeerIdentityAssurance.OriginOnly, print: false);
});

// 5. A custom operation: a method of an extension, next to the A2A operations. The helper activates the extension.
await Step("Custom operation", async () =>
{
    client ??= await clients.GetClientAsync("api-key");
    #region ClientTyped
    var result = await client.InvokeExtensionOperationAsync(
        "reports.create", ReportsUri,
        new ReportRequest("sales", 2026),
        ReportJson.Default.ReportRequest, ReportJson.Default.ReportResult);
    Console.WriteLine(result.Url);
    #endregion
    Expect("round-trip", result.Url, "https://example.com/reports/2026/sales?for=api-key-user");
});

await app.StopAsync();

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine("FAIL: " + failure);
    return 1;
}

Console.WriteLine();
Console.WriteLine(
    "OK: OAuth2 and API key calls succeeded, a 503 was retried, the peer identity matched and a custom operation round-tripped.");
return 0;

static async Task<string?> AskAsync(A2AClient client)
{
    var reply = await client.SendMessageAsync("who am I?");
    return (reply as MessageResponse)?.Message.GetText();
}

/// <summary>Replies with the caller's principal and how it signed in.</summary>
internal sealed class WhoAmIAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        var user = context.Call.User;
        var method = user?.FindFirst(DemoAuthHandler.MethodClaim)?.Value ?? "none";
        var reply = $"{context.Call.Principal} via {method}";
        if (user?.FindFirst(DemoAuthHandler.TokenClaim)?.Value is { } tokenNumber)
            reply += $", bearer token #{tokenNumber} from the demo token endpoint";
        await updater.ReplyAsync(reply, cancellationToken);
    }
}

// The request and result of the custom operation, with source-generated JSON (safe for trimming and native AOT).
internal sealed record ReportRequest(string Name, int Year);

internal sealed record ReportResult(string Url);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReportRequest))]
[JsonSerializable(typeof(ReportResult))]
internal sealed partial class ReportJson : JsonSerializerContext;

/// <summary>Answers 503 to the next <see cref="Arm" /> requests, and counts A2A requests.</summary>
internal sealed class FaultInjector
{
    public int Attempts;
    public int FailNext;

    public void Arm(int failures)
    {
        Volatile.Write(ref Attempts, 0);
        Volatile.Write(ref FailNext, failures);
    }
}

internal sealed record DemoApiKeyValue(string Value);

/// <summary>
///     DEMO token endpoint, NOT an OAuth server. It knows one client id and secret, issues random opaque tokens for the
///     client credentials grant, and keeps them in memory. Use a real identity provider in production.
/// </summary>
internal sealed class DemoTokenIssuer(string clientId, string clientSecret)
{
    private readonly ConcurrentDictionary<string, int> _issued = new(StringComparer.Ordinal);
    private int _count;

    public int Issued => Volatile.Read(ref _count);

    /// <summary>The number of an issued token, or null when this endpoint did not issue it.</summary>
    public int? Lookup(string token) => _issued.TryGetValue(token, out var n) ? n : null;

    // A RequestDelegate: it writes the response itself.
    public async Task HandleAsync(HttpContext context) => await (await Answer(context)).ExecuteAsync(context);

    private async Task<IResult> Answer(HttpContext context)
    {
        var form = await context.Request.ReadFormAsync(context.RequestAborted);
        if (form["grant_type"] != "client_credentials")
            return Results.Json(new Dictionary<string, string> { ["error"] = "unsupported_grant_type" }, statusCode: 400);
        if (!BasicCredentialsMatch(context.Request.Headers.Authorization.ToString()))
            return Results.Json(new Dictionary<string, string> { ["error"] = "invalid_client" }, statusCode: 401);
        if (form["scope"] != "agent.invoke")
            return Results.Json(new Dictionary<string, string> { ["error"] = "invalid_scope" }, statusCode: 400);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        _issued[token] = Interlocked.Increment(ref _count);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = token, ["token_type"] = "Bearer", ["expires_in"] = 300
        });
    }

    // RFC 6749 section 2.3.1: Basic with the URL-encoded id and secret, which is the provider's default.
    private bool BasicCredentialsMatch(string header)
    {
        if (!header.StartsWith("Basic ", StringComparison.Ordinal)) return false;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..]));
        }
        catch (FormatException)
        {
            return false;
        }

        var colon = decoded.IndexOf(':');
        if (colon < 0) return false;
        return FixedEquals(WebUtility.UrlDecode(decoded[..colon]), clientId)
               & FixedEquals(WebUtility.UrlDecode(decoded[(colon + 1)..]), clientSecret);
    }

    internal static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>Accepts a bearer token from <see cref="DemoTokenIssuer" />, or the demo API key in <c>X-Api-Key</c>.</summary>
internal sealed class DemoAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DemoTokenIssuer tokens,
    DemoApiKeyValue apiKey)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Demo";
    public const string MethodClaim = "auth_method";
    public const string TokenClaim = "token_number";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(tokens.Lookup(authorization["Bearer ".Length..]) is { } number
                ? Success("demo-client", new Claim(MethodClaim, "oauth2"),
                    new Claim(TokenClaim, number.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                : AuthenticateResult.Fail("Unknown bearer token"));
        }

        var key = Request.Headers["X-Api-Key"].ToString();
        if (key.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        return Task.FromResult(DemoTokenIssuer.FixedEquals(key, apiKey.Value)
            ? Success("api-key-user", new Claim(MethodClaim, "api-key"))
            : AuthenticateResult.Fail("Invalid API key"));
    }

    private static AuthenticateResult Success(string principal, params Claim[] claims)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, principal), .. claims], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
