using System.Text.Json;
using A2A.Client;
using A2A.Skills;

namespace A2A.Tests.TypedSkills;

public class TypedSkillClientTests
{
    /// <summary>Answers every send with a fixed response, as another SDK's agent might.</summary>
    private sealed class FakeTransport(Func<SendMessageRequest, SendMessageResponse> send, Func<string, AgentTask>? get = null) : IA2AClient
    {
        public SendMessageRequest? Sent { get; private set; }
        public A2ARequestOptions? SentOptions { get; private set; }

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent = request;
            SentOptions = options;
            return Task.FromResult(send(request));
        }

        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult((get ?? throw new NotSupportedException())(request.Id));

        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static readonly BookingRequest Input = new() { City = "Oslo", Nights = 2 };

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message Reply(params Part[] parts) => new() { MessageId = "r1", Role = Role.Agent, Parts = [.. parts] };

    private static AgentTask NewTask(TaskState state, params Artifact[] artifacts) => new()
    {
        Id = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = state }, Artifacts = [.. artifacts],
    };

    private static Artifact Data(string id, string json) => new() { ArtifactId = id, Parts = [Part.FromData(Json(json))] };

    private static Task<BookingResult> Call(A2AClient client) =>
        client.CallSkillAsync("book", Input, TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult);

    private static A2AClient ClientOf(FakeTransport transport) => new(transport, new A2AClientOptions { ValidateCapabilities = false });

    private const string ResultJson = """{"city":"Oslo","nights":2,"reference":"x"}""";

    [Fact]
    public async Task Sends_one_data_part_with_the_skill_id_and_the_extension()
    {
        var transport = new FakeTransport(_ => new TaskResponse(NewTask(TaskState.Completed, Data("a", ResultJson))));
        await Call(ClientOf(transport));
        var message = transport.Sent!.Message!;
        Assert.Equal("book", message.GetRequestedSkillId());
        var part = Assert.IsType<DataPart>(Assert.Single(message.Parts));
        Assert.Equal("Oslo", part.Data.GetProperty("city").GetString());
        Assert.Contains(SkillSelection.ExtensionUri, transport.SentOptions!.Extensions!);
    }

    [Fact]
    public async Task Reads_a_direct_message_reply_with_a_data_part()
    {
        var transport = new FakeTransport(_ => new MessageResponse(Reply(Part.FromText("here you go"), Part.FromData(Json(ResultJson)))));
        var result = await Call(ClientOf(transport));
        Assert.Equal("x", result.Reference);
    }

    [Fact]
    public async Task A_message_reply_without_a_data_part_is_InvalidAgentResponse()
    {
        var transport = new FakeTransport(_ => new MessageResponse(Reply(Part.FromText("no data"))));
        var ex = await Assert.ThrowsAsync<A2AException>(() => Call(ClientOf(transport)));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task An_unparseable_result_is_InvalidAgentResponse_and_does_not_leak_a_json_exception()
    {
        var transport = new FakeTransport(_ => new TaskResponse(NewTask(TaskState.Completed, Data("a", """{"city":5}"""))));
        var ex = await Assert.ThrowsAsync<A2AException>(() => Call(ClientOf(transport)));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task A_null_result_is_InvalidAgentResponse()
    {
        var transport = new FakeTransport(_ => new TaskResponse(NewTask(TaskState.Completed, Data("a", "null"))));
        var ex = await Assert.ThrowsAsync<A2AException>(() => Call(ClientOf(transport)));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task A_completed_task_without_a_data_artifact_is_InvalidAgentResponse()
    {
        var text = new Artifact { ArtifactId = "a", Parts = [Part.FromText("text")] };
        var transport = new FakeTransport(_ => new TaskResponse(NewTask(TaskState.Completed, text)));
        var ex = await Assert.ThrowsAsync<A2AException>(() => Call(ClientOf(transport)));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Fact]
    public async Task Reads_the_last_artifact_that_has_a_data_part()
    {
        var text = new Artifact { ArtifactId = "t", Parts = [Part.FromText("text")] };
        var transport = new FakeTransport(_ => new TaskResponse(NewTask(TaskState.Completed,
            Data("a", """{"city":"Old","nights":1,"reference":"old"}"""), Data("b", ResultJson), text)));
        Assert.Equal("x", (await Call(ClientOf(transport))).Reference);
    }

    [Theory]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Canceled)]
    [InlineData(TaskState.Rejected)]
    [InlineData(TaskState.InputRequired)]
    public async Task A_task_that_does_not_complete_throws_TypedSkillCallException(TaskState state)
    {
        var transport = new FakeTransport(_ => new TaskResponse(NewTask(state, Data("a", ResultJson))));
        var ex = await Assert.ThrowsAsync<TypedSkillCallException>(() => Call(ClientOf(transport)));
        Assert.Equal(state, ex.State);
        Assert.Equal("t1", ex.Task.Id);
    }

    [Fact]
    public async Task Waits_for_a_task_that_is_not_terminal_yet()
    {
        var polls = 0;
        var transport = new FakeTransport(
            _ => new TaskResponse(NewTask(TaskState.Working)),
            _ => ++polls < 2 ? NewTask(TaskState.Working) : NewTask(TaskState.Completed, Data("a", ResultJson)));
        Assert.Equal("x", (await Call(ClientOf(transport))).Reference);
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task Checks_the_card_before_sending()
    {
        var transport = new FakeTransport(_ => throw new InvalidOperationException("must not send"));
        var card = TestAgentHost.DefaultCard(); // lists only "scenario"
        card.Capabilities.ExtendedAgentCard = false; // otherwise the list may be incomplete and the check is skipped
        var client = new A2AClient(transport, new A2AClientOptions { ValidateCapabilities = true }, card);
        var ex = await Assert.ThrowsAsync<A2AException>(() => Call(client));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Null(transport.Sent);
    }

    [Fact]
    public async Task Rejects_empty_skill_id_and_null_input()
    {
        var client = ClientOf(new FakeTransport(_ => throw new NotSupportedException()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.CallSkillAsync("", Input, TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CallSkillAsync<BookingRequest, BookingResult>("book", null!, TypedJson.Default.BookingRequest, TypedJson.Default.BookingResult));
    }
}
