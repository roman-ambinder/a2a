// System under test for the official A2A conformance suites:
//   - a2a-tck  (picks a behavior by messageId prefix, see Behaviors.Tck)
//   - a2a-itk ACTS (picks a behavior by the first text part, see Behaviors.Acts)
// Usage: dotnet run -- [port]     HTTP (JSON-RPC + HTTP+JSON) on <port>, cleartext gRPC on <port+1>.
// Env:   ITK_ACTS_AUTH=1                  require a bearer token on every operation (ACTS auth pass)
//        ITK_ACTS_REDUCED_CAPABILITIES=1  advertise no streaming, push or extended card (ACTS reduced pass)
using A2A;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using ConformanceAgent;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var port = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 9999;
var host = Environment.GetEnvironmentVariable("SUT_BIND_HOST") ?? "127.0.0.1";
var baseUrl = $"http://{host}:{port}";
var reduced = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ITK_ACTS_REDUCED_CAPABILITIES"));
var authRequired = ActsAuth.Enforced;

var builder = WebApplication.CreateBuilder();
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenLocalhost(port, o => o.Protocols = HttpProtocols.Http1);
    k.ListenLocalhost(port + 1, o => o.Protocols = HttpProtocols.Http2);
});

var card = new AgentCard
{
    Name = "A2A .NET SDK conformance agent",
    Description = "System under test for the A2A TCK and ACTS suites",
    Version = "1.0.0",
    Provider = new AgentProvider { Organization = "roman-ambinder/a2a", Url = "https://github.com/roman-ambinder/a2a" },
    SupportedInterfaces =
    [
        new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
        new AgentInterface { Url = $"{baseUrl}/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
        new AgentInterface { Url = $"http://{host}:{port + 1}", ProtocolBinding = ProtocolBindings.Grpc },
    ],
    Capabilities = new AgentCapabilities
    {
        Streaming = !reduced,
        PushNotifications = !reduced,
        ExtendedAgentCard = !reduced,
    },
    DefaultInputModes = ["text/plain", "application/json"],
    DefaultOutputModes = ["text/plain", "application/json"],
    Skills = [new AgentSkill { Id = "conformance", Name = "Conformance", Description = "TCK and ACTS test behaviors", Tags = ["tck", "acts"] }],
    SecuritySchemes = ActsAuth.SecuritySchemes(),
    SecurityRequirements = ActsAuth.SecurityRequirements(),
};

builder.Services.AddA2AServer(o =>
{
    // Default pass: the suites call anonymously. Auth pass: every operation needs the bearer token.
    o.AllowAnonymous = !authRequired;
    o.AgentCard = card;
    o.ExtendedAgentCardProvider = (_, publicCard, _) => ValueTask.FromResult<AgentCard?>(ActsAuth.Extend(publicCard));
    // The suites run their webhook receivers on 127.0.0.1 over plain HTTP.
    o.PushNotifications.AllowInsecureUrls = true;
    o.PushNotifications.AllowPrivateNetworks = true;
})
.AddAgentExecutor<Behaviors>()
.AddAuthorizationHandler<ActsAuth.ScopeAuthorizationHandler>();
builder.Services.AddA2AServerAspNetCore();
// All suite calls come from one address; the default per-caller budget is too small for a full run.
builder.Services.AddA2ARateLimiting(o =>
{
    o.TokenLimit = 100_000;
    o.TokensPerPeriod = 100_000;
});
builder.Services.AddA2AGrpc();
builder.Services.AddAuthentication(ActsAuth.SchemeName)
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ActsAuth.BearerHandler>(ActsAuth.SchemeName, null);

var app = builder.Build();
app.UseAuthentication();
app.UseA2A();
app.MapAgentCard();
app.MapA2A("/a2a");
app.MapA2AGrpc();
app.Run();
