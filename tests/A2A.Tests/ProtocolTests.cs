using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Sse;
using A2A.Json;
using A2A.JsonRpc;
using A2A.Server.Push;
using A2A.Security.Http;
using A2A.Signing;

namespace A2A.Tests;

public class JsonTests
{
    [Fact]
    public void Options_have_no_reflection_fallback()
    {
        var resolver = A2AJson.Options.TypeInfoResolver!;
        Assert.IsNotType<System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver>(resolver);
        // The generated context is the only source of metadata: it serves model types, and nothing else is served.
        Assert.NotNull(A2AJsonContext.Default.GetTypeInfo(typeof(AgentCard)));
        Assert.NotNull(resolver.GetTypeInfo(typeof(AgentCard), A2AJson.Options));
        Assert.Null(resolver.GetTypeInfo(new { a = 1 }.GetType(), A2AJson.Options));
        Assert.Throws<NotSupportedException>(() => A2AJson.Serialize(new { a = 1 }));
    }

    [Fact]
    public void Relaxed_required_members_still_apply_to_the_generated_context()
    {
        var info = (System.Text.Json.Serialization.Metadata.JsonTypeInfo)A2AJson.Options.GetTypeInfo(typeof(AgentCard));
        Assert.All(info.Properties.Where(p => p.Name is "name" or "description" or "version" or "supportedInterfaces" or "capabilities"
            or "defaultInputModes" or "defaultOutputModes" or "skills"), p => Assert.False(p.IsRequired, p.Name));
        Assert.NotNull(A2AJson.Deserialize<AgentCard>("{}"));
    }

    [Fact]
    public void Member_names_are_case_sensitive()
    {
        Assert.Throws<JsonException>(() => A2AJson.Deserialize<Message>("{\"MessageId\":\"m1\",\"Parts\":[{\"text\":\"x\"}],\"role\":\"ROLE_USER\"}"));
        Assert.Null(A2AJson.Deserialize<AgentCard>("{\"IconUrl\":\"https://x/i.png\"}").IconUrl);
        Assert.Equal("https://x/i.png", A2AJson.Deserialize<AgentCard>("{\"iconUrl\":\"https://x/i.png\"}").IconUrl);
    }
    [Fact]
    public void Enums_use_protojson_names_and_unknown_values_read_as_unspecified()
    {
        var json = A2AJson.Serialize(new AgentTaskStatus { State = TaskState.InputRequired });
        Assert.Contains("\"TASK_STATE_INPUT_REQUIRED\"", json);
        Assert.Equal(TaskState.Unspecified, A2AJson.Deserialize<AgentTaskStatus>("{\"state\":\"TASK_STATE_FROM_THE_FUTURE\"}").State);
        Assert.Equal(TaskState.Working, A2AJson.Deserialize<AgentTaskStatus>("{\"state\":2}").State);
    }

    [Fact]
    public void Timestamps_are_utc_with_milliseconds()
    {
        var status = new AgentTaskStatus { State = TaskState.Working, Timestamp = new DateTimeOffset(2025, 10, 28, 12, 30, 0, 5, TimeSpan.FromHours(2)) };
        Assert.Contains("\"timestamp\":\"2025-10-28T10:30:00.005Z\"", A2AJson.Serialize(status));
    }

    [Fact]
    public void Parts_round_trip_each_content_kind_and_omit_nulls()
    {
        var msg = new Message
        {
            MessageId = "m1",
            Role = Role.User,
            Parts = [Part.FromText("hi"), Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream", "a.bin"), Part.FromUrl(new Uri("https://x/y.png"), "image/png"), Part.FromData(JsonSerializer.SerializeToElement(new { a = 1 }))],
        };
        var json = A2AJson.Serialize(msg);
        Assert.DoesNotContain("null", json);
        Assert.Contains("\"raw\":\"AQID\"", json);
        Assert.Contains("\"role\":\"ROLE_USER\"", json);
        var back = A2AJson.Deserialize<Message>(json);
        Assert.IsType<TextPart>(back.Parts[0]);
        Assert.IsType<RawPart>(back.Parts[1]);
        Assert.IsType<UrlPart>(back.Parts[2]);
        var data = Assert.IsType<DataPart>(back.Parts[3]);
        Assert.Equal(1, data.Data.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Default_bools_are_omitted_but_optional_capabilities_kept()
    {
        var json = A2AJson.Serialize(new AgentCapabilities { Streaming = false });
        Assert.Equal("{\"streaming\":false}", json);
        Assert.DoesNotContain("append", A2AJson.Serialize(new TaskArtifactUpdateEvent { Artifact = new Artifact { ArtifactId = "a", Parts = [] } }));
    }

    [Fact]
    public void JsonRpc_error_prefers_errorinfo_reason()
    {
        var resp = JsonRpcResponse.Failure(JsonSerializer.SerializeToElement(1), new A2AException(A2AErrorCode.Unauthenticated));
        Assert.Equal(-32603, resp.Error!.Code);
        Assert.Equal(A2AErrorCode.Unauthenticated, resp.Error.ToException().Code);
        var tnf = JsonRpcResponse.Failure(null, A2AException.TaskNotFound("t1"));
        Assert.Equal(-32001, tnf.Error!.Code);
        Assert.Contains("TASK_NOT_FOUND", A2AJson.Serialize(tnf));
        Assert.Contains("\"id\":null", A2AJson.Serialize(tnf));
    }
}

public class CanonicalizationTests
{
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(1.0, "1")]
    [InlineData(4.5, "4.5")]
    [InlineData(0.002, "0.002")]
    [InlineData(0.000001, "0.000001")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e30, "1e+30")]
    [InlineData(123456789012345680000.0, "123456789012345680000")]
    [InlineData(333333333.3333333, "333333333.3333333")]
    [InlineData(9007199254740992.0, "9007199254740992")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(-1.5e-10, "-1.5e-10")]
    public void Numbers_follow_ecmascript_formatting(double value, string expected) =>
        Assert.Equal(expected, JsonCanonicalizer.FormatNumber(value));

    [Fact]
    public void Keys_sorted_by_utf16_and_strings_minimally_escaped()
    {
        var json = "{\"b\":1,\"a\":\"\\u0007é\\n\\\"\",\"€\":true,\"\\r\":null,\"A\":[3,{\"z\":1,\"y\":2}]}";
        Assert.Equal("{\"\\r\":null,\"A\":[3,{\"y\":2,\"z\":1}],\"a\":\"\\u0007é\\n\\\"\",\"b\":1,\"€\":true}", JsonCanonicalizer.CanonicalizeToString(json));
    }

    [Fact]
    public void Agent_card_normalization_matches_spec_example()
    {
        // Spec §8.4.1 example: explicit optional false kept, empty repeated field removed.
        var raw = """
        {"name":"Example Agent","description":"An example agent that echoes messages.",
         "supportedInterfaces":[{"url":"https://example.com/a2a/v1","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],
         "version":"1.0.0","capabilities":{"streaming":false,"pushNotifications":false,"extensions":[]},
         "defaultInputModes":["text/plain"],"defaultOutputModes":["text/plain"],
         "skills":[{"id":"echo","name":"Echo","description":"Echoes the input.","tags":["echo"]}]}
        """;
        var expected = "{\"capabilities\":{\"pushNotifications\":false,\"streaming\":false},\"defaultInputModes\":[\"text/plain\"],\"defaultOutputModes\":[\"text/plain\"],\"description\":\"An example agent that echoes messages.\",\"name\":\"Example Agent\",\"skills\":[{\"description\":\"Echoes the input.\",\"id\":\"echo\",\"name\":\"Echo\",\"tags\":[\"echo\"]}],\"supportedInterfaces\":[{\"protocolBinding\":\"JSONRPC\",\"protocolVersion\":\"1.0\",\"url\":\"https://example.com/a2a/v1\"}],\"version\":\"1.0.0\"}";
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(AgentCardCanonicalizer.Canonicalize(JsonNode.Parse(raw)!.AsObject())));
    }
}

public class SigningTests
{
    internal static AgentCard SampleCard() => new()
    {
        Name = "Signer", Description = "d", Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }],
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "s", Name = "S", Description = "x", Tags = ["t"] }],
    };

    public static TheoryData<string> Algorithms => new() { "ES256", "ES384", "ES512", "RS256", "PS256" };

    private static AgentCardSigningKey NewKey(string alg, string kid = "k1", string? jku = null) => alg switch
    {
        "ES256" => new(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), kid, alg, jku),
        "ES384" => new(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384), kid, alg, jku),
        "ES512" => new(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP521), kid, alg, jku),
        _ => new(System.Security.Cryptography.RSA.Create(2048), kid, alg, jku),
    };

    internal const string Origin = "https://agent.example";
    internal static readonly Uri CardUri = new(Origin + "/.well-known/agent-card.json");

    /// <summary>A policy that trusts <paramref name="keys"/> for the origin of <see cref="CardUri"/>.</summary>
    internal static CardTrustPolicy Trust(params Jwk[] keys)
    {
        var policy = new CardTrustPolicy();
        policy.TrustedKeys[Origin] = new JwkSet { Keys = [.. keys] };
        return policy;
    }

    [Theory]
    [MemberData(nameof(Algorithms))]
    public async Task Sign_then_verify_succeeds_and_tampering_fails(string alg)
    {
        var key = NewKey(alg);
        var signed = AgentCardSigner.Sign(SampleCard(), key);
        var verifier = new AgentCardVerifier(Trust(key.ToPublicJwk()));

        var json = A2AJson.Serialize(signed);
        var ok = await verifier.VerifyAsync(json, CardUri);
        Assert.Equal(AgentCardVerificationStatus.Verified, ok.Status);
        Assert.Equal("k1", ok.KeyId);

        var tampered = json.Replace("\"Signer\"", "\"Evil\"");
        var result = await verifier.VerifyAsync(tampered, CardUri);
        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Unsigned_card_reports_unsigned()
    {
        var verifier = new AgentCardVerifier(new CardTrustPolicy());
        Assert.Equal(AgentCardVerificationStatus.Unsigned, (await verifier.VerifyAsync(SampleCard(), CardUri)).Status);
    }

    [Fact]
    public async Task Alg_none_and_unknown_kid_are_rejected()
    {
        var key = NewKey("ES256");
        var signed = AgentCardSigner.Sign(SampleCard(), key);
        var noneHeader = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "none", ["typ"] = "JOSE", ["kid"] = "k1" }, A2AJson.GetTypeInfo<Dictionary<string, string>>()));
        signed.Signatures![0].Protected = noneHeader;
        signed.Signatures[0].Signature = "AA";
        var result = await new AgentCardVerifier(Trust(key.ToPublicJwk())).VerifyAsync(signed, CardUri);
        Assert.Equal(AgentCardVerificationStatus.Failed, result.Status);
        Assert.Contains("not allowed", result.Reason);

        // An unknown kid means no trusted key applies: Unverified, not Failed.
        var other = AgentCardSigner.Sign(SampleCard(), key);
        var r2 = await new AgentCardVerifier(new CardTrustPolicy()).VerifyAsync(other, CardUri);
        Assert.Equal(AgentCardVerificationStatus.Unverified, r2.Status);
        Assert.Contains("no trusted key", r2.Reason);
    }

    [Fact]
    public async Task Fields_outside_the_v1_schema_are_not_covered_by_the_signature()
    {
        // a2a-python 1.2.1 and a2a-js 1.3.0 parse the card into the v1.0 schema before canonicalizing, so unknown
        // members (including the v0.3 compatibility fields a2a-python serves) never reach the signed payload.
        var key = NewKey("ES256");
        var node = AgentCardCanonicalizer.ToNode(SampleCard());
        node["futureField"] = new JsonObject { ["x"] = 1 };
        var sig = SignRaw(node, key);
        node["signatures"] = new JsonArray(JsonSerializer.SerializeToNode(sig, A2AJson.Options));
        var raw = node.ToJsonString();

        var verifier = new AgentCardVerifier(Trust(key.ToPublicJwk()));
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(raw, CardUri)).Status);
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(raw.Replace("\"x\":1", "\"x\":2"), CardUri)).Status);
        Assert.Equal(AgentCardVerificationStatus.Verified, (await verifier.VerifyAsync(A2AJson.Deserialize<AgentCard>(raw), CardUri)).Status);
    }

    private static AgentCardSignature SignRaw(JsonObject card, AgentCardSigningKey key)
    {
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = key.Algorithm, ["typ"] = "JOSE", ["kid"] = key.KeyId }, A2AJson.GetTypeInfo<Dictionary<string, string>>()));
        var input = System.Text.Encoding.ASCII.GetBytes(header + "." + Base64Url.Encode(AgentCardCanonicalizer.Canonicalize(card)));
        return new AgentCardSignature { Protected = header, Signature = Base64Url.Encode(JwsAlgorithms.Sign(key.Algorithm, key.Key, input)) };
    }

    [Fact]
    public async Task Jwks_resolver_refuses_cross_host_and_insecure_jku()
    {
        var policy = new CardTrustPolicy();
        policy.AllowJkuHost(Origin, "agent.example");
        var verifier = new AgentCardVerifier(policy, new GuardedJwksFetcher(new HttpMessageInvoker(new ThrowingHandler())));

        var crossHost = AgentCardSigner.Sign(SampleCard(), NewKey("ES256", jku: "https://evil.example/jwks.json"));
        Assert.Equal(AgentCardVerificationStatus.Unverified, (await verifier.VerifyAsync(crossHost, CardUri)).Status);
        var insecure = AgentCardSigner.Sign(SampleCard(), NewKey("ES256", jku: "http://agent.example/jwks.json"));
        Assert.Equal(AgentCardVerificationStatus.Unverified, (await verifier.VerifyAsync(insecure, CardUri)).Status);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("must not fetch");
    }
}

public class PushUrlValidatorTests
{
    [Theory]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("https://169.254.169.254/latest")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[fd00::1]/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("http://example.com/hook")]
    [InlineData("https://user:pw@example.com/hook")]
    public void Rejects_unsafe_urls(string url) =>
        Assert.Equal(A2AErrorCode.InvalidParams, Assert.Throws<A2AException>(() => PushUrlValidator.Validate(url, new PushNotificationOptions())).Code);

    [Fact]
    public void Accepts_public_https() => PushUrlValidator.Validate("https://example.com/hook", new PushNotificationOptions());

    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("172.20.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    public void Classifies_addresses(string ip, bool isPrivate) =>
        Assert.Equal(isPrivate, PushUrlValidator.IsPrivate(System.Net.IPAddress.Parse(ip)));
}
