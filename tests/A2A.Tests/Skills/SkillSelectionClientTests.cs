using A2A.Client;
using A2A.Server;
using A2A.Skills;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Skills;

public class SkillSelectionClientTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    /// <summary>Answers the extended card and records what each send carried.</summary>
    private sealed class CardTransport(AgentCard extended) : IA2ATransport
    {
        public List<(Message Message, A2ARequestOptions? Options)> Sent { get; } = [];
        public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(extended);
        public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Sent.Add((request.Message!, options));
            return Task.FromResult<SendMessageResponse>(new MessageResponse(Message.FromAgent("ok")));
        }
        public async IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Sent.Add((request.Message!, options));
            await Task.Yield();
            yield return StreamResponse.Of(Message.FromAgent("ok"));
        }
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

    private static (A2AClient Client, CardTransport Transport) Client(AgentCard? card, bool validate = true, AgentCard? extended = null)
    {
        var transport = new CardTransport(extended ?? Card(false));
        return (new A2AClient(transport, new A2AClientOptions { ValidateCapabilities = validate }, card), transport);
    }

    private static A2ARequestOptions For(string skill) => new() { Skill = skill };

    [Fact]
    public async Task Skill_sets_metadata_and_requests_the_extension_without_changing_the_callers_options()
    {
        var (client, transport) = Client(Card(false, "a"));
        var options = For("a");
        await client.SendMessageAsync(Msg(), options: options);
        var (message, sent) = Assert.Single(transport.Sent);
        Assert.Equal("a", message.GetRequestedSkillId());
        Assert.Equal([SkillSelection.ExtensionUri], sent!.Extensions);
        Assert.Null(options.Extensions); // the caller's options are reusable as they are
    }

    [Fact]
    public async Task Existing_extensions_are_kept_once()
    {
        var (client, transport) = Client(Card(false, "a"));
        var options = new A2ARequestOptions { Skill = "a", Extensions = new[] { "urn:x", SkillSelection.ExtensionUri } };
        await client.SendMessageAsync(Msg(), options: options);
        Assert.Equal(["urn:x", SkillSelection.ExtensionUri], Assert.Single(transport.Sent).Options!.Extensions);
    }

    [Fact]
    public async Task Streaming_sends_apply_the_skill_too()
    {
        var (client, transport) = Client(Card(false, "a"));
        await foreach (var _ in client.SendStreamingMessageAsync(Msg(), options: For("a"))) { }
        var (message, sent) = Assert.Single(transport.Sent);
        Assert.Equal("a", message.GetRequestedSkillId());
        Assert.Contains(SkillSelection.ExtensionUri, sent!.Extensions!);
    }

    [Fact]
    public async Task Unknown_skill_on_known_card_throws_and_sends_nothing()
    {
        var (client, transport) = Client(Card(false, "a"));
        var message = Msg();
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(message, options: For("nope")));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Empty(transport.Sent);
        Assert.Null(message.Metadata);
    }

    [Fact]
    public async Task Validation_off_or_no_card_sends_any_skill()
    {
        var (unchecked_, sentA) = Client(Card(false, "a"), validate: false);
        await unchecked_.SendMessageAsync(Msg(), options: For("nope"));
        Assert.Equal("nope", Assert.Single(sentA.Sent).Message.GetRequestedSkillId());

        var (noCard, sentB) = Client(null);
        await noCard.SendMessageAsync(Msg(), options: For("nope"));
        Assert.Single(sentB.Sent);
    }

    [Fact]
    public async Task Extended_card_not_loaded_skips_check_then_enforces_after_refresh()
    {
        var extended = Card(true, "a", "secret");
        var (client, _) = Client(Card(true, "a"), extended: extended);
        await client.SendMessageAsync(Msg(), options: For("secret"));
        await client.SendMessageAsync(Msg(), options: For("nope"));

        await client.RefreshWithExtendedAgentCardAsync();

        await client.SendMessageAsync(Msg(), options: For("secret"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Msg(), options: For("nope")));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task An_empty_skill_selects_nothing()
    {
        var (client, transport) = Client(Card(false, "a"));
        await client.SendMessageAsync(Msg(), options: For(""));
        var (message, sent) = Assert.Single(transport.Sent);
        Assert.Null(message.GetRequestedSkillId());
        Assert.Null(sent!.Extensions);
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
        await client.SendMessageAsync(Msg(), options: For("scenario"));
        await host.WaitIdleAsync();
        Assert.Equal("scenario", agent.Requested);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task End_to_end_streaming_reaches_the_executor(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = host.Client(binding);
        await foreach (var _e in client.SendStreamingMessageAsync(Msg(), options: For("scenario"))) { }
        await host.WaitIdleAsync();
        Assert.Equal("scenario", agent.Requested);
    }
}
