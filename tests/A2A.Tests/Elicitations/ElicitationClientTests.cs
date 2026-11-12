using A2A.Client;
using A2A.Elicitations;

namespace A2A.Tests.Elicitations;

public class ElicitationClientTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private sealed class Recording : IA2AClient
    {
    public AgentCard? AgentCard => null;

        public List<(SendMessageRequest Request, A2ARequestOptions? Options)> Sent { get; } = [];

        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((request, options));
            return Task.FromResult<SendMessageResponse>(new MessageResponse(Message.FromAgent("ok")));
        }
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static ElicitationForm Form(string id = "f1") => new([ElicitationField.Text("city", required: true)], id);

    private static AgentTask Waiting(ElicitationForm? form, TaskState state = TaskState.InputRequired)
    {
        var message = Message.FromAgent("which city?", "ctx-1", "task-1");
        if (form is not null) message.SetElicitationForm(form);
        return new AgentTask { Id = "task-1", ContextId = "ctx-1", Status = new AgentTaskStatus { State = state, Message = message } };
    }

    [Fact]
    public void UseElicitation_adds_the_extension_once_and_returns_the_same_options()
    {
        var options = new A2ARequestOptions();
        Assert.Same(options, options.UseElicitation());
        options.UseElicitation();
        Assert.Equal([Elicitation.ExtensionUri], options.Extensions);
    }

    [Fact]
    public void UseElicitation_keeps_other_extensions_and_replaces_a_read_only_list()
    {
        var options = new A2ARequestOptions { Extensions = new[] { "urn:other" } };
        options.UseElicitation();
        Assert.Equal(["urn:other", Elicitation.ExtensionUri], options.Extensions);

        var already = new A2ARequestOptions { Extensions = new[] { Elicitation.ExtensionUri } };
        already.UseElicitation();
        Assert.Equal([Elicitation.ExtensionUri], already.Extensions);
        Assert.Throws<ArgumentNullException>(() => ((A2ARequestOptions)null!).UseElicitation());
    }

    [Fact]
    public async Task Answer_sends_a_message_for_the_task_with_the_extension_on()
    {
        var client = new Recording();
        var form = Form();
        var task = Waiting(form);
        var options = new A2ARequestOptions();
        options.Headers["X-Mine"] = "1";
        await client.AnswerElicitationAsync(task, ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo" } }), options);

        var (request, sentOptions) = Assert.Single(client.Sent);
        Assert.Equal("task-1", request.Message!.TaskId);
        Assert.Equal("ctx-1", request.Message.ContextId);
        Assert.Equal(Role.User, request.Message.Role);
        Assert.Equal("Oslo", request.Message.GetElicitationAnswer()!.GetString("city"));
        Assert.DoesNotContain("Oslo", request.Message.GetText());
        Assert.Contains(Elicitation.ExtensionUri, sentOptions!.Extensions!);
        Assert.Equal("1", sentOptions.Headers["X-Mine"]);
    }

    [Fact]
    public async Task Answer_without_a_readable_pending_form_is_InvalidParams_and_sends_nothing()
    {
        var client = new Recording();
        var answer = ElicitationAnswer.Decline(Form());
        foreach (var task in new[] { Waiting(null), Waiting(Form(), TaskState.Working), Waiting(Form(), TaskState.Completed) })
        {
            var ex = await Assert.ThrowsAsync<A2AException>(() => client.AnswerElicitationAsync(task, answer));
            Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        }
        // A form that cannot be read (a keyword outside the spec).
        var unreadable = Waiting(null);
        unreadable.Status.Message!.Metadata = new() { [Elicitation.MetadataKey] = System.Text.Json.JsonDocument.Parse("{\"id\":\"f1\",\"requestedSchema\":{\"type\":\"object\",\"properties\":{\"a\":{\"type\":\"string\",\"pattern\":\"x\"}}}}").RootElement.Clone() };
        var ex2 = await Assert.ThrowsAsync<A2AException>(() => client.AnswerElicitationAsync(unreadable, answer));
        Assert.Equal(A2AErrorCode.InvalidParams, ex2.Code);
        Assert.Empty(client.Sent);
    }

    [Fact]
    public async Task Answer_for_another_form_is_InvalidParams_and_sends_nothing()
    {
        var client = new Recording();
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.AnswerElicitationAsync(Waiting(Form("f1")), ElicitationAnswer.Decline(Form("f2"))));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Empty(client.Sent);
    }

    // ---- D14: OnMessageReceived sees the form ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task Hook_sees_the_form_on_the_snapshot_and_the_answer_goes_through_the_same_client(string binding)
    {
        var (host, _) = await ElicitationServerTests.StartAsync();
        await using var _ = host;
        var seen = new List<(ReceivedMessageSource Source, ElicitationForm? Form, string Text)>();
        var client = ElicitationServerTests.ClientFor(host, binding, new A2AClientOptions
        {
            ValidateCapabilities = false,
            OnMessageReceived = (m, _) => { seen.Add((m.Source, m.Message.GetElicitationForm(), m.Message.GetText())); return ValueTask.CompletedTask; },
        });
        var task = (await client.SendMessageAsync(Message.FromUser("book"), options: ElicitationServerTests.Activated())).RequireTask();
        var first = Assert.Single(seen);
        Assert.Equal(ReceivedMessageSource.TaskSnapshot, first.Source);
        Assert.Equal("f1", first.Form!.Id);

        await client.AnswerElicitationAsync(task, ElicitationAnswer.Accept(task.GetElicitationForm()!, new ElicitationValues { { "city", "Oslo" }, { "nights", 1 } }));
        Assert.Equal(2, seen.Count);
        Assert.Equal("booked Oslo 1", seen[1].Text); // the agent's next message passes the hook too
        Assert.Null(seen[1].Form);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Hook_sees_the_form_on_a_status_update_when_streaming(string binding)
    {
        var (host, _) = await ElicitationServerTests.StartAsync();
        await using var _ = host;
        var forms = new List<(ReceivedMessageSource Source, ElicitationForm? Form)>();
        var client = ElicitationServerTests.ClientFor(host, binding, new A2AClientOptions
        {
            ValidateCapabilities = false,
            OnMessageReceived = (m, _) => { forms.Add((m.Source, m.Message.GetElicitationForm())); return ValueTask.CompletedTask; },
        });
        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("book"), options: ElicitationServerTests.Activated()))
            if (evt is StatusUpdateEvent { StatusUpdate.Status.State: TaskState.InputRequired }) break; // the stream stays open while the task waits
        Assert.Contains(forms, f => f.Form is { Id: "f1" } && f.Source is ReceivedMessageSource.StatusUpdate or ReceivedMessageSource.TaskSnapshot);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_throwing_hook_stops_the_form_before_the_application_gets_it(string binding)
    {
        var (host, agent) = await ElicitationServerTests.StartAsync();
        await using var _ = host;
        var client = ElicitationServerTests.ClientFor(host, binding, new A2AClientOptions
        {
            ValidateCapabilities = false,
            OnMessageReceived = (m, _) => m.Message.GetElicitationForm() is { } form && form.Fields.Any(f => f.Name == "nights")
                ? throw new InvalidOperationException("form refused by policy")
                : ValueTask.CompletedTask,
        });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SendMessageAsync(Message.FromUser("book"), options: ElicitationServerTests.Activated()));
        Assert.Equal("form refused by policy", ex.Message);
        Assert.Equal(1, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_throwing_hook_stops_the_streaming_form_too(string binding)
    {
        var (host, _) = await ElicitationServerTests.StartAsync();
        await using var _ = host;
        var client = ElicitationServerTests.ClientFor(host, binding, new A2AClientOptions
        {
            ValidateCapabilities = false,
            OnMessageReceived = (m, _) => m.Message.GetElicitationForm() is not null ? throw new InvalidOperationException("form refused by policy") : ValueTask.CompletedTask,
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("book"), options: ElicitationServerTests.Activated())) { }
        });
    }
}
