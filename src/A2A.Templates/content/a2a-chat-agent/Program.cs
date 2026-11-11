using A2A.Extensions.AI;
#if (AuthApiKey)
using A2A.Server;
#endif
using A2A.Server.AspNetCore;
using A2AChatAgent1;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

// The model behind the agent. Replace EchoChatClient with any IChatClient, for example (package
// Microsoft.Extensions.AI.OpenAI): new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini").AsIChatClient().
// Add .AsBuilder().UseFunctionInvocation().Build() when the model should call tools of its own.
builder.Services.AddSingleton<IChatClient>(new EchoChatClient());

#if (AuthApiKey)
// Callers send an API key in the X-Api-Key header. The agent keeps only key hashes, in "A2A:ApiKeys" of
// appsettings.json (or environment variables, or a secret store's configuration provider).
// Make a key and its hash with the a2a tool: dotnet tool install -g RomanAmbinder.A2A.Cli, then a2a keys new.
#else
// Add your authentication scheme here before you deploy, for example JWT bearer
// (package Microsoft.AspNetCore.Authentication.JwtBearer): builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
#endif

// The Agent Card, limits and public URL come from the "A2A" section of appsettings.json.
builder.Services.AddA2A(builder.Configuration.GetSection("A2A"), a =>
{
    a.AddChatClientAgent(o => o.Instructions = "You are a helpful, short-spoken agent.");
#if (AuthApiKey)
    a.AddApiKeyAuthentication(); // every A2A endpoint requires a key, in Development too
    // Local work: accept curl without the A2A-Version header.
    if (builder.Environment.IsDevelopment())
        a.Services.Configure<A2AServerOptions>(o => o.TreatMissingVersionAsCurrent = true);
#else
    // Local work: anonymous callers, and curl without the A2A-Version header. The app refuses it outside Development.
    if (builder.Environment.IsDevelopment()) a.UseDevelopmentDefaults();
#endif
});

var app = builder.Build();
app.UseAuthentication();
app.UseA2A(); // per-caller rate limiting
app.UseAuthorization();
#if (AuthApiKey)
app.MapA2A("/a2a"); // the API key protects the A2A endpoints
#else
var a2a = app.MapA2A("/a2a");
if (!app.Environment.IsDevelopment()) a2a.RequireAuthorization();
#endif
app.Run();
