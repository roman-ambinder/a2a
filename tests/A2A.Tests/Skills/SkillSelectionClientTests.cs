using A2A.Client;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Skills;

public class SkillSelectionClientTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private sealed class CardTransport(AgentCard extended) : IA2AClient
    {
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(extended);
        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingAgent : IAgentExecutor
    {
        public string? Requested { get; private set; }

        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            Requested = context.RequestedSkillId;
            return updater.CompleteAsync("done", ct).AsTask();
        }
    }

    private static Message Msg() => new() { MessageId = "m1", Role = Role.User, Parts = [Part.FromText("work")] };

    private static AgentSkill Skill(string id) => new() { Id = id, Name = id, Description = id, Tags = ["t"] };

    private static AgentCard Card(bool extended, params string[] skills)
    {
        var card = TestAgentHost.DefaultCard();
        card.Capabilities.ExtendedAgentCard = extended;
        card.Skills = skills.Select(Skill).ToList();
        return card;
    }

    private static A2AClient Client(AgentCard? card, bool validate = true, AgentCard? extended = null) =>
        new(new CardTransport(extended ?? Card(false)), new A2AClientOptions { ValidateCapabilities = validate }, card);

    [Fact]
    public void Sets_metadata_adds_uri_once_and_returns_same_options()
    {
        var client = Client(Card(false, "a"));
        var message = Msg();
        var options = new A2ARequestOptions();
        var first = client.SelectSkill(message, "a", options);
        var second = client.SelectSkill(message, "a", options);
        Assert.Same(options, first);
        Assert.Same(options, second);
        Assert.Equal("a", message.GetRequestedSkillId());
        Assert.Equal([SkillSelection.ExtensionUri], options.Extensions);
    }

    [Fact]
    public void Null_options_give_new_options()
    {
        var options = Client(Card(false, "a")).SelectSkill(Msg(), "a");
        Assert.Equal([SkillSelection.ExtensionUri], options.Extensions);
    }

    [Fact]
    public void Read_only_extensions_are_replaced_and_keep_entries()
    {
        var options = new A2ARequestOptions { Extensions = new[] { "urn:x" } };
        var result = Client(Card(false, "a")).SelectSkill(Msg(), "a", options);
        Assert.Equal(["urn:x", SkillSelection.ExtensionUri], result.Extensions);
        Assert.False(result.Extensions!.IsReadOnly);
    }

    [Fact]
    public void Unknown_skill_on_known_card_throws_and_leaves_message_unchanged()
    {
        var message = Msg();
        var ex = Assert.Throws<A2AException>(() => Client(Card(false, "a")).SelectSkill(message, "nope"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Null(message.Metadata);
    }

    [Fact]
    public void Validation_off_does_not_throw()
    {
        var message = Msg();
        Client(Card(false, "a"), validate: false).SelectSkill(message, "nope");
        Assert.Equal("nope", message.GetRequestedSkillId());
    }

    [Fact]
    public void No_card_does_not_throw() => Client(null).SelectSkill(Msg(), "nope");

    [Fact]
    public async Task Extended_card_not_loaded_skips_check_then_enforces_after_refresh()
    {
        var extended = Card(true, "a", "secret");
        var client = Client(Card(true, "a"), extended: extended);
        client.SelectSkill(Msg(), "secret");
        client.SelectSkill(Msg(), "nope");

        await client.RefreshWithExtendedAgentCardAsync();

        client.SelectSkill(Msg(), "secret");
        var ex = Assert.Throws<A2AException>(() => client.SelectSkill(Msg(), "nope"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [Fact]
    public void Bad_arguments_throw()
    {
        var client = Client(null);
        Assert.Throws<ArgumentNullException>(() => client.SelectSkill(null!, "a"));
        Assert.Throws<ArgumentNullException>(() => client.SelectSkill(Msg(), null!));
        Assert.Throws<ArgumentException>(() => client.SelectSkill(Msg(), ""));
    }

    private static async Task<(TestAgentHost Host, RecordingAgent Agent)> StartAsync()
    {
        var agent = new RecordingAgent();
        var host = await TestAgentHost.StartAsync(
            o => o.AgentCard.Capabilities.Extensions = [SkillSelection.CreateExtension()],
            services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));
        return (host, agent);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task End_to_end_unary_reaches_the_executor(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = host.Client(binding);
        var message = Msg();
        var options = client.SelectSkill(message, "scenario");
        await client.SendMessageAsync(message, options: options);
        await host.WaitIdleAsync();
        Assert.Equal("scenario", agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task End_to_end_streaming_reaches_the_executor(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = host.Client(binding);
        var message = Msg();
        var options = client.SelectSkill(message, "scenario");
        await foreach (var _e in client.SendStreamingMessageAsync(message, options: options)) { }
        await host.WaitIdleAsync();
        Assert.Equal("scenario", agent.Requested);
    }
}
