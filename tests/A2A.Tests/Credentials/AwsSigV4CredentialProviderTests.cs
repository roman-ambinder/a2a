using A2A.Client;
using A2A.Client.Credentials;
using A2A.Client.Identity.Aws;
using Amazon.Runtime;

namespace A2A.Tests.Credentials;

/// <summary>
///     <see cref="AwsSigV4CredentialProvider" />: argument checks, the scheme name it requires, and the error when it is
///     asked to sign for a second agent. Card selection and the signature itself are covered in
///     <see cref="AwsSigV4Tests" />.
/// </summary>
public class AwsSigV4CredentialProviderTests
{
    private const string Origin = "https://abc123.execute-api.us-east-1.amazonaws.com/";

    private static AwsSigV4Options Options(Action<AwsSigV4Options>? configure = null)
    {
        var options = new AwsSigV4Options
        {
            Region = "us-east-1", Service = "execute-api", SchemeName = "aws",
            Credentials = new BasicAWSCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY")
        };
        configure?.Invoke(options);
        return options;
    }

    private static A2ACredentialContext Context(string origin = Origin, string name = "aws") =>
        new(name, new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "AWS4-HMAC-SHA256" }), [], new Uri(origin));

    [Fact]
    public void Options_are_required()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new AwsSigV4CredentialProvider(null!));
        Assert.Equal("options", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Scheme_name_is_required(string? schemeName)
    {
        var ex = Assert.Throws<ArgumentException>(() => new AwsSigV4CredentialProvider(Options(o => o.SchemeName = schemeName)));
        Assert.StartsWith("SchemeName is required: no A2A scheme type means SigV4, so name the card scheme to handle.", ex.Message);
        Assert.Equal("options", ex.ParamName);
    }

    [Fact]
    public void Can_handle_needs_a_context()
    {
        var provider = new AwsSigV4CredentialProvider(Options());
        var ex = Assert.Throws<ArgumentNullException>(() => provider.CanHandle(null!));
        Assert.Equal("context", ex.ParamName);
    }

    [Fact]
    public async Task Apply_needs_a_request()
    {
        var provider = new AwsSigV4CredentialProvider(Options());
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            provider.ApplyAsync(null!, Context(), TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("request", ex.ParamName);
    }

    [Fact]
    public async Task Apply_needs_a_context()
    {
        var provider = new AwsSigV4CredentialProvider(Options());
        using var request = new HttpRequestMessage(HttpMethod.Get, Origin);
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            provider.ApplyAsync(request, null!, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("context", ex.ParamName);
    }

    [Fact]
    public async Task Apply_for_a_second_agent_is_refused_and_names_its_origin()
    {
        var provider = new AwsSigV4CredentialProvider(Options());
        var ct = TestContext.Current.CancellationToken;
        using (var first = new HttpRequestMessage(HttpMethod.Get, Origin))
            await provider.ApplyAsync(first, Context(), ct); // binds to the first origin

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://other.example:8443/a2a");
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            provider.ApplyAsync(request, Context("https://other.example:8443/"), ct).AsTask());

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal(
            "This provider is bound to another agent, so it will not sign requests to https://other.example:8443. Use one provider per agent.",
            ex.Message);
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Apply_to_a_configured_origin_signs_and_another_origin_is_refused()
    {
        var provider = new AwsSigV4CredentialProvider(Options(o => o.AgentOrigin = new Uri(Origin)));
        var ct = TestContext.Current.CancellationToken;

        using var signed = new HttpRequestMessage(HttpMethod.Get, Origin + "a2a");
        await provider.ApplyAsync(signed, Context(), ct);
        Assert.True(signed.Headers.TryGetValues("Authorization", out var values));
        Assert.StartsWith("AWS4-HMAC-SHA256 ", Assert.Single(values));

        Assert.False(provider.CanHandle(Context("https://other.example/")));
        using var refused = new HttpRequestMessage(HttpMethod.Get, "https://other.example/a2a");
        await Assert.ThrowsAsync<A2AException>(() => provider.ApplyAsync(refused, Context("https://other.example/"), ct).AsTask());
    }
}
