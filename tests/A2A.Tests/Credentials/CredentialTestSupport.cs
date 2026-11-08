using System.Collections.Concurrent;
using System.Net;
using System.Text;
using A2A.Client;
using A2A.Client.Credentials;
using A2A.Signing;

namespace A2A.Tests.Credentials;

internal sealed class RecordingProvider(string name, Func<A2ACredentialContext, bool>? canHandle = null, Func<HttpRequestMessage, Task>? apply = null) : IA2ACredentialProvider
{
    public ConcurrentQueue<A2ACredentialContext> Applied { get; } = new();
    public string Name { get; } = name;
    public bool CanHandle(A2ACredentialContext context) => canHandle?.Invoke(context) ?? true;

    public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken)
    {
        Applied.Enqueue(context);
        request.Headers.TryAddWithoutValidation("X-Cred", Name);
        if (apply is not null) await apply(request);
    }
}

internal sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
{
    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return Task.FromResult(respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }
}

internal static class Cards
{
    public static SecurityRequirement Req(params (string Scheme, string[] Scopes)[] items) =>
        new() { Schemes = items.ToDictionary(i => i.Scheme, i => new StringList { List = i.Scopes.ToList() }) };

    public static AgentCard Card(string interfaceUrl = "https://agent.example/a2a", string binding = ProtocolBindings.JsonRpc,
        Dictionary<string, SecurityScheme>? schemes = null, List<SecurityRequirement>? requirements = null) => new()
        {
            Name = "agent", Description = "d", Version = "1",
            SupportedInterfaces = [new AgentInterface { Url = interfaceUrl, ProtocolBinding = binding }],
            SecuritySchemes = schemes,
            SecurityRequirements = requirements,
        };

    public static ResolvedAgentCard Resolved(AgentCard card, string source = "https://agent.example/.well-known/agent-card.json") =>
        new(card, "{}", new Uri(source), new AgentCardVerificationResult(AgentCardVerificationStatus.Unsigned, null, null, null));

    public static Dictionary<string, SecurityScheme> TwoSchemes() => new()
    {
        ["key"] = SecurityScheme.ApiKey("X-Key"),
        ["oauth"] = new OAuth2Scheme(new OAuth2SecurityScheme()),
        ["bearer"] = SecurityScheme.Bearer(),
    };
}
