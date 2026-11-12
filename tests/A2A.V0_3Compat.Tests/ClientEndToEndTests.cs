using A2A.Client;
using A2A.Client.V0_3Compat;
using Microsoft.AspNetCore.TestHost;

namespace A2A.V0_3Compat.Tests;

/// <summary>The compat client against a real in-process server with <c>EnableV03Compat</c>, over both bindings.</summary>
public class ClientEndToEndTests
{
    /// <summary>The host's card with every interface marked 0.3, so the client has to take the v0.3 path.</summary>
    private static AgentCard V03OnlyCard()
    {
        var card = V03Host.DefaultCard();
        foreach (var itf in card.SupportedInterfaces) itf.ProtocolVersion = "0.3";
        return card;
    }

    private static (HttpClient Http, RecordingHandler Recorder) Recorded(V03Host host, string? user = "alice")
    {
        var recorder = new RecordingHandler(host.App.GetTestServer().CreateHandler());
        var http = new HttpClient(recorder) { BaseAddress = new Uri("http://localhost/") };
        if (user is not null) http.DefaultRequestHeaders.Add("X-Test-User", user);
        return (http, recorder);
    }

    private static A2AClient ClientFor(string binding, HttpClient http, AgentCard? card = null) =>
        V03CompatClientFactory.Create(card ?? V03OnlyCard(), http, new A2AClientOptions { SupportedBindings = [binding], MaxRetries = 0 });

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    [Theory, MemberData(nameof(Bindings))]
    public async Task Send_returns_a_message(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var (http, recorder) = Recorded(host);
        var response = await ClientFor(binding, http).SendMessageAsync("hello");
        Assert.Equal("hi there", Assert.IsType<TextPart>(Assert.IsType<MessageResponse>(response).Message.Parts[0]).Text);
        Assert.All(recorder.Requests, r => Assert.Equal("0.3", r.Header("A2A-Version")));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Send_returns_the_finished_task_when_blocking_and_the_working_task_when_not(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var client = ClientFor(binding, Recorded(host).Http);

        var done = Assert.IsType<TaskResponse>(await client.SendMessageAsync("task")).Task;
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal("result", done.Artifacts![0].Name);
        Assert.Equal(["part1", "part2"], done.Artifacts[0].Parts.OfType<TextPart>().Select(p => p.Text));

        var request = new SendMessageRequest
        {
            Message = Message.FromUser("slow"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        };
        var working = Assert.IsType<TaskResponse>(await client.SendMessageAsync(request)).Task;
        Assert.False(working.Status.State.IsTerminal());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Stream_ends_on_the_final_status(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var client = ClientFor(binding, Recorded(host).Http);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("task"), cancellationToken: cts.Token)) events.Add(e);

        Assert.Contains(events, e => e is ArtifactUpdateEvent);
        var last = Assert.IsType<StatusUpdateEvent>(events[^1]);
        Assert.Equal(TaskState.Completed, last.StatusUpdate.Status.State);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Get_task_cancel_and_subscribe(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var client = ClientFor(binding, Recorded(host).Http);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var started = Assert.IsType<TaskResponse>(await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("slow"),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, cancellationToken: cts.Token)).Task;

        var fetched = await client.GetTaskAsync(started.Id, historyLength: 5, cancellationToken: cts.Token);
        Assert.Equal(started.Id, fetched.Id);

        // Follow the task, then cancel it from a second call: the stream ends on the final canceled status.
        var seen = new List<StreamResponse>();
        var follow = Task.Run(async () =>
        {
            await foreach (var e in client.ObserveTaskAsync(started.Id, cancellationToken: cts.Token))
            {
                seen.Add(e);
                if (seen.Count == 1) _ = client.CancelTaskAsync(started.Id, cancellationToken: cts.Token);
            }
        }, cts.Token);
        await follow;

        Assert.Equal(TaskState.Canceled, ((StatusUpdateEvent)seen[^1]).StatusUpdate.Status.State);
        Assert.Equal(TaskState.Canceled, (await client.GetTaskAsync(started.Id, cancellationToken: cts.Token)).Status.State);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Extended_card_is_read_from_the_v03_route(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var (http, recorder) = Recorded(host);
        var card = await ClientFor(binding, http).GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest());
        Assert.StartsWith("Extended card for", card.Description, StringComparison.Ordinal);
        Assert.Contains(recorder.Requests, r => binding == ProtocolBindings.HttpJson
            ? r.Method == HttpMethod.Get && r.PathAndQuery == "/a2a/v1/card"
            : r.Body.Contains("agent/getAuthenticatedExtendedCard", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task List_tasks_is_unavailable(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var client = ClientFor(binding, Recorded(host).Http);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Missing_credentials_are_unauthenticated(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var client = ClientFor(binding, Recorded(host, user: null).Http);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("hello"));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal(401, ex.HttpStatus);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task An_unknown_task_maps_to_task_not_found(string binding)
    {
        await using var host = await V03Host.StartAsync();
        var client = ClientFor(binding, Recorded(host).Http);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("nope"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Fact]
    public async Task The_tenant_route_works_over_rest()
    {
        await using var host = await V03Host.StartAsync(pattern: "/a2a");
        var (http, recorder) = Recorded(host);
        var card = V03OnlyCard();
        var options = new A2AClientOptions { SupportedBindings = [ProtocolBindings.HttpJson], Tenant = "acme", MaxRetries = 0 };
        var client = V03CompatClientFactory.Create(card, http, options);
        var response = await client.SendMessageAsync("hello");
        Assert.IsType<MessageResponse>(response);
        Assert.Contains(recorder.Requests, r => r.PathAndQuery == "/a2a/acme/v1/message:send");
    }

    // ---- against the real card ----

    [Fact]
    public async Task An_agent_offering_both_versions_takes_the_v1_path_from_the_fetched_card()
    {
        await using var host = await V03Host.StartAsync();
        var (http, recorder) = Recorded(host);
        var client = await V03CompatClientFactory.CreateAsync(new Uri("http://localhost/a2a"), http);
        Assert.IsType<MessageResponse>(await client.SendMessageAsync("hello"));
        Assert.Contains(recorder.Requests, r => r.PathAndQuery.EndsWith("agent-card.json", StringComparison.Ordinal));
        Assert.All(recorder.Requests.Where(r => !r.PathAndQuery.EndsWith("agent-card.json", StringComparison.Ordinal)), r => Assert.Equal("1.0", r.Header("A2A-Version")));
    }

    [Fact]
    public async Task The_fetched_blended_card_still_has_a_v03_view_for_the_v03_path()
    {
        await using var host = await V03Host.StartAsync();
        // Fetch the blended card as raw JSON: the v0.3 members are there. A v0.3-only client reads them.
        using var response = await host.Http(null).GetAsync("/.well-known/agent-card.json");
        var card = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("0.3.0", (string?)card["protocolVersion"]);
        card.Remove("supportedInterfaces"); // what a v0.3-only agent would serve
        var converted = A2A.V0_3Compat.V03CardCodec.FromV03(card);
        Assert.All(converted.SupportedInterfaces, i => Assert.Equal("0.3", i.ProtocolVersion));

        var (http, _) = Recorded(host);
        var client = V03CompatClientFactory.Create(converted, http, new A2AClientOptions { SupportedBindings = [ProtocolBindings.JsonRpc], MaxRetries = 0 });
        Assert.IsType<MessageResponse>(await client.SendMessageAsync("hello"));
    }
}
