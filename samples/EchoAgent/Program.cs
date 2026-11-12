using System.Security.Cryptography;
using A2A;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Signing;

var builder = WebApplication.CreateBuilder(args);
var baseUrl = builder.Configuration["BaseUrl"] ?? "http://localhost:5100";
// The agent stores only the hash of its API key; the caller holds the key. Make a pair with `a2a keys new`
// (or A2AApiKey.Create()) and start the agent with --EchoAgent:ApiKeyHash=<hash>. No default: a sample that starts
// with a hash everyone knows would let in anyone who read the key in this file.
var apiKeyHash = builder.Configuration["EchoAgent:ApiKeyHash"];
if (string.IsNullOrEmpty(apiKeyHash))
    throw new InvalidOperationException(
        "Set EchoAgent:ApiKeyHash to the hash of an API key (for example: dotnet run -- --EchoAgent:ApiKeyHash=sha256:...). "
        + "Make a key and its hash with 'a2a keys new'. Callers send the key in the X-Api-Key header.");

var card = new AgentCard
{
    Name = "Echo Agent",
    Description = "Echoes your message back, streaming it word by word.",
    Version = "1.0.0",
    // No SupportedInterfaces: the served card lists every mapped binding at the public base URL (set below).
    Capabilities = new AgentCapabilities { PushNotifications = true },
    // No SecuritySchemes: AddApiKeyAuthentication declares the X-Api-Key scheme, so clients know to send the key.
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills =
    [
        new AgentSkill
        {
            Id = "echo", Name = "Echo", Description = "Repeats the input", Tags = ["echo"], Examples = ["hello world"]
        }
    ]
};

// Optional: sign the Agent Card. Set EchoAgent:SigningKeyFile to a PKCS#8 or SEC1 EC private key in PEM (P-256, ES256).
// The public key is then served at /.well-known/jwks.json, and clients pin it with a CardTrustPolicy (see EchoClient).
AgentCardSigningKey? signingKey = null;
if (builder.Configuration["EchoAgent:SigningKeyFile"] is { Length: > 0 } keyFile)
{
    var ec = ECDsa.Create();
    ec.ImportFromPem(File.ReadAllText(keyFile));
    signingKey = new AgentCardSigningKey(ec, builder.Configuration["EchoAgent:SigningKeyId"] ?? "echo-1", "ES256",
        $"{baseUrl}/.well-known/jwks.json");
}

// Limits (request size, stream duration, rate) keep their secure defaults: nothing to configure here.
// AddApiKeyAuthentication: the scheme, the card's security declaration, and the policy on every A2A endpoint.
// Every caller with this key is "echo-client"; register more keys (o.Add(...)) to give each caller its own tasks.
builder.Services.AddA2A(a => a.AddAgentExecutor<EchoAgent>().AddAgentCard(card)
        .AddApiKeyAuthentication(o => o.Add("echo-client", apiKeyHash)))
    // Development convenience: accept requests without the A2A-Version header (e.g. curl).
    .Configure(o => o.TreatMissingVersionAsCurrent = builder.Environment.IsDevelopment())
    .ConfigureHttp(h =>
    {
        h.PublicBaseUrl = new Uri(baseUrl);
        h.SigningKey = signingKey;
    });

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapA2A("/a2a"); // JSON-RPC, HTTP+JSON and the public Agent Card; the API key protects the A2A endpoints
app.Run(baseUrl);

/// <summary>Replies directly to "ping"; otherwise runs a task that streams the text back as an artifact.</summary>
internal sealed class EchoAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        var text = context.UserText;
        if (text.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            await updater.ReplyAsync("pong", cancellationToken);
            return;
        }

        await updater.StartWorkAsync("Echoing...", cancellationToken);
        // The writer creates the artifact on the first write and appends after that; completing the task seals it.
        await using var echo = updater.OpenArtifact("echo");
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            await Task.Delay(150, cancellationToken);
            await echo.WriteAsync((i == 0 ? "" : " ") + words[i], cancellationToken);
        }

        await updater.CompleteAsync(cancellationToken);
    }
}