using A2A.Client.Credentials;
using Xunit;
using static A2A.Tests.Credentials.Cards;

namespace A2A.Tests.Credentials;

public class CredentialBindingTests
{
    private static bool Is<T>(A2ACredentialContext c) where T : SecurityScheme => c.Scheme is T;

    private static A2ACredentialBinding Bind(AgentCard card, IEnumerable<IA2ACredentialProvider> providers, bool requireHttps = true, string? source = null) =>
        A2ACredentialBinding.Create(source is null ? Resolved(card) : Resolved(card, source), card.SupportedInterfaces[0], providers, requireHttps);

    [Fact]
    public void First_satisfied_requirement_in_card_order_wins()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [Req(("key", [])), Req(("oauth", []))]);
        var b = Bind(card, [new RecordingProvider("p")]);
        Assert.Equal("key", Assert.Single(b.Schemes).SchemeName);
    }

    [Fact]
    public void And_requirement_needs_a_provider_for_every_scheme_and_partial_match_is_skipped()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [Req(("key", []), ("oauth", [])), Req(("bearer", []))]);
        var onlyKey = new RecordingProvider("k", c => Is<ApiKeyScheme>(c));
        var bearer = new RecordingProvider("b", c => Is<HttpAuthScheme>(c));
        var b = Bind(card, [onlyKey, bearer]);
        Assert.Equal("bearer", Assert.Single(b.Schemes).SchemeName);

        var both = Bind(card, [onlyKey, new RecordingProvider("o", c => Is<OAuth2Scheme>(c))]);
        Assert.Equal(["key", "oauth"], both.Schemes.Select(s => s.SchemeName).Order());
    }

    [Fact]
    public void First_provider_in_registration_order_wins()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var first = new RecordingProvider("first");
        var second = new RecordingProvider("second");
        var b = Bind(card, [first, second]);
        Assert.Same(first, b.Pairs.Single().Provider);
    }

    [Fact]
    public void Empty_requirement_first_then_oauth_with_provider_uses_oauth()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [new SecurityRequirement(), Req(("oauth", []))]);
        var b = Bind(card, [new RecordingProvider("o", c => Is<OAuth2Scheme>(c))]);
        Assert.Equal("oauth", Assert.Single(b.Schemes).SchemeName);
    }

    [Fact]
    public void Empty_requirement_and_no_matching_provider_is_anonymous()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [new SecurityRequirement(), Req(("oauth", []))]);
        var b = Bind(card, [new RecordingProvider("none", _ => false)]);
        Assert.Empty(b.Schemes);
    }

    [Fact]
    public void No_requirements_declared_is_anonymous()
    {
        var b = Bind(Card(schemes: TwoSchemes()), [new RecordingProvider("p")]);
        Assert.Empty(b.Schemes);
        Assert.Empty(Bind(Card(), [new RecordingProvider("p")]).Schemes);
    }

    [Fact]
    public void No_match_with_a_non_empty_requirement_is_unauthenticated_and_names_the_schemes()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [Req(("oauth", []))]);
        var ex = Assert.Throws<A2AException>(() => Bind(card, [new RecordingProvider("none", _ => false)]));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Contains("key", ex.Message);
        Assert.Contains("oauth", ex.Message);
        Assert.Contains("bearer", ex.Message);
        Assert.Contains("TokenEndpoint", ex.Message);
        Assert.Contains("query", ex.Message);
    }

    [Fact]
    public void Requirement_naming_an_undeclared_scheme_is_skipped()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [Req(("ghost", [])), Req(("key", []))]);
        Assert.Equal("key", Assert.Single(Bind(card, [new RecordingProvider("p")]).Schemes).SchemeName);

        var onlyGhost = Card(schemes: TwoSchemes(), requirements: [Req(("ghost", []))]);
        Assert.Equal(A2AErrorCode.Unauthenticated, Assert.Throws<A2AException>(() => Bind(onlyGhost, [new RecordingProvider("p")])).Code);
    }

    [Fact]
    public void Scopes_in_the_context_come_from_the_requirement()
    {
        var card = Card(schemes: TwoSchemes(), requirements: [Req(("oauth", ["read", "write"]))]);
        var ctx = Assert.Single(Bind(card, [new RecordingProvider("p")]).Schemes);
        Assert.Equal(["read", "write"], ctx.Scopes);
        Assert.Equal("https://agent.example", ctx.CardOrigin.GetLeftPart(UriPartial.Authority));
        Assert.Equal("oauth", ctx.SchemeName);
    }

    [Theory]
    [InlineData("https://other.example/a2a")]
    [InlineData("https://agent.example:8443/a2a")]
    [InlineData("http://agent.example/a2a")]
    public void Interface_on_another_origin_is_refused(string url)
    {
        var card = Card(url, schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var ex = Assert.Throws<A2AException>(() => Bind(card, [new RecordingProvider("p")], requireHttps: false));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("agent.example", ex.Message);
    }

    [Fact]
    public void Same_origin_with_explicit_default_port_is_accepted()
    {
        var card = Card("https://agent.example:443/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        Assert.Single(Bind(card, [new RecordingProvider("p")]).Schemes);
    }

    [Fact]
    public void Other_origin_is_fine_when_anonymous()
    {
        var card = Card("https://other.example/a2a");
        Assert.Empty(Bind(card, [new RecordingProvider("p")]).Schemes);
    }

    [Fact]
    public void Plain_http_on_a_non_loopback_origin_is_refused_and_loopback_or_opt_out_is_accepted()
    {
        var card = Card("http://agent.example/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var src = "http://agent.example/.well-known/agent-card.json";
        var ex = Assert.Throws<A2AException>(() => Bind(card, [new RecordingProvider("p")], source: src));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Single(Bind(card, [new RecordingProvider("p")], requireHttps: false, source: src).Schemes);

        var loop = Card("http://localhost:5000/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        Assert.Single(Bind(loop, [new RecordingProvider("p")], source: "http://localhost:5000/.well-known/agent-card.json").Schemes);
    }

    [Fact]
    public void Grpc_is_refused_with_credentials_and_allowed_when_anonymous()
    {
        var withCreds = Card(binding: ProtocolBindings.Grpc, schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var ex = Assert.Throws<NotSupportedException>(() => Bind(withCreds, [new RecordingProvider("p")]));
        Assert.Contains("A2ACredentialHandler", ex.Message);
        Assert.Contains("HttpHandler", ex.Message);

        Assert.Empty(Bind(Card(binding: ProtocolBindings.Grpc), [new RecordingProvider("p")]).Schemes);
    }
}
