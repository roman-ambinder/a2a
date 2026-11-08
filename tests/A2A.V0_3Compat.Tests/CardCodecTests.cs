using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;
using A2A.Signing;

namespace A2A.V0_3Compat.Tests;

/// <summary>Agent Card conversion in both directions, security included (a2a-go #556, a2a-python #1418).</summary>
public class CardCodecTests
{
    private static JsonObject V1(AgentCard card) => AgentCardCanonicalizer.ToNode(card);

    private static AgentCard SecureCard()
    {
        var card = new AgentCard
        {
            Name = "Secure",
            Description = "Every scheme",
            Version = "2.0.0",
            SupportedInterfaces =
            [
                new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
                new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
                new AgentInterface { Url = "https://agent.example:50051", ProtocolBinding = ProtocolBindings.Grpc },
            ],
            Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true, ExtendedAgentCard = true },
            SecuritySchemes = new()
            {
                ["key"] = SecurityScheme.ApiKey("X-Key", "query", "API key"),
                ["bearer"] = SecurityScheme.Bearer("JWT", "Bearer token"),
                ["oauth"] = new OAuth2Scheme(new OAuth2SecurityScheme
                {
                    Description = "OAuth",
                    Oauth2MetadataUrl = "https://auth.example/.well-known/oauth-authorization-server",
                    Flows = new OAuthFlows
                    {
                        AuthorizationCode = new AuthorizationCodeOAuthFlow
                        {
                            AuthorizationUrl = "https://auth.example/authorize", TokenUrl = "https://auth.example/token",
                            RefreshUrl = "https://auth.example/refresh", Scopes = new() { ["read"] = "Read" }, PkceRequired = true,
                        },
                        ClientCredentials = new ClientCredentialsOAuthFlow { TokenUrl = "https://auth.example/token", Scopes = new() { ["admin"] = "Admin" } },
#pragma warning disable CS0618 // the deprecated flows still exist in v0.3
                        Implicit = new ImplicitOAuthFlow { AuthorizationUrl = "https://auth.example/authorize", Scopes = new() { ["imp"] = "Implicit" } },
                        Password = new PasswordOAuthFlow { TokenUrl = "https://auth.example/token", Scopes = new() { ["pw"] = "Password" } },
#pragma warning restore CS0618
                        DeviceCode = new DeviceCodeOAuthFlow { DeviceAuthorizationUrl = "https://auth.example/device", TokenUrl = "https://auth.example/token" },
                    },
                }),
                ["oidc"] = new OpenIdConnectScheme(new OpenIdConnectSecurityScheme { OpenIdConnectUrl = "https://auth.example/.well-known/openid-configuration", Description = "OIDC" }),
                ["mtls"] = new MutualTlsScheme(new MutualTlsSecurityScheme { Description = "Client certificate" }),
            },
            SecurityRequirements =
            [
                new SecurityRequirement { Schemes = new() { ["bearer"] = new StringList() } },
                new SecurityRequirement { Schemes = new() { ["oauth"] = new StringList { List = ["read", "admin"] }, ["key"] = new StringList() } },
            ],
            DefaultInputModes = ["text/plain"],
            DefaultOutputModes = ["text/plain"],
            Skills =
            [
                new AgentSkill
                {
                    Id = "s1", Name = "Skill", Description = "Does it", Tags = ["t"], Examples = ["e"], InputModes = ["a/b"], OutputModes = ["c/d"],
                    SecurityRequirements = [new SecurityRequirement { Schemes = new() { ["oauth"] = new StringList { List = ["read"] } } }],
                },
            ],
        };
        return card;
    }

    private static JsonObject ToV03(AgentCard card, bool? push = null)
    {
        Assert.True(V03CardCodec.TryToV03(V1(card), push, out var v03, out var problem), problem);
        return v03;
    }

    [Fact]
    public void All_five_scheme_types_cross_over_with_flows_scopes_and_security()
    {
        using var recorder = new MeterRecorder();
        var v03 = ToV03(SecureCard());

        Assert.Equal("0.3.0", v03["protocolVersion"]!.GetValue<string>());
        Assert.Equal("https://agent.example/a2a", v03["url"]!.GetValue<string>());
        Assert.Equal("JSONRPC", v03["preferredTransport"]!.GetValue<string>());
        // gRPC is never listed.
        var transports = v03["additionalInterfaces"]!.AsArray().Select(i => i!["transport"]!.GetValue<string>()).ToList();
        Assert.Equal(["JSONRPC", "HTTP+JSON"], transports);

        var schemes = v03["securitySchemes"]!.AsObject();
        Assert.True(Nodes.Same(Nodes.Parse("""{"type":"apiKey","in":"query","name":"X-Key","description":"API key"}"""), schemes["key"]));
        Assert.True(Nodes.Same(Nodes.Parse("""{"type":"http","scheme":"Bearer","bearerFormat":"JWT","description":"Bearer token"}"""), schemes["bearer"]));
        Assert.True(Nodes.Same(Nodes.Parse("""{"type":"openIdConnect","openIdConnectUrl":"https://auth.example/.well-known/openid-configuration","description":"OIDC"}"""), schemes["oidc"]));
        Assert.True(Nodes.Same(Nodes.Parse("""{"type":"mutualTLS","description":"Client certificate"}"""), schemes["mtls"]));

        var oauth = schemes["oauth"]!;
        Assert.Equal("oauth2", oauth["type"]!.GetValue<string>());
        Assert.Equal("OAuth", oauth["description"]!.GetValue<string>());
        Assert.Equal("https://auth.example/.well-known/oauth-authorization-server", oauth["oauth2MetadataUrl"]!.GetValue<string>());
        var flows = oauth["flows"]!.AsObject();
        Assert.True(Nodes.Same(Nodes.Parse("""{"authorizationUrl":"https://auth.example/authorize","tokenUrl":"https://auth.example/token","refreshUrl":"https://auth.example/refresh","scopes":{"read":"Read"}}"""), flows["authorizationCode"]));
        Assert.Equal("Admin", flows["clientCredentials"]!["scopes"]!["admin"]!.GetValue<string>());
        Assert.Equal("Implicit", flows["implicit"]!["scopes"]!["imp"]!.GetValue<string>());
        Assert.Equal("Password", flows["password"]!["scopes"]!["pw"]!.GetValue<string>());
        Assert.Null(flows["deviceCode"]);

        // security at card and skill level
        Assert.True(Nodes.Same(Nodes.Parse("""[{"bearer":[]},{"oauth":["read","admin"],"key":[]}]"""), v03["security"]));
        Assert.True(Nodes.Same(Nodes.Parse("""[{"oauth":["read"]}]"""), v03["skills"]![0]!["security"]));
        Assert.True(v03["supportsAuthenticatedExtendedCard"]!.GetValue<bool>());

        var dropped = recorder.DroppedFields().ToList();
        Assert.Contains("oauth.deviceCode", dropped);
        Assert.Contains("oauth.pkceRequired", dropped);
    }

    [Fact]
    public void A_scheme_with_only_a_device_code_flow_stays_listed_with_empty_flows()
    {
        var card = SecureCard();
        card.SecuritySchemes = new()
        {
            ["device"] = new OAuth2Scheme(new OAuth2SecurityScheme
            {
                Flows = new OAuthFlows { DeviceCode = new DeviceCodeOAuthFlow { DeviceAuthorizationUrl = "https://auth.example/device", TokenUrl = "https://auth.example/token" } },
            }),
        };
        card.SecurityRequirements = [new SecurityRequirement { Schemes = new() { ["device"] = new StringList() } }];
        card.Skills = [];

        var v03 = ToV03(card);
        // It must never read as anonymous access: the scheme and the requirement naming it are both still there.
        Assert.True(Nodes.Same(Nodes.Parse("""{"type":"oauth2","flows":{}}"""), v03["securitySchemes"]!["device"]));
        Assert.True(Nodes.Same(Nodes.Parse("""[{"device":[]}]"""), v03["security"]));
    }

    [Fact]
    public void Extended_card_form_says_no_push_notifications()
    {
        Assert.True(ToV03(SecureCard(), push: false)["capabilities"]!["pushNotifications"]!.GetValue<bool>() == false);
        Assert.True(ToV03(SecureCard())["capabilities"]!["pushNotifications"]!.GetValue<bool>());
    }

    [Fact]
    public void Tenant_interface_gets_the_tenant_in_its_url()
    {
        var card = SecureCard();
        card.SupportedInterfaces = [new AgentInterface { Url = "https://agent.example/a2a/", ProtocolBinding = ProtocolBindings.JsonRpc, Tenant = "ac me" }];
        Assert.Equal("https://agent.example/a2a/ac%20me", ToV03(card)["url"]!.GetValue<string>());
    }

    [Fact]
    public void A_card_without_a_jsonrpc_or_http_interface_gets_no_v03_fields()
    {
        var card = SecureCard();
        card.SupportedInterfaces = [new AgentInterface { Url = "https://agent.example:50051", ProtocolBinding = ProtocolBindings.Grpc }];
        var v1 = V1(card);
        Assert.False(V03CardCodec.TryToV03(v1, null, out _, out var problem));
        Assert.Contains("no JSONRPC or HTTP+JSON interface", problem);

        var before = v1.ToJsonString();
        var blended = V03CardCodec.Blend(v1);
        Assert.Equal(before, blended.ToJsonString());
        Assert.Null(blended["protocolVersion"]);
    }

    // ---- v0.3 -> v1.0 ----

    [Fact]
    public void Golden_v03_card_converts_to_v1_and_back()
    {
        var golden = Golden.Object("agent-card.json");
        var card = V03CardCodec.FromV03(golden);

        Assert.Equal("Golden Agent", card.Name);
        Assert.Equal(["JSONRPC", "HTTP+JSON"], card.SupportedInterfaces.Select(i => i.ProtocolBinding));
        Assert.All(card.SupportedInterfaces, i => Assert.Equal("0.3", i.ProtocolVersion));
        Assert.Equal(true, card.Capabilities.ExtendedAgentCard);
        Assert.Equal(false, card.Capabilities.PushNotifications);
        Assert.True(card.Capabilities.Extensions![0].Required);
        Assert.Null(card.Signatures); // v0.3 signatures cover v0.3 content: never carried
        Assert.Equal(5, card.SecuritySchemes!.Count);
        Assert.IsType<ApiKeyScheme>(card.SecuritySchemes["key"]);
        Assert.Equal("query", ((ApiKeyScheme)V03CardCodec.FromV03(WithKeyIn(golden, "query")).SecuritySchemes!["key"]).Value.Location);
        var oauth = Assert.IsType<OAuth2Scheme>(card.SecuritySchemes["oauth"]).Value;
        Assert.Equal("read", oauth.Flows.AuthorizationCode!.Scopes.Keys.Single());
        Assert.Equal("https://auth.example/refresh", oauth.Flows.AuthorizationCode.RefreshUrl);
        Assert.Equal("admin", oauth.Flows.ClientCredentials!.Scopes.Keys.Single());
        Assert.Equal(["bearer"], card.SecurityRequirements![0].Schemes.Keys);
        Assert.Equal(["read"], card.SecurityRequirements[1].Schemes["oauth"].List);
        Assert.Equal(["read"], card.Skills[0].SecurityRequirements![0].Schemes["oauth"].List);

        // and back: the same card, minus what v0.3 -> v1.0 drops
        var expected = Golden.Object("agent-card.json");
        expected["capabilities"]!.AsObject().Remove("stateTransitionHistory");
        expected.Remove("signatures");
        Assert.True(V03CardCodec.TryToV03(V1(card), null, out var back, out _));
        Assert.True(Nodes.Same(expected, back), Nodes.Show(back));
    }

    private static JsonObject WithKeyIn(JsonObject card, string location)
    {
        var copy = (JsonObject)card.DeepClone();
        copy["securitySchemes"]!["key"]!["in"] = location;
        return copy;
    }

    [Fact]
    public void Default_transport_is_jsonrpc_and_interfaces_are_not_repeated()
    {
        var card = V03CardCodec.FromV03(Nodes.Parse("""
            {"name":"n","description":"d","url":"https://a/x","version":"1","capabilities":{},"defaultInputModes":[],"defaultOutputModes":[],"skills":[],
             "additionalInterfaces":[{"url":"https://a/x","transport":"JSONRPC"},{"url":"https://a/grpc","transport":"grpc"}]}
            """).AsObject());
        Assert.Equal([("https://a/x", "JSONRPC"), ("https://a/grpc", "GRPC")], card.SupportedInterfaces.Select(i => (i.Url, i.ProtocolBinding)));
    }

    [Fact]
    public void An_unknown_scheme_type_refuses_the_card()
    {
        var card = Nodes.Parse("""{"name":"n","securitySchemes":{"x":{"type":"magic"}}}""").AsObject();
        var ex = Assert.Throws<JsonException>(() => V03CardCodec.FromV03(card));
        Assert.Contains("magic", ex.Message);
    }

    [Fact]
    public void State_transition_history_is_dropped_and_reported()
    {
        using var recorder = new MeterRecorder();
        V03CardCodec.ToV1(Golden.Object("agent-card.json"));
        var fields = recorder.DroppedFields().ToList();
        Assert.Contains("capabilities.stateTransitionHistory", fields);
        Assert.Contains("card.signatures", fields);
    }

    // ---- resolver normalizer ----

    [Fact]
    public void Normalizer_converts_a_v03_only_card_and_leaves_v1_and_blended_cards_alone()
    {
        var v03 = Golden.Object("agent-card.json");
        var normalized = V03CardCodec.NormalizeForResolver(v03);
        Assert.NotNull(normalized["supportedInterfaces"]);
        Assert.Null(normalized["signatures"]); // reads as unsigned

        var v1 = V1(SecureCard());
        Assert.Same(v1, V03CardCodec.NormalizeForResolver(v1));

        var blended = V03CardCodec.Blend(V1(SecureCard()));
        Assert.Same(blended, V03CardCodec.NormalizeForResolver(blended));

        var noVersion = Nodes.Parse("""{"name":"n"}""").AsObject();
        Assert.Same(noVersion, V03CardCodec.NormalizeForResolver(noVersion));
    }

    [Theory]
    [InlineData("0.2.5")]
    [InlineData("0.1")]
    [InlineData("0.30.0")]
    public void Other_zero_versions_are_refused(string version)
    {
        var card = Golden.Object("agent-card.json");
        card["protocolVersion"] = version;
        Assert.Throws<A2AException>(() => V03CardCodec.NormalizeForResolver(card));
    }

    [Theory]
    [InlineData("0.3")]
    [InlineData("0.3.0")]
    [InlineData("0.3.9")]
    public void Every_03_version_is_accepted(string version)
    {
        var card = Golden.Object("agent-card.json");
        card["protocolVersion"] = version;
        Assert.NotNull(V03CardCodec.NormalizeForResolver(card)["supportedInterfaces"]);
    }

    // ---- blended card ----

    [Fact]
    public void Blended_card_keeps_the_v1_members_and_adds_the_v03_ones()
    {
        var card = SecureCard();
        var v1 = V1(card);
        var blended = V03CardCodec.Blend((JsonObject)v1.DeepClone());

        Assert.Equal("0.3.0", blended["protocolVersion"]!.GetValue<string>());
        Assert.Equal("https://agent.example/a2a", blended["url"]!.GetValue<string>());
        Assert.NotNull(blended["additionalInterfaces"]);
        Assert.NotNull(blended["security"]);
        Assert.NotNull(blended["skills"]![0]!["security"]);
        Assert.True(blended["supportsAuthenticatedExtendedCard"]!.GetValue<bool>());
        // each scheme: the v1.0 wrapper and the flat v0.3 members side by side
        var bearer = blended["securitySchemes"]!["bearer"]!;
        Assert.NotNull(bearer["httpAuthSecurityScheme"]);
        Assert.Equal("http", bearer["type"]!.GetValue<string>());
        Assert.Equal("Bearer", bearer["scheme"]!.GetValue<string>());
        // the v1.0 capability value is kept
        Assert.True(blended["capabilities"]!["pushNotifications"]!.GetValue<bool>());

        // every v1.0 member is untouched
        foreach (var (name, value) in v1)
            if (name != "securitySchemes" && name != "skills") Assert.True(Nodes.Same(value, blended[name]), name);
    }

    [Fact]
    public void Blended_card_reads_in_the_v1_model()
    {
        var blended = V03CardCodec.Blend(V1(SecureCard()));
        var card = A2AJson.Deserialize<AgentCard>(blended.ToJsonString()); // tolerates the extra flat members and top-level fields
        Assert.Equal("Secure", card.Name);
        Assert.Equal(5, card.SecuritySchemes!.Count);
        Assert.IsType<HttpAuthScheme>(card.SecuritySchemes["bearer"]);
        Assert.IsType<OAuth2Scheme>(card.SecuritySchemes["oauth"]);
        Assert.Equal(3, card.SupportedInterfaces.Count);
    }

    [Fact]
    public async Task Blended_signed_card_still_verifies_for_v1_clients()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new AgentCardSigningKey(ec, "k1", "ES256");
        var signed = AgentCardSigner.Sign(SecureCard(), key);
        var blended = V03CardCodec.Blend(V1(signed));
        Assert.NotNull(blended["protocolVersion"]);

        const string origin = "https://agent.example";
        var policy = new CardTrustPolicy();
        policy.TrustedKeys[origin] = new JwkSet { Keys = [key.ToPublicJwk()] };
        var result = await new AgentCardVerifier(policy).VerifyAsync(blended, new Uri(origin + "/.well-known/agent-card.json"));
        Assert.Equal(AgentCardVerificationStatus.Verified, result.Status);

        // the added v0.3 members are not signed: changing one does not break the signature
        blended["url"] = "https://elsewhere.example/";
        Assert.Equal(AgentCardVerificationStatus.Verified, (await new AgentCardVerifier(policy).VerifyAsync(blended, new Uri(origin + "/.well-known/agent-card.json"))).Status);
        // but a v1.0 member is
        blended["name"] = "Tampered";
        Assert.NotEqual(AgentCardVerificationStatus.Verified, (await new AgentCardVerifier(policy).VerifyAsync(blended, new Uri(origin + "/.well-known/agent-card.json"))).Status);
    }

    [Fact]
    public void The_blend_does_not_run_twice_into_the_same_object_twice_differently()
    {
        // Blending a blended card changes nothing (the v0.3 members are there already).
        var once = V03CardCodec.Blend(V1(SecureCard()));
        var twice = V03CardCodec.Blend((JsonObject)once.DeepClone());
        Assert.True(Nodes.Same(once, twice));
    }
}
