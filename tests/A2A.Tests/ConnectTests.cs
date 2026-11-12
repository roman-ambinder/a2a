using A2A.Client;
using A2A.Client.Credentials;
using Microsoft.AspNetCore.TestHost;

namespace A2A.Tests;

/// <summary>A2AClient.ConnectAsync: the one way to reach an agent by URL.</summary>
public class ConnectTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>The test server, with the test scheme's user on every request (the card fetch included).</summary>
    private HttpMessageHandler Handler(string user = "alice") =>
        new UserHandler(user) { InnerHandler = _host.App.GetTestServer().CreateHandler() };

    [Fact]
    public async Task Connects_by_url_and_keeps_the_resolved_card()
    {
        var agent = await A2AClient.ConnectAsync(new Uri("http://localhost/"), o => o.Handler = Handler());
        Assert.Equal("hi there", await agent.AskAsync("hello"));
        Assert.NotNull(agent.AgentCard);
        Assert.NotNull(agent.ResolvedCard);
        Assert.Same(agent.AgentCard, agent.ResolvedCard!.Card);
        Assert.Equal("localhost", agent.ResolvedCard.Source.Host);
    }

    [Fact]
    public async Task The_string_overload_takes_an_absolute_url()
    {
        var agent = await A2AClient.ConnectAsync("http://localhost/", o => o.Handler = Handler());
        Assert.Equal("part1part2", await agent.AskAsync("task"));
        await Assert.ThrowsAsync<UriFormatException>(() => A2AClient.ConnectAsync("not a url"));
    }

    [Fact]
    public async Task Client_options_reach_the_client()
    {
        var agent = await A2AClient.ConnectAsync(new Uri("http://localhost/"), o =>
        {
            o.Handler = Handler();
            o.Client.MaxRetries = 0;
        });
        var reply = await agent.StartConversation().SendAsync("input");
        Assert.True(reply.NeedsInput);
    }

    [Fact]
    public async Task A_handler_that_follows_redirects_is_refused()
    {
        using var redirecting = new HttpClientHandler { AllowAutoRedirect = true };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            A2AClient.ConnectAsync(new Uri("http://localhost/"), o => o.Handler = redirecting));
    }

    [Fact]
    public async Task Credentials_go_through_the_credential_path()
    {
        // The test card declares no scheme, so the provider has nothing to send; the call still works and the card is
        // fetched without credentials (the provider never runs for it).
        var provider = new ApiKeyCredentialProvider("unused");
        var agent = await A2AClient.ConnectAsync(new Uri("http://localhost/"), o =>
        {
            o.Handler = Handler();
            o.Credentials.Add(provider);
        });
        Assert.Equal("hi there", await agent.AskAsync("hello"));
        Assert.NotNull(agent.ResolvedCard);
    }

    [Fact]
    public async Task A_null_url_is_refused() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => A2AClient.ConnectAsync((Uri)null!));

    private sealed class UserHandler(string user) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Add("X-Test-User", user);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
