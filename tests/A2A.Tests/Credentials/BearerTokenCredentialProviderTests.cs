using A2A.Client.Credentials;

namespace A2A.Tests.Credentials;

public class BearerTokenCredentialProviderTests
{
    private static readonly Uri Origin = new("https://agent.example/");

    private static A2ACredentialContext Context(SecurityScheme scheme) => new("s", scheme, [], Origin);

    [Fact]
    public void Handles_bearer_oauth2_and_openid_connect_but_not_api_keys()
    {
        var provider = new BearerTokenCredentialProvider(_ => ValueTask.FromResult("t"));
        Assert.True(provider.CanHandle(Context(SecurityScheme.Bearer())));
        Assert.True(provider.CanHandle(Context(new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "bearer" }))));
        Assert.True(provider.CanHandle(Context(new OpenIdConnectScheme(new OpenIdConnectSecurityScheme
        {
            OpenIdConnectUrl = "https://agent.example/.well-known/openid-configuration"
        }))));
        Assert.False(provider.CanHandle(Context(SecurityScheme.ApiKey("X-Api-Key"))));
        Assert.False(provider.CanHandle(Context(new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "basic" }))));
    }

    [Fact]
    public async Task Asks_the_source_on_every_request()
    {
        var calls = 0;
        var provider = new BearerTokenCredentialProvider(_ => ValueTask.FromResult($"token-{++calls}"));
        using var first = new HttpRequestMessage(HttpMethod.Post, "https://agent.example/a2a");
        using var second = new HttpRequestMessage(HttpMethod.Post, "https://agent.example/a2a");
        await provider.ApplyAsync(first, Context(SecurityScheme.Bearer()), CancellationToken.None);
        await provider.ApplyAsync(second, Context(SecurityScheme.Bearer()), CancellationToken.None);
        Assert.Equal("Bearer token-1", first.Headers.Authorization!.ToString());
        Assert.Equal("Bearer token-2", second.Headers.Authorization!.ToString()); // a rotated token is picked up
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc\r\nX-Injected: 1")]
    public async Task Refuses_an_empty_token_or_one_with_control_characters(string token)
    {
        var provider = new BearerTokenCredentialProvider(_ => ValueTask.FromResult(token));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://agent.example/a2a");
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            provider.ApplyAsync(request, Context(SecurityScheme.Bearer()), CancellationToken.None).AsTask());
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Null(request.Headers.Authorization);
    }

    [Fact]
    public void A_null_source_is_refused() =>
        Assert.Throws<ArgumentNullException>(() => new BearerTokenCredentialProvider(null!));
}
