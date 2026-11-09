using A2A.Client;

namespace A2A.TestKit.Tests;

public sealed class PeerTests
{
    [Fact]
    public async Task Reply_answers_with_a_message_and_records_the_call()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.WhenText("ping", r => r.Reply("pong")));
        var message = A2AAssert.IsMessage(await peer.CreateClient().SendMessageAsync(Ask.Text("ping please")));
        Assert.Equal("pong", message.GetText());

        var call = Assert.Single(peer.Calls);
        Assert.Equal("ping please", call.Text);
        Assert.Equal("ping please", call.Message.GetText());
        Assert.True(call.Matched);
        Assert.True(call.IsNewTask);
        Assert.False(string.IsNullOrEmpty(call.TaskId));
        Assert.False(string.IsNullOrEmpty(call.ContextId));
        Assert.Empty(peer.UnmatchedCalls);
    }

    [Fact]
    public async Task Working_artifact_and_complete_stream_in_order()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r
            .Working("on it")
            .Artifact("part one", "result")
            .Artifact([Part.FromText("a"), Part.FromText("b")], "pair")
            .Complete("done")));
        var events = await Ask.Stream(peer.CreateClient(ProtocolBindings.HttpJson), "go").CollectAsync();
        A2AAssert.EventOrder(events, ExpectedEvent.Task(TaskState.Submitted), ExpectedEvent.Status(TaskState.Working),
            ExpectedEvent.Artifact("result"), ExpectedEvent.Artifact("pair"), ExpectedEvent.Status(TaskState.Completed));
        var final = ((StatusUpdateEvent)events[^1]).StatusUpdate.Status;
        Assert.Equal("done", final.Message!.GetText());
        var pair = ((ArtifactUpdateEvent)events[3]).ArtifactUpdate.Artifact;
        Assert.Equal(2, pair.Parts.Count);
    }

    [Fact]
    public async Task Artifacts_reach_a_non_streaming_caller()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.Working().Artifact("text", "result").Complete()));
        var task = A2AAssert.HasState(await peer.CreateClient().SendMessageAsync(Ask.Text("go")), TaskState.Completed);
        Assert.Equal("text", A2AAssert.HasArtifact(task, "result").Parts.OfType<TextPart>().Single().Text);
    }

    [Fact]
    public async Task RequireInput_and_RequireAuth_interrupt_the_task()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s
            .WhenText("input", r => r.RequireInput("which one?"))
            .WhenText("auth", r => r.RequireAuth("sign in")));
        var client = peer.CreateClient();
        var input = A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("input")), TaskState.InputRequired);
        Assert.Equal("which one?", input.Status.Message!.GetText());
        var auth = A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("auth")), TaskState.AuthRequired);
        Assert.Equal("sign in", auth.Status.Message!.GetText());
    }

    [Fact]
    public async Task Fail_and_Reject_end_the_task()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s
            .WhenText("fail", r => r.Fail("no luck"))
            .WhenText("reject", r => r.Reject("not mine")));
        var client = peer.CreateClient();
        var failed = A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("fail")), TaskState.Failed);
        Assert.Equal("no luck", failed.Status.Message!.GetText());
        var rejected = A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("reject")), TaskState.Rejected);
        Assert.Equal("not mine", rejected.Status.Message!.GetText());
    }

    [Fact]
    public async Task Throw_makes_the_task_fail_or_the_call_fail()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.Throw(new InvalidOperationException("kaboom"))));
        var client = peer.CreateClient();
        try
        {
            A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("go")), TaskState.Failed);
        }
        catch (A2AException)
        {
            // the server may answer with an error instead of a failed task
        }
        Assert.Single(peer.Calls);
    }

    [Fact]
    public async Task Run_gives_access_to_the_context_and_the_updater()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.Run(async (context, updater, ct) =>
            await updater.CompleteAsync("shout: " + context.UserText.ToUpperInvariant(), ct))));
        var task = A2AAssert.HasState(await peer.CreateClient().SendMessageAsync(Ask.Text("quiet")), TaskState.Completed);
        Assert.Equal("shout: QUIET", task.Status.Message!.GetText());
    }

    [Fact]
    public async Task Steps_run_in_the_order_they_were_added()
    {
        var order = new List<string>();
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r
            .Run((_, _, _) => { order.Add("one"); return Task.CompletedTask; })
            .Run((_, _, _) => { order.Add("two"); return Task.CompletedTask; })
            .Complete()));
        await peer.CreateClient().SendMessageAsync(Ask.Text("go"));
        Assert.Equal(["one", "two"], order);
    }

    [Fact]
    public async Task WaitFor_holds_the_answer_until_the_gate_opens()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.WaitFor(gate.Task).Reply("late")));
        var sending = peer.CreateClient().SendMessageAsync(Ask.Text("go"));
        await peer.WaitForCallAsync();
        Assert.False(sending.IsCompleted); // the peer is stuck at the gate
        gate.SetResult();
        Assert.Equal("late", A2AAssert.IsMessage(await sending).GetText());
    }

    [Fact]
    public async Task Delay_waits_on_the_host_clock()
    {
        var delay = TimeSpan.FromMinutes(7);
        var spy = new SpyClock(new TestClock());
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.Delay(delay).Reply("slow answer")), o => o.TimeProvider = spy);
        var sending = peer.CreateClient().SendMessageAsync(Ask.Text("go"));
        await spy.WhenTimerAsync(delay).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(sending.IsCompleted);

        spy.Inner.Advance(delay);

        Assert.Equal("slow answer", A2AAssert.IsMessage(await sending).GetText());
    }

    [Fact]
    public async Task Queued_answers_win_over_rules_and_rules_over_otherwise()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s
            .WhenText("a", r => r.Reply("first rule"))
            .WhenText("a", r => r.Reply("second rule"))
            .When(c => c.UserText.StartsWith('b'), r => r.Reply("predicate rule"))
            .Otherwise(r => r.Reply("fallback"))
            .Enqueue(r => r.Reply("queued one"))
            .Enqueue(r => r.Reply("queued two")));
        var client = peer.CreateClient();
        async Task<string> Say(string text) => A2AAssert.IsMessage(await client.SendMessageAsync(Ask.Text(text))).GetText();

        Assert.Equal("queued one", await Say("a"));
        Assert.Equal("queued two", await Say("b"));
        Assert.Equal("first rule", await Say("a"));
        Assert.Equal("predicate rule", await Say("b"));
        Assert.Equal("fallback", await Say("zzz"));

        peer.Script.Enqueue(r => r.Reply("added later")).WhenText("zzz", r => r.Reply("late rule"));
        Assert.Equal("added later", await Say("zzz"));
        Assert.Equal("late rule", await Say("zzz"));
        Assert.Empty(peer.UnmatchedCalls);
        Assert.Equal(7, peer.Calls.Count);
    }

    [Fact]
    public async Task A_call_without_a_match_fails_clearly_and_is_recorded()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.WhenText("known", r => r.Reply("ok")));
        var failed = A2AAssert.HasState(await peer.CreateClient().SendMessageAsync(Ask.Text("surprise")), TaskState.Failed);
        Assert.Contains("surprise", failed.Status.Message!.GetText(), StringComparison.Ordinal);
        var call = Assert.Single(peer.UnmatchedCalls);
        Assert.Equal("surprise", call.Text);
        Assert.False(call.Matched);
        Assert.Single(peer.Calls);
    }

    [Fact]
    public async Task Multi_turn_input_required_reaches_the_same_task()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s
            .WhenText("order", r => r.RequireInput("Which size?"))
            .WhenText("large", r => r.Complete("ordered a large one")));
        var client = peer.CreateClient();
        var asked = A2AAssert.HasState(await client.SendMessageAsync(Ask.Text("order a coffee")), TaskState.InputRequired);

        var answered = await client.SendMessageAsync(Ask.Text("large", asked.Id, asked.ContextId));

        var done = A2AAssert.HasState(answered, TaskState.Completed);
        Assert.Equal(asked.Id, done.Id);
        Assert.Equal(2, peer.Calls.Count);
        Assert.True(peer.Calls[0].IsNewTask);
        Assert.False(peer.Calls[1].IsNewTask);
        Assert.Equal(peer.Calls[0].TaskId, peer.Calls[1].TaskId);
        Assert.Equal(peer.Calls[0].ContextId, peer.Calls[1].ContextId);
    }

    [Fact]
    public async Task The_peer_can_be_found_through_its_card()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.Reply("hello")));
        var client = await A2AClientFactory.CreateAsync(peer.Url, peer.CreateHttpClient());
        Assert.Equal("Fake Peer", client.AgentCard!.Name);
        Assert.Equal("Fake Peer", peer.Card.Name);
        Assert.Equal("hello", A2AAssert.IsMessage(await client.SendMessageAsync(Ask.Text("hi"))).GetText());
        Assert.NotNull(peer.CreateHandler());
        Assert.Same(peer.Host.Url, peer.Url);
    }

    [Fact]
    public async Task ConfigureCard_can_rename_the_peer()
    {
        await using var peer = await FakePeerAgent.StartAsync(_ => { }, o => o.ConfigureCard = c => c.Name = "Weather Peer");
        Assert.Equal("Weather Peer", peer.Card.Name);
    }

    [Fact]
    public async Task WaitForCallAsync_finds_past_and_future_calls_and_reports_what_arrived()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.Reply("ok")));
        var client = peer.CreateClient();
        await client.SendMessageAsync(Ask.Text("first"));
        Assert.Equal("first", (await peer.WaitForCallAsync(c => c.Text == "first")).Text); // already there

        var waiting = peer.WaitForCallAsync(c => c.Text == "second");
        Assert.False(waiting.IsCompleted);
        await client.SendMessageAsync(Ask.Text("second"));
        Assert.Equal("second", (await waiting).Text);

        var ex = await Assert.ThrowsAsync<A2AAssertionException>(() => peer.WaitForCallAsync(c => c.Text == "third", TimeSpan.FromMilliseconds(50)));
        Assert.Contains("\"first\", \"second\"", ex.Message, StringComparison.Ordinal);

        using var cts = new CancellationTokenSource();
        var canceled = peer.WaitForCallAsync(c => c.Text == "never", cancellationToken: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
    }

    [Fact]
    public async Task WaitForCallAsync_with_no_calls_says_so()
    {
        await using var peer = await FakePeerAgent.StartAsync(_ => { });
        var ex = await Assert.ThrowsAsync<A2AAssertionException>(() => peer.WaitForCallAsync(timeout: TimeSpan.FromMilliseconds(30)));
        Assert.Contains("No call arrived", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Anonymous_and_user_clients_both_reach_the_peer()
    {
        await using var peer = await FakePeerAgent.StartAsync(s => s.Otherwise(r => r.Reply("ok")));
        await peer.CreateClient(user: "alice").SendMessageAsync(Ask.Text("a"));
        await peer.CreateHttpClient("bob").GetAsync(new Uri("/.well-known/agent-card.json", UriKind.Relative));
        Assert.Single(peer.Calls);
    }
}
