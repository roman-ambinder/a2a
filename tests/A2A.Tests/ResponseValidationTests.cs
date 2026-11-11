using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Json;
using A2A.Validation;

namespace A2A.Tests;

/// <summary>
///     Spec 5.7: the client rejects an agent response that lacks a REQUIRED field (InvalidAgentResponse), unless
///     <see cref="A2AClientOptions.ValidateResponses" /> is off.
/// </summary>
public class ResponseValidationTests
{
    private const string Task = """{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}""";
    private const string Msg = """{"messageId":"m1","role":"ROLE_AGENT","parts":[{"text":"hi"}]}""";

    private static StreamResponse Event(string json) => A2AJson.Deserialize<StreamResponse>(json)!;

    public static TheoryData<string, string> InvalidEvents() => new()
    {
        { """{"task":{"id":"","contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}}""", "task.id" },
        { """{"task":{"id":"t1","contextId":"c1","status":{}}}""", "task.status.state" },
        { """{"task":{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"},"history":[{"messageId":"","role":"ROLE_USER","parts":[{"text":"a"}]}]}}""", "task.history[0].messageId" },
        { """{"task":{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING"},"artifacts":[{"artifactId":"","parts":[]}]}}""", "task.artifacts[0].artifactId" },
        { """{"message":{"messageId":"m1","role":"ROLE_UNSPECIFIED","parts":[{"text":"hi"}]}}""", "message.role" }, // a missing role fails to parse already
        { """{"message":{"messageId":"m1","role":"ROLE_AGENT","parts":[]}}""", "message.parts" },
        { """{"statusUpdate":{"contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}}""", "statusUpdate.taskId" },
        { """{"statusUpdate":{"taskId":"t1","status":{"state":"TASK_STATE_WORKING"}}}""", "statusUpdate.contextId" },
        { """{"statusUpdate":{"taskId":"t1","contextId":"c1","status":{"state":"TASK_STATE_WORKING","message":{"messageId":"m2","role":"ROLE_AGENT","parts":[]}}}}""", "statusUpdate.status.message.parts" },
        { """{"artifactUpdate":{"taskId":"t1","artifact":{"artifactId":"a1","parts":[{"text":"x"}]}}}""", "artifactUpdate.contextId" },
        { """{"artifactUpdate":{"taskId":"t1","contextId":"c1","artifact":{"artifactId":"a1","parts":[]}}}""", "artifactUpdate.artifact.parts" },
    };

    [Theory, MemberData(nameof(InvalidEvents))]
    public void An_event_without_a_required_field_is_an_invalid_agent_response(string json, string field)
    {
        var ex = Assert.Throws<A2AException>(() => A2AValidator.ValidateResponse(Event(json)));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains(field, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"task\":" + Task + "}")]
    [InlineData("{\"message\":" + Msg + "}")]
    [InlineData("""{"statusUpdate":{"taskId":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED"}}}""")]
    [InlineData("""{"artifactUpdate":{"taskId":"t1","contextId":"c1","artifact":{"artifactId":"a1","parts":[]},"append":true,"lastChunk":true}}""")] // an append chunk may only seal
    [InlineData("""{"task":{"id":"t1","contextId":"c1","status":{"state":"TASK_STATE_COMPLETED"},"artifacts":[{"artifactId":"a1","parts":[]}]}}""")] // a stored artifact may be empty
    [InlineData("""{"error":{"code":"UNAVAILABLE","message":"later"}}""")] // not checked
    public void Valid_events_pass(string json)
    {
        A2AValidator.ValidateResponse(Event(json));
    }

    // ---- the client ----

    private static readonly Uri Endpoint = new("http://localhost/a2a");

    /// <summary>Answers every JSON-RPC call with <paramref name="result" />, as JSON or as one SSE event.</summary>
    private sealed class Agent(string result) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var reply = $$"""{"jsonrpc":"2.0","id":{{body["id"]!.ToJsonString()}},"result":{{result}}}""";
            var sse = request.Headers.Accept.Any(a => a.MediaType == "text/event-stream");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = sse
                    ? new StringContent("data: " + reply + "\n\n", Encoding.UTF8, "text/event-stream")
                    : new StringContent(reply, Encoding.UTF8, "application/json")
            };
        }
    }

    private static A2AClient Client(string result, bool validate = true) =>
        new(new JsonRpcTransport(new HttpClient(new Agent(result)), Endpoint),
            new A2AClientOptions { MaxRetries = 0, ValidateResponses = validate });

    private const string NoTaskId = """{"statusUpdate":{"contextId":"c1","status":{"state":"TASK_STATE_WORKING"}}}""";
    private const string TaskWithoutState = """{"task":{"id":"t1","contextId":"c1","status":{}}}""";

    [Fact]
    public async Task SendMessage_rejects_a_task_without_a_state()
    {
        var ex = await Assert.ThrowsAsync<A2AException>(() => Client(TaskWithoutState).SendMessageAsync("hi"));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("task.status.state", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTask_and_CancelTask_reject_a_task_without_a_state()
    {
        const string bare = """{"id":"t1","contextId":"c1","status":{}}""";

        var get = await Assert.ThrowsAsync<A2AException>(() => Client(bare).GetTaskAsync("t1"));
        var cancel = await Assert.ThrowsAsync<A2AException>(() => Client(bare).CancelTaskAsync("t1"));

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, get.Code);
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, cancel.Code);
    }

    [Fact]
    public async Task A_stream_event_without_a_task_id_ends_the_stream_with_an_invalid_agent_response()
    {
        var client = Client(NoTaskId);

        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SubscribeToTaskAsync("t1")) { }
        });

        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
        Assert.Contains("statusUpdate.taskId", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_ValidateResponses_off_the_response_is_returned_as_it_is()
    {
        var task = (await Client(TaskWithoutState, validate: false).SendMessageAsync("hi")).RequireTask();
        var events = new List<StreamResponse>();
        await foreach (var e in Client(NoTaskId, validate: false).SubscribeToTaskAsync("t1")) events.Add(e);

        Assert.Equal(TaskState.Unspecified, task.Status.State);
        Assert.Equal("", Assert.IsType<StatusUpdateEvent>(Assert.Single(events)).StatusUpdate.TaskId);
    }

    // ---- the card ----

    [Fact]
    public void A_card_lists_its_missing_required_fields()
    {
        var card = SigningTests.SampleCard();
        card.Version = "";
        card.DefaultOutputModes = [];
        card.Skills = [new AgentSkill { Id = "s", Name = "S", Description = "" }];

        Assert.Equal(["version", "defaultOutputModes", "skills[0].description", "skills[0].tags"], A2ACardResolver.MissingRequired(card));
        Assert.Empty(A2ACardResolver.MissingRequired(TestAgentHost.DefaultCard()));
    }

    [Fact]
    public async Task A_fetched_card_without_required_fields_is_used_and_logged_once()
    {
        var card = SigningTests.SampleCard();
        card.Skills = [];
        var log = new CardTrustTests.ListLogger();
        var resolver = new A2ACardResolver(new HttpClient(new CardTrustTests.CardServer(A2AJson.Serialize(card))),
            new A2ACardResolverOptions { DefaultCacheDuration = TimeSpan.Zero }, log);

        var first = await resolver.ResolveAsync(new Uri("https://incomplete.agent.example/"));
        await resolver.ResolveAsync(new Uri("https://incomplete.agent.example/"));

        Assert.Empty(first.Card.Skills);
        var warning = Assert.Single(log.Entries, e => e.Message.Contains("REQUIRED fields", StringComparison.Ordinal));
        Assert.Contains("skills", warning.Message, StringComparison.Ordinal);
    }
}
