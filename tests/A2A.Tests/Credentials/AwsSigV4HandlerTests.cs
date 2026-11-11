using System.Net;
using A2A.Client.Identity.Aws;
using Amazon.Runtime;

namespace A2A.Tests.Credentials;

/// <summary>
///     <see cref="AwsSigV4Handler" />: both constructors check the options the same way, and only requests to the agent
///     origin are signed. The signature itself is covered in <see cref="AwsSigV4Tests" />.
/// </summary>
public class AwsSigV4HandlerTests
{
    private static AwsSigV4Options Options(Action<AwsSigV4Options>? configure = null)
    {
        var options = new AwsSigV4Options
        {
            Region = "us-east-1", Service = "execute-api",
            AgentOrigin = new Uri("https://abc123.execute-api.us-east-1.amazonaws.com"),
            Credentials = new BasicAWSCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY")
        };
        configure?.Invoke(options);
        return options;
    }

    private sealed class Capture : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    [Fact]
    public void Both_constructors_need_an_agent_origin()
    {
        var options = Options(o => o.AgentOrigin = null);
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(options));
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(options, new Capture()));
    }

    [Fact]
    public void Both_constructors_need_https_for_a_remote_origin()
    {
        var options = Options(o => o.AgentOrigin = new Uri("http://agent.example.com"));
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(options));
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(options, new Capture()));
    }

    [Fact]
    public void Inner_handler_that_follows_redirects_is_refused()
    {
        using var redirecting = new HttpClientHandler { AllowAutoRedirect = true };
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(Options(), redirecting));
    }

    [Fact]
    public async Task Requests_to_the_agent_origin_are_signed_and_others_are_not()
    {
        var inner = new Capture();
        using var invoker = new HttpMessageInvoker(new AwsSigV4Handler(Options(), inner));
        var ct = TestContext.Current.CancellationToken;

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://abc123.execute-api.us-east-1.amazonaws.com/.well-known/agent-card.json"), ct);
        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://other.example/x"), ct);

        // The signer adds Authorization without validation: the typed property cannot parse a SigV4 value.
        Assert.True(inner.Requests[0].Headers.TryGetValues("Authorization", out var signed));
        Assert.StartsWith("AWS4-HMAC-SHA256 ", Assert.Single(signed));
        Assert.True(inner.Requests[0].Headers.Contains("X-Amz-Date"));
        Assert.False(inner.Requests[1].Headers.Contains("Authorization"));
        Assert.False(inner.Requests[1].Headers.Contains("X-Amz-Date"));
    }
}
