using System.Runtime.CompilerServices;

namespace A2A.TestKit.Tests;

public sealed class AssertionTests
{
    private static AgentTask Task(TaskState state, string id = "t1", params string[] artifactNames) => new()
    {
        Id = id,
        Status = new AgentTaskStatus { State = state },
        Artifacts = artifactNames.Select(n => new Artifact { ArtifactId = "id-" + n, Name = n, Parts = [] }).ToList(),
    };

    private static StreamResponse TaskEv(TaskState s) => StreamResponse.Of(Task(s));
    private static StreamResponse Status(TaskState s) => StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t1", Status = new AgentTaskStatus { State = s } });
    private static StreamResponse Artifact(string name) => StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "t1", Artifact = new Artifact { ArtifactId = "id-" + name, Name = name, Parts = [] } });
    private static StreamResponse Msg(string text) => StreamResponse.Of(Message.FromAgent(text));

    private static readonly StreamResponse[] Typical =
    [
        TaskEv(TaskState.Working), Status(TaskState.Working), Artifact("result"), Status(TaskState.Completed),
    ];

    [Fact]
    public void Describe_prints_one_compact_line()
    {
        Assert.Equal("Task(WORKING), Status(WORKING), Artifact(result), Status(COMPLETED)", TestEvents.Describe(Typical));
        Assert.Equal("(no events)", TestEvents.Describe([]));
        Assert.Equal("Message(\"hi\"), Status(INPUT_REQUIRED), Status(AUTH_REQUIRED)",
            TestEvents.Describe([Msg("hi"), Status(TaskState.InputRequired), Status(TaskState.AuthRequired)]));
        Assert.Equal("Artifact(id-x)", TestEvents.Describe([StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "t", Artifact = new Artifact { ArtifactId = "id-x", Parts = [] } })]));
    }

    [Fact]
    public void IsTask_and_IsMessage_return_the_value_or_fail()
    {
        var task = Task(TaskState.Working);
        var message = Message.FromAgent("hello");
        Assert.Same(task, A2AAssert.IsTask(new TaskResponse(task)));
        Assert.Same(message, A2AAssert.IsMessage(new MessageResponse(message)));

        var notTask = Assert.Throws<A2AAssertionException>(() => A2AAssert.IsTask(new MessageResponse(message)));
        Assert.Contains("Expected a task response but got a message (\"hello\")", notTask.Message, StringComparison.Ordinal);
        var notMessage = Assert.Throws<A2AAssertionException>(() => A2AAssert.IsMessage(new TaskResponse(task)));
        Assert.Contains("Expected a message response but got a task (WORKING)", notMessage.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HasState_passes_and_fails_with_both_states()
    {
        var task = Task(TaskState.Completed);
        Assert.Same(task, A2AAssert.HasState(task, TaskState.Completed));
        Assert.Same(task, A2AAssert.HasState(new TaskResponse(task), TaskState.Completed));
        var ex = Assert.Throws<A2AAssertionException>(() => A2AAssert.HasState(task, TaskState.Failed));
        Assert.Contains("FAILED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("COMPLETED", ex.Message, StringComparison.Ordinal);
        Assert.Throws<A2AAssertionException>(() => A2AAssert.HasState(new MessageResponse(Message.FromAgent("m")), TaskState.Completed));
    }

    [Fact]
    public void HasArtifact_matches_name_or_id_and_lists_artifacts_on_failure()
    {
        var task = Task(TaskState.Completed, "t1", "result", "extra");
        Assert.Equal("result", A2AAssert.HasArtifact(task, "result").Name);
        Assert.Equal("extra", A2AAssert.HasArtifact(task, "id-extra").Name);
        var ex = Assert.Throws<A2AAssertionException>(() => A2AAssert.HasArtifact(task, "missing"));
        Assert.Contains("'missing'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("result, extra", ex.Message, StringComparison.Ordinal);
        Assert.Contains("(none)", Assert.Throws<A2AAssertionException>(() => A2AAssert.HasArtifact(Task(TaskState.Working), "x")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StateOrder_is_exact_and_does_not_remove_duplicates()
    {
        A2AAssert.StateOrder(Typical, TaskState.Working, TaskState.Working, TaskState.Completed);

        var ex = Assert.Throws<A2AAssertionException>(() => A2AAssert.StateOrder(Typical, TaskState.Working, TaskState.Completed));
        Assert.Contains("Expected state order: WORKING, COMPLETED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Actual state order:   WORKING, WORKING, COMPLETED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Actual events: Task(WORKING), Status(WORKING), Artifact(result), Status(COMPLETED)", ex.Message, StringComparison.Ordinal);
        Assert.Throws<A2AAssertionException>(() => A2AAssert.StateOrder(Typical, TaskState.Completed, TaskState.Working, TaskState.Working));
    }

    [Fact]
    public void EventOrder_is_exact()
    {
        A2AAssert.EventOrder(Typical, ExpectedEvent.Task(TaskState.Working), ExpectedEvent.Status(TaskState.Working),
            ExpectedEvent.Artifact("result"), ExpectedEvent.Status(TaskState.Completed));
        A2AAssert.EventOrder(Typical, ExpectedEvent.Task(), ExpectedEvent.Status(TaskState.Working), ExpectedEvent.Artifact(), ExpectedEvent.Status(TaskState.Completed));

        var wrong = Assert.Throws<A2AAssertionException>(() => A2AAssert.EventOrder(Typical, ExpectedEvent.Task(), ExpectedEvent.Status(TaskState.Completed)));
        Assert.Contains("event 2 is Status(WORKING), expected Status(COMPLETED)", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("Expected events: Task(any), Status(COMPLETED)", wrong.Message, StringComparison.Ordinal);
        Assert.Contains("Actual events:   Task(WORKING), Status(WORKING), Artifact(result), Status(COMPLETED)", wrong.Message, StringComparison.Ordinal);

        var missing = Assert.Throws<A2AAssertionException>(() => A2AAssert.EventOrder(Typical.Take(1), ExpectedEvent.Task(), ExpectedEvent.Status(TaskState.Working)));
        Assert.Contains("event 2 is missing", missing.Message, StringComparison.Ordinal);
        var extra = Assert.Throws<A2AAssertionException>(() => A2AAssert.EventOrder(Typical, ExpectedEvent.Task()));
        Assert.Contains("event 2 (Status(WORKING)) is not expected", extra.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContainsEventOrder_allows_gaps_but_keeps_order()
    {
        A2AAssert.ContainsEventOrder(Typical, ExpectedEvent.Task(), ExpectedEvent.Artifact("result"), ExpectedEvent.Status(TaskState.Completed));
        A2AAssert.ContainsEventOrder(Typical);

        var ex = Assert.Throws<A2AAssertionException>(() =>
            A2AAssert.ContainsEventOrder(Typical, ExpectedEvent.Artifact("result"), ExpectedEvent.Status(TaskState.Working)));
        Assert.Contains("Status(WORKING) (expected event 2 of 2)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Actual events: Task(WORKING)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExpectedEvent_factories_describe_and_match()
    {
        Assert.Equal("Task(any)", ExpectedEvent.Task().ToString());
        Assert.Equal("Task(INPUT_REQUIRED)", ExpectedEvent.Task(TaskState.InputRequired).ToString());
        Assert.Equal("Message(any)", ExpectedEvent.Message().ToString());
        Assert.Equal("Artifact(any)", ExpectedEvent.Artifact().ToString());
        Assert.Equal("Artifact(result)", ExpectedEvent.Artifact("result").ToString());

        A2AAssert.EventOrder([Msg("hi")], ExpectedEvent.Message());
        A2AAssert.EventOrder([Artifact("result")], ExpectedEvent.Artifact("id-result"));
        A2AAssert.EventOrder([Msg("hi")], ExpectedEvent.Matching("a greeting", e => e is MessageEvent m && m.Message.GetText() == "hi"));
        var ex = Assert.Throws<A2AAssertionException>(() => A2AAssert.EventOrder([Msg("bye")], ExpectedEvent.Matching("a greeting", e => e is MessageEvent m && m.Message.GetText() == "hi")));
        Assert.Contains("expected a greeting", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrowsA2AAsync_returns_the_error_and_checks_the_code()
    {
        await using var host = await A2ATestHost.StartAsync<EchoExecutor>();
        var client = host.CreateClient();
        var ex = await A2AAssert.ThrowsA2AAsync(() => client.GetTaskAsync(new GetTaskRequest { Id = "nope" }));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        await A2AAssert.ThrowsA2AAsync(() => client.GetTaskAsync(new GetTaskRequest { Id = "nope" }), A2AErrorCode.TaskNotFound);

        var wrongCode = await Assert.ThrowsAsync<A2AAssertionException>(() =>
            A2AAssert.ThrowsA2AAsync(() => client.GetTaskAsync(new GetTaskRequest { Id = "nope" }), A2AErrorCode.InvalidParams));
        Assert.Contains("InvalidParams", wrongCode.Message, StringComparison.Ordinal);
        Assert.Contains("TaskNotFound", wrongCode.Message, StringComparison.Ordinal);

        var nothing = await Assert.ThrowsAsync<A2AAssertionException>(() => A2AAssert.ThrowsA2AAsync(() => System.Threading.Tasks.Task.CompletedTask));
        Assert.Contains("nothing was thrown", nothing.Message, StringComparison.Ordinal);
        var other = await Assert.ThrowsAsync<A2AAssertionException>(() => A2AAssert.ThrowsA2AAsync(() => throw new InvalidOperationException("boom")));
        Assert.Contains("InvalidOperationException: boom", other.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventuallyHasStateAsync_polls_until_the_state_and_fails_with_the_last_state()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await gate.Task.WaitAsync(ct);
            await updater.CompleteAsync("done", ct);
        });
        var client = host.CreateClient();
        var task = A2AAssert.IsTask(await client.SendMessageAsync(Ask.Text("go", returnImmediately: true)));

        var ex = await Assert.ThrowsAsync<A2AAssertionException>(() =>
            A2AAssert.EventuallyHasStateAsync(client, task.Id, TaskState.Completed, TimeSpan.FromMilliseconds(100)));
        Assert.Contains("did not reach COMPLETED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WORKING", ex.Message, StringComparison.Ordinal);

        gate.SetResult();
        var done = await A2AAssert.EventuallyHasStateAsync(client, task.Id, TaskState.Completed);
        Assert.Equal(task.Id, done.Id);

        var missing = await Assert.ThrowsAsync<A2AAssertionException>(() =>
            A2AAssert.EventuallyHasStateAsync(client, "unknown", TaskState.Completed, TimeSpan.FromMilliseconds(50)));
        Assert.Contains("not found", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectAsync_returns_the_events_of_a_stream()
    {
        await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await updater.AddArtifactAsync([Part.FromText("x")], name: "result", cancellationToken: ct);
            await updater.CompleteAsync("done", ct);
        });
        var events = await Ask.Stream(host.CreateClient(), "go").CollectAsync();
        A2AAssert.EventOrder(events, ExpectedEvent.Task(TaskState.Submitted), ExpectedEvent.Status(TaskState.Working),
            ExpectedEvent.Artifact("result"), ExpectedEvent.Status(TaskState.Completed));
    }

    private static async IAsyncEnumerable<StreamResponse> Hangs([EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return TaskEv(TaskState.Working);
        await System.Threading.Tasks.Task.Delay(Timeout.Infinite, ct);
    }

    [Fact]
    public async Task CollectAsync_does_not_hang_and_lists_the_events_so_far()
    {
        var ex = await Assert.ThrowsAsync<A2AAssertionException>(() => Hangs().CollectAsync(TimeSpan.FromMilliseconds(100)));
        Assert.Contains("did not end within", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Events so far: Task(WORKING)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CollectAsync_honors_the_callers_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var collecting = Hangs().CollectAsync(cancellationToken: cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collecting);
    }

    [Fact]
    public void The_exception_has_the_three_standard_constructors()
    {
        Assert.NotEmpty(new A2AAssertionException().Message);
        Assert.Equal("m", new A2AAssertionException("m").Message);
        var inner = new InvalidOperationException();
        Assert.Same(inner, new A2AAssertionException("m", inner).InnerException);
    }
}
