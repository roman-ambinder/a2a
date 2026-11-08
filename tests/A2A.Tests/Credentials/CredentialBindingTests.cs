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

    private const string SigmaSource = "https://σagent.example/.well-known/agent-card.json";

    private static void AssertSigmaHostsDiffer()
    {
        // Guard against a vacuous test: the two names fold together by ordinal ignore case but are dialed as different hosts.
        Assert.NotEqual(new Uri("https://σagent.example/").IdnHost, new Uri("https://ςagent.example/a2a").IdnHost);
    }

    [Fact]
    public void Sigma_and_final_sigma_are_different_origins()
    {
        AssertSigmaHostsDiffer();
        Assert.False(A2ACredentialBinding.IsSameOrigin(new Uri("https://σagent.example/"), new Uri("https://ςagent.example/a2a")));
        Assert.True(A2ACredentialBinding.IsSameOrigin(new Uri("https://σagent.example/"), new Uri("https://σagent.example/a2a")));

        var card = Card("https://ςagent.example/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var ex = Assert.Throws<A2AException>(() => Bind(card, [new RecordingProvider("p")], source: SigmaSource));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public void Unicode_host_and_its_punycode_form_are_the_same_origin()
    {
        Assert.True(A2ACredentialBinding.IsSameOrigin(new Uri("https://bücher.example/a"), new Uri("https://xn--bcher-kva.example/b")));
        Assert.True(A2ACredentialBinding.IsSameOrigin(new Uri("https://BÜCHER.example/a"), new Uri("https://xn--bcher-kva.example:443/b")));
    }

    [Fact]
    public void Ipv6_zone_ids_must_match()
    {
        Assert.False(A2ACredentialBinding.IsSameOrigin(new Uri("https://[fe80::1%25eth0]/"), new Uri("https://[fe80::1%25eth1]/")));
        Assert.True(A2ACredentialBinding.IsSameOrigin(new Uri("https://[fe80::1%25eth0]/"), new Uri("https://[fe80::1%25eth0]/x")));
    }

    [Fact]
    public void IsSameOrigin_compares_scheme_and_port_and_rejects_relative_uris()
    {
        Assert.False(A2ACredentialBinding.IsSameOrigin(new Uri("https://a.example/"), new Uri("http://a.example/")));
        Assert.False(A2ACredentialBinding.IsSameOrigin(new Uri("https://a.example/"), new Uri("https://a.example:8443/")));
        Assert.False(A2ACredentialBinding.IsSameOrigin(new Uri("/x", UriKind.Relative), new Uri("https://a.example/")));
        Assert.Throws<ArgumentNullException>(() => A2ACredentialBinding.IsSameOrigin(null!, new Uri("https://a.example/")));
    }

    [Fact]
    public void User_info_never_reaches_messages_or_CardOrigin()
    {
        var card = Card("https://user:hunter2@other.example/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var ex = Assert.Throws<A2AException>(() =>
            Bind(card, [new RecordingProvider("p")], source: "https://user:hunter2@agent.example/.well-known/agent-card.json"));
        Assert.DoesNotContain("hunter2", ex.Message);
        Assert.Contains("other.example", ex.Message);

        var ok = Card("https://agent.example/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var binding = Bind(ok, [new RecordingProvider("p")], source: "https://user:hunter2@agent.example/.well-known/agent-card.json");
        Assert.DoesNotContain("hunter2", binding.CardOrigin.ToString());
        Assert.DoesNotContain("hunter2", Assert.Single(binding.Schemes).CardOrigin.ToString());
        Assert.Equal("https://agent.example/", binding.CardOrigin.AbsoluteUri);

        var http = Card("http://agent.example/a2a", schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var plain = Assert.Throws<A2AException>(() =>
            Bind(http, [new RecordingProvider("p")], source: "http://user:hunter2@agent.example/.well-known/agent-card.json"));
        Assert.DoesNotContain("hunter2", plain.Message);
    }

    [Fact]
    public void Grpc_is_refused_by_default_and_allowed_for_a_grpc_channel()
    {
        var card = Card("https://agent.example:443/grpc", binding: ProtocolBindings.Grpc, schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var itf = card.SupportedInterfaces[0];
        var providers = new[] { new RecordingProvider("p") };
        var ex = Assert.Throws<NotSupportedException>(() => A2ACredentialBinding.Create(Resolved(card), itf, providers));
        Assert.Contains("forGrpcChannel", ex.Message);

        var b = A2ACredentialBinding.Create(Resolved(card), itf, providers, forGrpcChannel: true);
        Assert.Single(b.Schemes);
    }

    [Fact]
    public void Grpc_for_a_channel_still_refuses_another_origin_and_plain_http()
    {
        var other = Card("https://other.example/grpc", binding: ProtocolBindings.Grpc, schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var ex = Assert.Throws<A2AException>(() =>
            A2ACredentialBinding.Create(Resolved(other), other.SupportedInterfaces[0], [new RecordingProvider("p")], forGrpcChannel: true));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);

        var plain = Card("http://agent.example/grpc", binding: ProtocolBindings.Grpc, schemes: TwoSchemes(), requirements: [Req(("key", []))]);
        var http = Assert.Throws<A2AException>(() =>
            A2ACredentialBinding.Create(Resolved(plain, "http://agent.example/.well-known/agent-card.json"), plain.SupportedInterfaces[0],
                [new RecordingProvider("p")], forGrpcChannel: true));
        Assert.Equal(A2AErrorCode.InvalidRequest, http.Code);
    }

    [Fact]
    public void Null_entries_in_the_card_are_unsatisfiable_not_a_crash()
    {
        var nullRequirement = Card(schemes: TwoSchemes(), requirements: [null!]);
        Assert.Equal(A2AErrorCode.Unauthenticated, Assert.Throws<A2AException>(() => Bind(nullRequirement, [new RecordingProvider("p")])).Code);

        var nullSchemes = Card(schemes: TwoSchemes(), requirements: [new SecurityRequirement { Schemes = null! }]);
        Assert.Equal(A2AErrorCode.Unauthenticated, Assert.Throws<A2AException>(() => Bind(nullSchemes, [new RecordingProvider("p")])).Code);

        var nullScheme = Card(schemes: new() { ["k"] = null! }, requirements: [Req(("k", []))]);
        Assert.Equal(A2AErrorCode.Unauthenticated, Assert.Throws<A2AException>(() => Bind(nullScheme, [new RecordingProvider("p")])).Code);

        // A later, satisfiable requirement still wins over a null one.
        var mixed = Card(schemes: TwoSchemes(), requirements: [null!, Req(("key", []))]);
        Assert.Equal("key", Assert.Single(Bind(mixed, [new RecordingProvider("p")]).Schemes).SchemeName);
    }

    [Fact]
    public void Hostile_scheme_names_do_not_reach_the_message_raw()
    {
        var schemes = new Dictionary<string, SecurityScheme> { ["bad\r\nInjected: yes\u202Ename"] = SecurityScheme.ApiKey("X-Key") };
        var card = Card(schemes: schemes, requirements: [Req(("bad\r\nInjected: yes\u202Ename", []))]);
        var ex = Assert.Throws<A2AException>(() => Bind(card, [new RecordingProvider("none", _ => false)]));
        Assert.DoesNotContain('\r', ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\u202E', ex.Message);
        Assert.Contains("Injected", ex.Message);
    }
}
