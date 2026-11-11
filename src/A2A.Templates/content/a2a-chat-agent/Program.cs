using A2A.Extensions.AI;
using A2A.Server.AspNetCore;
using A2AChatAgent1;
using Microsoft.Extensions.AI;

var builder = WebApplication.CreateBuilder(args);

// The model behind the agent. Replace EchoChatClient with any IChatClient, for example (package
// Microsoft.Extensions.AI.OpenAI): new OpenAIClient(apiKey).GetChatClient("gpt-4o-mini").AsIChatClient().
// Add .AsBuilder().UseFunctionInvocation().Build() when the model should call tools of its own.
builder.Services.AddSingleton<IChatClient>(new EchoChatClient());

// Add your authentication scheme here before you deploy, for example JWT bearer
// (package Microsoft.AspNetCore.Authentication.JwtBearer): builder.Services.AddAuthentication().AddJwtBearer();
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();

// The Agent Card, limits and public URL come from the "A2A" section of appsettings.json.
builder.Services.AddA2A(builder.Configuration.GetSection("A2A"), a =>
{
    a.AddChatClientAgent(o => o.Instructions = "You are a helpful, short-spoken agent.");
    // Local work: anonymous callers, and curl without the A2A-Version header. The app refuses it outside Development.
    if (builder.Environment.IsDevelopment()) a.UseDevelopmentDefaults();
});

var app = builder.Build();
app.UseAuthentication();
app.UseA2A(); // per-caller rate limiting
app.UseAuthorization();
var a2a = app.MapA2A("/a2a");
if (!app.Environment.IsDevelopment()) a2a.RequireAuthorization();
app.Run();
