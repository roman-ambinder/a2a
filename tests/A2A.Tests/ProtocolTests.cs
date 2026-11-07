using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Sse;
using A2A.Json;
using A2A.JsonRpc;
using A2A.Server.Push;
using A2A.Signing;

namespace A2A.Tests;

public class JsonTests
{
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
            Parts = [Part.FromText("hi"), Part.FromBytes([1, 2, 3], "application/octet-stream", "a.bin"), Part.FromUrl("https://x/y.png", "image/png"), Part.FromData(new { a = 1 })],
        };
        var json = A2AJson.Serialize(msg);
        Assert.DoesNotContain("null", json);
        Assert.Contains("\"raw\":\"AQID\"", json);
        Assert.Contains("\"role\":\"ROLE_USER\"", json);
        var back = A2AJson.Deserialize<Message>(json);
        Assert.Equal([PartKind.Text, PartKind.Raw, PartKind.Url, PartKind.Data], back.Parts.Select(p => p.Kind));
        Assert.Equal(1, back.Parts[3].Data!.Value.GetProperty("a").GetInt32());
    }

    [Fact]
    public void Default_bools_are_omitted_but_optional_capabilities_kept()
    {
        var json = A2AJson.Serialize(new AgentCapabilities { Streaming = false });
        Assert.Equal("{\"streaming\":false}", json);
        Assert.DoesNotContain("append", A2AJson.Serialize(new TaskArtifactUpdateEvent()));
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
        Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(AgentCardCanonicalizer.Normalized(JsonNode.Parse(raw)!.AsObject())));
    }
}

public class SigningTests
{
    internal static AgentCard SampleCard() => new()
    {
        Name = "Signer", Description = "d", Version = "1.0.0",
        SupportedInterfaces = [new AgentInterface { Url = "https://agent.example/a2a", ProtocolBinding = ProtocolBindings.JsonRpc }],
        Capabilities = new AgentCapabilities { Streaming = true, Extensions = [] },
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

    [Theory]
    [MemberData(nameof(Algorithms))]
    public async Task Sign_then_verify_succeeds_and_tampering_fails(string alg)
    {
        var key = NewKey(alg);
        var signed = AgentCardSigner.Sign(SampleCard(), key);
        var verifier = new AgentCardVerifier(new StaticKeyResolver([key.ToPublicJwk()]));

        var json = A2AJson.Serialize(signed);
        Assert.True((await verifier.VerifyAsync(json)).IsValid);

        var tampered = json.Replace("\"Signer\"", "\"Evil\"");
        var result = await verifier.VerifyAsync(tampered);
        Assert.Equal(AgentCardVerificationStatus.Invalid, result.Status);
    }

    [Fact]
    public async Task Unsigned_card_reports_unsigned()
    {
        var verifier = new AgentCardVerifier(new StaticKeyResolver());
        Assert.Equal(AgentCardVerificationStatus.Unsigned, (await verifier.VerifyAsync(SampleCard())).Status);
    }

    [Fact]
    public async Task Alg_none_and_unknown_kid_are_rejected()
    {
        var key = NewKey("ES256");
        var signed = AgentCardSigner.Sign(SampleCard(), key);
        var noneHeader = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", kid = "k1" }));
        signed.Signatures![0].Protected = noneHeader;
        signed.Signatures[0].Signature = "AA";
        var result = await new AgentCardVerifier(new StaticKeyResolver([key.ToPublicJwk()])).VerifyAsync(signed);
        Assert.Equal(AgentCardVerificationStatus.Invalid, result.Status);
        Assert.Contains(result.Errors, e => e.Contains("not allowed"));

        var other = AgentCardSigner.Sign(SampleCard(), key);
        var r2 = await new AgentCardVerifier(new StaticKeyResolver()).VerifyAsync(other);
        Assert.Contains(r2.Errors, e => e.Contains("no trusted key"));
    }

    [Fact]
    public async Task Fields_unknown_to_this_sdk_still_verify_when_raw_json_is_used()
    {
        var key = NewKey("ES256");
        var node = AgentCardCanonicalizer.ToNode(SampleCard());
        node["futureField"] = new JsonObject { ["x"] = 1 };
        var sig = SignRaw(node, key);
        node["signatures"] = new JsonArray(JsonSerializer.SerializeToNode(sig, A2AJson.Options));
        var raw = node.ToJsonString();

        var verifier = new AgentCardVerifier(new StaticKeyResolver([key.ToPublicJwk()]));
        Assert.True((await verifier.VerifyAsync(raw)).IsValid);
        // Round-tripping through the model drops the unknown field, which breaks the signature: always verify raw JSON.
        Assert.False((await verifier.VerifyAsync(A2AJson.Deserialize<AgentCard>(raw))).IsValid);
    }

    private static AgentCardSignature SignRaw(JsonObject card, AgentCardSigningKey key)
    {
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = key.Algorithm, ["kid"] = key.KeyId }));
        var input = System.Text.Encoding.ASCII.GetBytes(header + "." + Base64Url.Encode(AgentCardCanonicalizer.Normalized(card)));
        return new AgentCardSignature { Protected = header, Signature = Base64Url.Encode(JwsAlgorithms.Sign(key.Algorithm, key.Key, input)) };
    }

    [Fact]
    public async Task Jwks_resolver_refuses_cross_host_and_insecure_jku()
    {
        var resolver = new JwksKeyResolver(new HttpClient(new ThrowingHandler()));
        var ctx = new KeyResolutionContext("k1", "ES256", "https://evil.example/jwks.json", new Uri("https://agent.example/.well-known/agent-card.json"));
        Assert.Empty(await resolver.ResolveAsync(ctx));
        Assert.Empty(await resolver.ResolveAsync(ctx with { JwksUrl = "http://agent.example/jwks.json" }));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("must not fetch");
    }
}

public class SseReaderTests
{
    [Fact]
    public async Task Parses_multiline_data_comments_crlf_and_drops_incomplete_event()
    {
        var text = ": hello\r\ndata: a\r\ndata: b\r\n\r\nevent: x\nid: 7\ndata:c\n\ndata: incomplete";
        var events = new List<SseEvent>();
        await foreach (var e in SseReader.ReadAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)))) events.Add(e);
        Assert.Equal(2, events.Count);
        Assert.Equal("a\nb", events[0].Data);
        Assert.Equal(new SseEvent("c", "x", "7"), events[1]);
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
