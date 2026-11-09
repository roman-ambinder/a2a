using A2A.Client.Outbox;

namespace A2A.Tests;

/// <summary>The dispatcher checks its options when it is created.</summary>
public sealed class ClientOutboxOptionsTests
{
    private static ClientOutboxDispatcher Create(ClientOutboxOptions options) =>
        new(new InMemoryClientOutboxStore(), (_, _) => new(new ScriptedA2AClient()), options);

    private static ClientOutboxOptions Off() => new() { SweepInterval = Timeout.InfiniteTimeSpan };

    [Fact]
    public async Task Defaults_are_valid_and_match_the_design()
    {
        var o = new ClientOutboxOptions();
        Assert.Equal(TimeSpan.FromSeconds(1), o.SweepInterval);
        Assert.Equal(50, o.BatchSize);
        Assert.Equal(8, o.MaxConcurrency);
        Assert.Equal(TimeSpan.FromMinutes(5), o.Lease);
        Assert.Equal(TimeSpan.FromSeconds(60), o.CallTimeout);
        Assert.Equal(TimeSpan.FromSeconds(1), o.RetryBaseDelay);
        Assert.Equal(TimeSpan.FromMinutes(5), o.RetryMaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), o.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), o.MaxPollInterval);
        Assert.Equal(TimeSpan.FromHours(24), o.MaxQueueAge);
        Assert.Equal(TimeSpan.FromDays(7), o.MaxTrackingAge);
        Assert.True(o.RetryInDoubt);
        Assert.Equal(TimeSpan.FromDays(7), o.CollectedRetention);
        Assert.Equal(TimeSpan.FromHours(1), o.PurgeInterval);
        await using var dispatcher = Create(o);
    }

    [Fact]
    public void Default_queue_age_fits_the_servers_default_idempotency_retention() =>
        Assert.True(new ClientOutboxOptions().MaxQueueAge <= new A2A.Server.Idempotency.IdempotencyOptions().Retention);

    public static TheoryData<string, Action<ClientOutboxOptions>> Invalid => new()
    {
        { "SweepInterval zero", o => o.SweepInterval = TimeSpan.Zero },
        { "SweepInterval negative", o => o.SweepInterval = TimeSpan.FromSeconds(-1) },
        { "BatchSize zero", o => o.BatchSize = 0 },
        { "MaxConcurrency zero", o => o.MaxConcurrency = 0 },
        { "CallTimeout zero", o => o.CallTimeout = TimeSpan.Zero },
        { "CallTimeout infinite", o => o.CallTimeout = Timeout.InfiniteTimeSpan },
        { "Lease below twice CallTimeout", o => { o.CallTimeout = TimeSpan.FromSeconds(60); o.Lease = TimeSpan.FromSeconds(119); } },
        { "Lease infinite", o => o.Lease = Timeout.InfiniteTimeSpan },
        { "RetryBaseDelay zero", o => o.RetryBaseDelay = TimeSpan.Zero },
        { "RetryMaxDelay below base", o => { o.RetryBaseDelay = TimeSpan.FromSeconds(10); o.RetryMaxDelay = TimeSpan.FromSeconds(9); } },
        { "PollInterval zero", o => o.PollInterval = TimeSpan.Zero },
        { "MaxPollInterval below PollInterval", o => { o.PollInterval = TimeSpan.FromSeconds(10); o.MaxPollInterval = TimeSpan.FromSeconds(9); } },
        { "MaxQueueAge zero", o => o.MaxQueueAge = TimeSpan.Zero },
        { "MaxTrackingAge negative", o => o.MaxTrackingAge = TimeSpan.FromDays(-1) },
        { "CollectedRetention negative", o => o.CollectedRetention = TimeSpan.FromSeconds(-1) },
        { "CollectedRetention infinite", o => o.CollectedRetention = Timeout.InfiniteTimeSpan },
        { "PurgeInterval zero", o => o.PurgeInterval = TimeSpan.Zero },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_option_is_refused_by_the_constructor(string name, Action<ClientOutboxOptions> change)
    {
        var o = Off();
        change(o);
        Assert.ThrowsAny<ArgumentException>(() => Create(o));
        Assert.False(string.IsNullOrEmpty(name));
    }

    [Fact]
    public async Task Infinite_limits_are_allowed()
    {
        var o = Off();
        o.MaxQueueAge = Timeout.InfiniteTimeSpan;
        o.MaxTrackingAge = Timeout.InfiniteTimeSpan;
        o.PurgeInterval = Timeout.InfiniteTimeSpan;
        o.CollectedRetention = TimeSpan.Zero;
        await using var dispatcher = Create(o);
    }

    [Fact]
    public async Task Lease_of_exactly_twice_the_call_timeout_is_allowed()
    {
        var o = Off();
        o.CallTimeout = TimeSpan.FromSeconds(30);
        o.Lease = TimeSpan.FromSeconds(60);
        await using var dispatcher = Create(o);
    }

    [Fact]
    public void Missing_arguments_are_refused()
    {
        Assert.Throws<ArgumentNullException>(() => new ClientOutboxDispatcher(null!, (_, _) => new(new ScriptedA2AClient())));
        Assert.Throws<ArgumentNullException>(() => new ClientOutboxDispatcher(new InMemoryClientOutboxStore(), null!));
    }

    [Fact]
    public void Message_needs_an_id_a_peer_and_a_request()
    {
        var request = new SendMessageRequest { Message = Message.FromUser("hi") };
        Assert.Throws<ArgumentException>(() => ClientOutboxMessage.Create("", request));
        Assert.Throws<ArgumentException>(() => ClientOutboxMessage.Create("peer", request, id: ""));
        Assert.Throws<ArgumentNullException>(() => ClientOutboxMessage.Create("peer", null!));
    }

    [Fact]
    public void Create_copies_the_request_stamps_ids_and_returns_immediately()
    {
        var request = new SendMessageRequest
        {
            Tenant = "t1",
            Message = new Message { MessageId = "", Role = Role.User, Parts = [Part.FromText("hello")] },
            Metadata = new() { ["k"] = System.Text.Json.JsonSerializer.SerializeToElement("v") },
            Configuration = new SendMessageConfiguration { HistoryLength = 3, AcceptedOutputModes = ["text/plain"] },
        };

        var message = ClientOutboxMessage.Create("billing", request, "order-7");

        Assert.Equal("billing", message.Peer);
        Assert.Equal("order-7", message.Correlation);
        Assert.False(string.IsNullOrEmpty(message.Id));
        Assert.Equal("", request.Message.MessageId); // the caller's object is not changed
        Assert.False(request.Configuration.ReturnImmediately);

        var stored = A2A.Json.A2AJson.Deserialize<SendMessageRequest>(System.Text.Encoding.UTF8.GetString(message.Request.Span));
        Assert.Equal(message.Id, stored.Message.MessageId); // default id: the message id
        Assert.Equal("t1", stored.Tenant);
        Assert.True(stored.Configuration!.ReturnImmediately);
        Assert.Equal(3, stored.Configuration.HistoryLength);
        Assert.Equal(["text/plain"], stored.Configuration.AcceptedOutputModes);
        Assert.Equal("v", stored.Metadata!["k"].GetString());
    }

    [Fact]
    public void Create_uses_the_given_id_and_keeps_the_message_id()
    {
        var request = new SendMessageRequest { Message = new Message { MessageId = "m-1", Role = Role.User, Parts = [Part.FromText("x")] } };
        var message = ClientOutboxMessage.Create("peer", request, id: "entry-1");
        Assert.Equal("entry-1", message.Id);
        Assert.Equal("m-1", A2A.Json.A2AJson.Deserialize<SendMessageRequest>(System.Text.Encoding.UTF8.GetString(message.Request.Span)).Message.MessageId);
    }
}
