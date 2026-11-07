using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using A2A;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);
var baseUrl = builder.Configuration["BaseUrl"] ?? "http://localhost:5100";
var apiKey = builder.Configuration["EchoAgent:ApiKey"];
if (string.IsNullOrEmpty(apiKey))
    throw new InvalidOperationException("Set EchoAgent:ApiKey (for example: dotnet run -- --EchoAgent:ApiKey=<secret>). Callers send it in the X-Api-Key header.");

// A2A endpoints need authentication. This sample uses one shared API key; every caller is the same user.
builder.Services.AddAuthentication(ApiKeyAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthHandler>(ApiKeyAuthHandler.SchemeName, null);
builder.Services.AddAuthorization();
builder.Services.AddSingleton(new ApiKey(apiKey));

builder.Services.AddA2AServer(o =>
{
    o.AgentCard = new AgentCard
    {
        Name = "Echo Agent",
        Description = "Echoes your message back, streaming it word by word.",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
        ],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "echo", Name = "Echo", Description = "Repeats the input", Tags = ["echo"], Examples = ["hello world"] }],
    };
    // Development convenience: accept requests without the A2A-Version header (e.g. curl).
    o.TreatMissingVersionAsCurrent = builder.Environment.IsDevelopment();
}).AddAgentExecutor<EchoAgent>();
builder.Services.AddA2AServerAspNetCore();

var app = builder.Build();
app.UseAuthentication();
app.UseA2A();                                // per-caller rate limiting (adds UseRateLimiter)
app.UseAuthorization();
app.MapAgentCard();                          // public, anonymous
app.MapA2A("/a2a").RequireAuthorization();   // needs a valid X-Api-Key
app.Run(baseUrl);

internal sealed record ApiKey(string Value);

/// <summary>Accepts requests whose <c>X-Api-Key</c> header matches the configured key.</summary>
internal sealed class ApiKeyAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKey key)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var supplied = Request.Headers["X-Api-Key"].ToString();
        if (supplied.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());
        var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(key.Value));
        if (!ok) return Task.FromResult(AuthenticateResult.Fail("Invalid API key"));
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "echo-client")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>Replies directly to "ping"; otherwise runs a task that streams the text back as an artifact.</summary>
internal sealed class EchoAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var text = context.UserText;
        if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await updater.ReplyAsync("pong", cancellationToken);
            return;
        }

        await updater.StartWorkAsync("Echoing...", cancellationToken);
        string? artifactId = null;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            await Task.Delay(150, cancellationToken);
            artifactId = await updater.AddArtifactAsync([Part.FromText((i == 0 ? "" : " ") + words[i])], artifactId, name: "echo",
                append: i > 0, lastChunk: i == words.Length - 1, cancellationToken: cancellationToken);
        }
        await updater.CompleteAsync(cancellationToken: cancellationToken);
    }
}
