using A2A.Server.AspNetCore;
using A2AAgent1;

var builder = WebApplication.CreateBuilder(args);

#if (AuthApiKey)
// Callers send an API key in the X-Api-Key header. The agent keeps only key hashes, in "A2A:ApiKeys" of
// appsettings.json (or environment variables, or a secret store's configuration provider).
// Make a key and its hash with the a2a tool: dotnet tool install -g RomanAmbinder.A2A.Cli, then a2a keys new.
#else
// Outside Development, callers send a bearer token (JWT). Add the package Microsoft.AspNetCore.Authentication.JwtBearer
// and register it here, for example:
//   builder.Services.AddAuthentication().AddJwtBearer(o => { o.Authority = "https://your-idp"; o.Audience = "api://your-agent"; });
// Until you do, the app refuses to start outside Development and says so.
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
#endif

// The Agent Card, limits and public URL come from the "A2A" section of appsettings.json.
builder.Services.AddA2A(builder.Configuration.GetSection("A2A"), a =>
{
    a.AddAgentExecutor<Agent>();
#if (AuthApiKey)
    a.AddApiKeyAuthentication(); // every A2A endpoint requires a key, in Development too
    // Local work: accept curl without the A2A-Version header.
    if (builder.Environment.IsDevelopment()) a.Configure(o => o.TreatMissingVersionAsCurrent = true);
#else
    // Local work: anonymous callers, and curl without the A2A-Version header (Development only).
    if (builder.Environment.IsDevelopment()) a.ConfigureForDevelopment();
    // Anywhere else: the card says "send a bearer token", and every A2A endpoint requires one ("Bearer" from AddJwtBearer).
    else a.AddBearerAuthentication();
#endif
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapA2A("/a2a"); // JSON-RPC at POST /a2a, HTTP+JSON under /a2a, the card at /.well-known/agent-card.json
app.Run();
