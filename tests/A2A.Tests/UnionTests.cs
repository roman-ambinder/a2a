using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Json;
using Xunit;

namespace A2A.Tests;

public class UnionTests
{
    private const string TaskJson = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}""";
    private const string MessageJson = """{"messageId":"m1","role":"ROLE_USER","parts":[{"text":"hi"}]}""";

    [Fact]
    public void Stream_response_with_two_members_rejected()
    {
        var ex = Assert.Throws<JsonException>(() =>
            A2AJson.Deserialize<StreamResponse>($$"""{"task":{{TaskJson}},"message":{{MessageJson}}}"""));
        Assert.Contains("found: task, message", ex.Message);
    }

    [Fact]
    public void Stream_response_with_no_member_rejected()
    {
        var ex = Assert.Throws<JsonException>(() => A2AJson.Deserialize<StreamResponse>("{}"));
        Assert.Contains("found none", ex.Message);
    }

    [Fact]
    public void Stream_response_round_trips_each_case()
    {
        var statusJson = """{"taskId":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}""";
        var artifactJson = """{"taskId":"t1","contextId":"c1","artifact":{"artifactId":"a1","parts":[{"text":"x"}]},"append":true}""";
        foreach (var (member, body, type) in new[]
        {
            ("task", TaskJson, typeof(TaskEvent)),
            ("message", MessageJson, typeof(MessageEvent)),
            ("statusUpdate", statusJson, typeof(StatusUpdateEvent)),
            ("artifactUpdate", artifactJson, typeof(ArtifactUpdateEvent)),
        })
        {
            var json = $$"""{"{{member}}":{{body}}}""";
            var evt = A2AJson.Deserialize<StreamResponse>(json);
            Assert.IsType(type, evt);
            Assert.Equal(json, A2AJson.Serialize(evt));
            if (member == "message") Assert.Null(evt.TaskId); else Assert.Equal("t1", evt.TaskId);
        }
    }

    [Fact]
    public void Send_message_response_round_trips_each_case()
    {
        var task = A2AJson.Deserialize<SendMessageResponse>($$"""{"task":{{TaskJson}}}""");
        Assert.Equal("t1", Assert.IsType<TaskResponse>(task).Task.Id);
        Assert.Equal($$"""{"task":{{TaskJson}}}""", A2AJson.Serialize(task));

        var msg = A2AJson.Deserialize<SendMessageResponse>($$"""{"message":{{MessageJson}}}""");
        Assert.Equal("m1", Assert.IsType<MessageResponse>(msg).Message.MessageId);
        Assert.Equal($$"""{"message":{{MessageJson}}}""", A2AJson.Serialize(msg));

        Assert.Throws<JsonException>(() => A2AJson.Deserialize<SendMessageResponse>($$"""{"task":{{TaskJson}},"message":{{MessageJson}}}"""));
        Assert.Throws<JsonException>(() => A2AJson.Deserialize<SendMessageResponse>("{}"));
    }

    [Fact]
    public void Derived_typed_union_serialization_matches_base_typed_wire_format()
    {
        var task = new AgentTask { Id = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = TaskState.Working } };
        var evt = new TaskEvent(task);
        Assert.Equal(A2AJson.Serialize<StreamResponse>(evt), A2AJson.Serialize(evt));
        Assert.Equal($$"""{"task":{{TaskJson}}}""", A2AJson.Serialize(evt));
        Assert.Equal("t1", A2AJson.Deserialize<TaskEvent>($$"""{"task":{{TaskJson}}}""").Task.Id);

        var resp = new MessageResponse(Message.FromUser("hi"));
        Assert.Equal(A2AJson.Serialize<SendMessageResponse>(resp), A2AJson.Serialize(resp));
        Assert.Throws<JsonException>(() => A2AJson.Deserialize<MessageResponse>($$"""{"task":{{TaskJson}}}"""));

        SecurityScheme scheme = SecurityScheme.Bearer();
        var derived = Assert.IsType<HttpAuthScheme>(scheme);
        Assert.Equal(A2AJson.Serialize(scheme), A2AJson.Serialize(derived));
    }

    [Fact]
    public void Security_scheme_round_trips_and_rejects_two()
    {
        const string json = """
            {"apiKeySecurityScheme":{"location":"header","name":"X-Key"}}
            """;
        var scheme = A2AJson.Deserialize<SecurityScheme>(json.Trim());
        Assert.Equal("X-Key", Assert.IsType<ApiKeyScheme>(scheme).Value.Name);
        Assert.Equal(json.Trim(), A2AJson.Serialize(scheme));

        var cases = new (string Json, Type Type)[]
        {
            ("""{"httpAuthSecurityScheme":{"scheme":"Bearer","bearerFormat":"JWT"}}""", typeof(HttpAuthScheme)),
            ("""{"oauth2SecurityScheme":{"flows":{"clientCredentials":{"tokenUrl":"https://x/t","scopes":{}}}}}""", typeof(OAuth2Scheme)),
            ("""{"openIdConnectSecurityScheme":{"openIdConnectUrl":"https://x/.well-known"}}""", typeof(OpenIdConnectScheme)),
            ("""{"mtlsSecurityScheme":{"description":"d"}}""", typeof(MutualTlsScheme)),
        };
        foreach (var (j, t) in cases)
        {
            var s = A2AJson.Deserialize<SecurityScheme>(j);
            Assert.IsType(t, s);
            Assert.Equal(j, A2AJson.Serialize(s));
        }

        var ex = Assert.Throws<JsonException>(() => A2AJson.Deserialize<SecurityScheme>(
            """{"apiKeySecurityScheme":{"name":"k"},"httpAuthSecurityScheme":{"scheme":"Bearer"}}"""));
        Assert.Contains("found: apiKeySecurityScheme, httpAuthSecurityScheme", ex.Message);
    }

    [Fact]
    public void Outgoing_validation_rejects_empty_message_id_and_parts()
    {
        var noParts = new SendMessageRequest { Message = new Message { MessageId = "m", Role = Role.User, Parts = [] } };
        Assert.Equal(A2AErrorCode.InvalidParams, Assert.Throws<A2AException>(() => Validation.A2AValidator.ValidateOutgoing(noParts)).Code);
        var noId = new SendMessageRequest { Message = new Message { MessageId = "", Role = Role.User, Parts = [Part.FromText("x")] } };
        Assert.Equal(A2AErrorCode.InvalidParams, Assert.Throws<A2AException>(() => Validation.A2AValidator.ValidateOutgoing(noId)).Code);
        Validation.A2AValidator.ValidateOutgoing(new SendMessageRequest { Message = Message.FromUser("ok") });
    }

    [Fact]
    public async Task Client_rejects_message_without_parts_before_sending()
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        var client = new A2AClient(new JsonRpcTransport(http, new Uri("http://localhost/a2a")));
        var bad = new Message { MessageId = "m1", Role = Role.User, Parts = [] };

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(bad));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(bad)) { }
        });
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public void Agent_card_with_empty_proto_fields_omitted_deserializes_with_defaults()
    {
        var card = A2AJson.Deserialize<AgentCard>("""
            {"name":"n","version":"1","supportedInterfaces":[{"url":"https://x","protocolBinding":"JSONRPC","protocolVersion":"1.0"}],"capabilities":{}}
            """);
        Assert.Equal("", card.Description);
        Assert.Empty(card.Skills);
        Assert.Empty(card.DefaultInputModes);
        Assert.Empty(card.DefaultOutputModes);

        var bare = A2AJson.Deserialize<AgentCard>("{}");
        Assert.Equal("", bare.Name);
        Assert.NotNull(bare.Capabilities);
        Assert.Empty(bare.SupportedInterfaces);
    }

    [Fact]
    public void Artifact_update_without_parts_key_deserializes()
    {
        var e = A2AJson.Deserialize<TaskArtifactUpdateEvent>(
            """{"taskId":"t","contextId":"c","artifact":{"artifactId":"a"},"append":true,"lastChunk":true}""");
        Assert.True(e.Append);
        Assert.True(e.LastChunk);
        Assert.Empty(e.Artifact.Parts);
    }

    [Theory]
    [InlineData("JSONRPC", """{"messageId":"m1","role":"ROLE_USER"}""")]
    [InlineData("JSONRPC", """{"role":"ROLE_USER","parts":[{"text":"x"}]}""")]
    [InlineData("JSONRPC", """{"messageId":"m1","role":"ROLE_USER","parts":[]}""")]
    [InlineData("HTTP+JSON", """{"messageId":"m1","role":"ROLE_USER"}""")]
    [InlineData("HTTP+JSON", """{"role":"ROLE_USER","parts":[{"text":"x"}]}""")]
    [InlineData("HTTP+JSON", """{"messageId":"m1","role":"ROLE_USER","parts":[]}""")]
    public async Task Raw_send_message_with_missing_required_member_is_invalid_params(string binding, string message)
    {
        await using var host = await TestAgentHost.StartAsync();
        var http = host.Http("alice");
        http.DefaultRequestHeaders.Add(A2AProtocol.Headers.Version, "1.0");
        HttpResponseMessage resp;
        if (binding == "JSONRPC")
        {
            var body = """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":""" + message + "}}";
            resp = await http.PostAsync(TestAgentHost.BaseUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Assert.Equal(-32602, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        }
        else
        {
            var body = """{"message":""" + message + "}";
            resp = await http.PostAsync(TestAgentHost.BaseUrl + "/message:send", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.Contains("INVALID_ARGUMENT", await resp.Content.ReadAsStringAsync());
        }
    }
}
