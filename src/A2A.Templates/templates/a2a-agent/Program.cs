using A2A.Server.AspNetCore;
using A2AAgent1;

var builder = WebApplication.CreateBuilder(args);

// Add your authentication scheme here before you deploy, for example JWT bearer
// (package Microsoft.AspNetCore.Authentication.JwtBearer): builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

// The Agent Card, limits and public URL come from the "A2A" section of appsettings.json.
builder.Services.AddA2A(builder.Configuration.GetSection("A2A"), a =>
{
    a.AddAgentExecutor<Agent>();
    // Local work: anonymous callers, and curl without the A2A-Version header. The app refuses it outside Development.
    if (builder.Environment.IsDevelopment()) a.UseDevelopmentDefaults();
});

var app = builder.Build();
app.UseAuthentication();
app.UseA2A(); // per-caller rate limiting
app.UseAuthorization();
var a2a = app.MapA2A("/a2a"); // JSON-RPC at POST /a2a, HTTP+JSON under /a2a, the card at /.well-known/agent-card.json
if (!app.Environment.IsDevelopment()) a2a.RequireAuthorization();
app.Run();
