using A2A.Client.Credentials;
using Xunit;

namespace A2A.Tests.Credentials;

public class ApiKeyCredentialProviderTests
{
    private static A2ACredentialContext Ctx(SecurityScheme scheme, string name = "key") =>
        new(name, scheme, [], new Uri("https://agent.example"));

    private static HttpRequestMessage Req() => new(HttpMethod.Post, "https://agent.example/a2a");

    [Fact]
    public async Task Header_is_placed_under_the_declared_name()
    {
        var p = new ApiKeyCredentialProvider("secret-1");
        var ctx = Ctx(SecurityScheme.ApiKey("X-Api-Key"));
        Assert.True(p.CanHandle(ctx));
        var r = Req();
        r.Headers.TryAddWithoutValidation("X-Api-Key", "old");
        await p.ApplyAsync(r, ctx, default);
        Assert.Equal(["secret-1"], r.Headers.GetValues("X-Api-Key"));
    }

    [Fact]
    public async Task Header_location_is_case_insensitive()
    {
        var p = new ApiKeyCredentialProvider("k");
        var ctx = Ctx(SecurityScheme.ApiKey("X-Api-Key", "HEADER"));
        Assert.True(p.CanHandle(ctx));
        var r = Req();
        await p.ApplyAsync(r, ctx, default);
        Assert.Equal("k", r.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Cookie_is_appended_to_an_existing_cookie()
    {
        var p = new ApiKeyCredentialProvider("abc123");
        var ctx = Ctx(SecurityScheme.ApiKey("sid", "cookie"));
        Assert.True(p.CanHandle(ctx));
        var r = Req();
        r.Headers.TryAddWithoutValidation("Cookie", "existing=1");
        await p.ApplyAsync(r, ctx, default);
        var values = r.Headers.GetValues("Cookie").ToList();
        Assert.Contains("existing=1", values);
        Assert.Contains("sid=abc123", values);
    }

    [Fact]
    public async Task Delegate_source_is_called_on_every_apply()
    {
        var n = 0;
        var p = new ApiKeyCredentialProvider(_ => ValueTask.FromResult($"k{++n}"));
        var ctx = Ctx(SecurityScheme.ApiKey("X-Api-Key"));
        var r1 = Req();
        var r2 = Req();
        await p.ApplyAsync(r1, ctx, default);
        await p.ApplyAsync(r2, ctx, default);
        Assert.Equal("k1", r1.Headers.GetValues("X-Api-Key").Single());
        Assert.Equal("k2", r2.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public void Query_location_is_refused_by_CanHandle()
    {
        var p = new ApiKeyCredentialProvider("k");
        Assert.False(p.CanHandle(Ctx(SecurityScheme.ApiKey("api_key", "query"))));
    }

    [Fact]
    public void Non_api_key_scheme_is_refused()
    {
        var p = new ApiKeyCredentialProvider("k");
        Assert.False(p.CanHandle(Ctx(SecurityScheme.Bearer())));
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("host")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Connection")]
    [InlineData("Upgrade")]
    [InlineData("Cookie")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Content-Length")]
    [InlineData("content-type")]
    [InlineData("A2A-Version")]
    [InlineData("a2a-extensions")]
    public void Sensitive_header_names_are_refused(string name)
    {
        var p = new ApiKeyCredentialProvider("k");
        Assert.False(p.CanHandle(Ctx(SecurityScheme.ApiKey(name))));
    }

    [Fact]
    public void Authorization_header_is_allowed()
    {
        var p = new ApiKeyCredentialProvider("k");
        Assert.True(p.CanHandle(Ctx(SecurityScheme.ApiKey("Authorization"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad name")]
    [InlineData("bad:name")]
    [InlineData("bad\r\nname")]
    [InlineData("na(me")]
    [InlineData("naïve")]
    public void Invalid_token_names_are_refused(string name)
    {
        var p = new ApiKeyCredentialProvider("k");
        Assert.False(p.CanHandle(Ctx(SecurityScheme.ApiKey(name))));
        Assert.False(p.CanHandle(Ctx(SecurityScheme.ApiKey(name, "cookie"))));
    }

    [Fact]
    public void Scheme_name_filter_applies()
    {
        var p = new ApiKeyCredentialProvider("k", "wanted");
        var scheme = SecurityScheme.ApiKey("X-Api-Key");
        Assert.True(p.CanHandle(Ctx(scheme, "wanted")));
        Assert.False(p.CanHandle(Ctx(scheme, "other")));
    }

    [Theory]
    [InlineData("", "header")]
    [InlineData("has\nnewline-SECRET", "header")]
    [InlineData("has\0nul-SECRET", "header")]
    [InlineData("a;b-SECRET", "cookie")]
    [InlineData("a,b-SECRET", "cookie")]
    [InlineData("a b-SECRET", "cookie")]
    [InlineData("", "cookie")]
    public async Task Bad_key_values_are_refused_without_leaking_the_key(string key, string location)
    {
        var p = new ApiKeyCredentialProvider(key);
        var ctx = Ctx(SecurityScheme.ApiKey("X-Key", location));
        var ex = await Assert.ThrowsAsync<A2AException>(async () => await p.ApplyAsync(Req(), ctx, default));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.DoesNotContain("SECRET", ex.Message);
        if (key.Length > 0) Assert.DoesNotContain(key, ex.Message);
    }

    [Theory]
    [InlineData("Host", "header")]
    [InlineData("Content-Length", "header")]
    [InlineData("api_key", "query")]
    [InlineData("X-Key", "body")]
    public async Task Apply_enforces_the_CanHandle_policy(string name, string location)
    {
        var p = new ApiKeyCredentialProvider("SECRET");
        var r = Req();
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
            await p.ApplyAsync(r, Ctx(SecurityScheme.ApiKey(name, location)), default));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.DoesNotContain("SECRET", ex.Message);
        Assert.Empty(r.Headers);
    }

    [Fact]
    public async Task Apply_enforces_the_scheme_name_filter()
    {
        var p = new ApiKeyCredentialProvider("k", "wanted");
        var r = Req();
        await Assert.ThrowsAsync<A2AException>(async () =>
            await p.ApplyAsync(r, Ctx(SecurityScheme.ApiKey("X-Key"), "other"), default));
        Assert.Empty(r.Headers);
    }

    [Fact]
    public async Task Applying_twice_replaces_the_same_cookie_and_keeps_others()
    {
        var key = "one";
        var p = new ApiKeyCredentialProvider(_ => ValueTask.FromResult(key));
        var ctx = Ctx(SecurityScheme.ApiKey("sid", "cookie"));
        var r = Req();
        r.Headers.TryAddWithoutValidation("Cookie", "other=1");
        await p.ApplyAsync(r, ctx, default);
        key = "two";
        await p.ApplyAsync(r, ctx, default);
        var values = r.Headers.GetValues("Cookie").ToList();
        Assert.Single(values, v => v.StartsWith("sid=", StringComparison.Ordinal));
        Assert.Contains("sid=two", values);
        Assert.Contains("other=1", values);
    }

    [Fact]
    public void Constructors_validate_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new ApiKeyCredentialProvider((string)null!));
        Assert.Throws<ArgumentNullException>(() => new ApiKeyCredentialProvider((Func<CancellationToken, ValueTask<string>>)null!));
    }
}
