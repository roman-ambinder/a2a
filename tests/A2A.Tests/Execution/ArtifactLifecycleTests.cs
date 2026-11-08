using System.Text.Json;
using A2A.Client;
using A2A.Integrity;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Execution;

/// <summary>Artifact lifecycle (create, append, replace, seal) and the optional integrity hash, through the engine and the <see cref="TaskUpdater"/> (S27).</summary>
public sealed class ArtifactLifecycleTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string SealedKey = "a2a.artifact.sealed";
    private const string SealedMessage = "Artifact 'x' is sealed: lastChunk was already sent. Send it with append: false to replace it, or use a new artifact id.";

    // sha256 of "foo" and "foobar" (computed outside .NET)
    private const string Foo = "sha256:2c26b46b68ffc68ff99b453c1d30413413422d706483bfa0f98a5e886266e7ae";
    private const string FooBar = "sha256:c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2";

    private sealed class Executor(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute,
        Func<AgentTask, TaskUpdater, CancellationToken, Task>? cancel = null) : IAgentExecutor
    {
        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct) => execute(context, updater, ct);

        public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken ct) =>
            cancel is null ? updater.CancelAsync(cancellationToken: ct).AsTask() : cancel(task, updater, ct);
    }

    private sealed class Factory(IAgentExecutor executor) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken ct) => ValueTask.FromResult(new AgentExecutorLease(executor, scope: null));
    }

    private sealed class CapturingLogger : ILogger<TaskExecutionEngine>
    {
        public List<(EventId Id, LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((eventId, logLevel, formatter(state, exception)));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServerCallContext _call = ServerCallContext.Anonymous();
        public InMemoryTaskStore Tasks { get; }
        public InMemoryEventStore Events { get; }
        public TaskExecutionEngine Engine { get; }
        public CapturingLogger Log { get; } = new();

        public Rig(IAgentExecutor executor)
        {
            var options = new A2AServerOptions();
            options.AgentCard.Name = "Artifact Test Agent";
            Events = new InMemoryEventStore(options.Streaming);
            Tasks = new InMemoryTaskStore(Events, new InMemoryPushOutbox());
            Engine = new TaskExecutionEngine(Tasks, Events, new Factory(executor), new TaskEventBus(), new InMemoryPushOutbox(),
                new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), Tasks, options),
                new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options),
                options, new AgentCardProvider(options, []), Log);
        }

        public static Rig For(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) => new(new Executor(execute));

        public AgentRequestContext NewTask() => new() { Message = Message.FromUser("go"), TaskId = TaskIds.New(), ContextId = TaskIds.New(), Call = _call };

        public AgentRequestContext FollowUp(AgentTask task) => new()
        {
            Message = Message.FromUser("again"), TaskId = task.Id, ContextId = task.ContextId, Call = _call, Task = task.DeepCopy(),
        };

        public TaskKey Key(string taskId) => new(_call.Tenant, _call.Principal, taskId);

        /// <summary>Runs one execution to its end and returns the stored task.</summary>
        public async Task<AgentTask> RunAsync(AgentRequestContext context)
        {
            var exec = await Engine.StartForTestAsync(context);
            await exec.Completion.WaitAsync(Wait);
            await Engine.WhenIdleAsync().WaitAsync(Wait);
            return await StoredAsync(context.TaskId);
        }

        public async Task<AgentTask> StoredAsync(string taskId) => (await Tasks.GetAsync(Key(taskId)))!.Task;

        public async Task<List<StreamResponse>> EventsAsync(string taskId)
        {
            var list = new List<StreamResponse>();
            await foreach (var e in Events.ReadAfterAsync(Key(taskId), 0)) list.Add(e.Event);
            return list;
        }

        public ValueTask DisposeAsync() => Engine.DisposeAsync();
    }

    private static List<Part> Text(params string[] texts) => texts.Select(t => (Part)Part.FromText(t)).ToList();

    private static string[] TextsOf(Artifact a) => a.Parts.OfType<TextPart>().Select(p => p.Text).ToArray();

    private static bool IsSealed(Artifact a) => a.Metadata is { } m && m.TryGetValue(SealedKey, out var v) && v.ValueKind == JsonValueKind.True;

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // The lifecycle table ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Append_to_an_unknown_artifact_creates_it()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text("a"), "x", append: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var task = await rig.RunAsync(rig.NewTask());
        Assert.Equal(["a"], TextsOf(Assert.Single(task.Artifacts!)));
        Assert.False(IsSealed(task.Artifacts![0]));
    }

    [Fact]
    public async Task Append_to_an_open_artifact_adds_parts_and_replace_starts_over()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text("a"), "x", name: "n", cancellationToken: ct);
            await u.AddArtifactAsync(Text("b"), "x", append: true, cancellationToken: ct);
            await u.AddArtifactAsync(Text("c"), "y", cancellationToken: ct);
            await u.AddArtifactAsync(Text("d"), "y", cancellationToken: ct); // replace
            await u.CompleteAsync(cancellationToken: ct);
        });
        var task = await rig.RunAsync(rig.NewTask());
        Assert.Equal(["a", "b"], TextsOf(task.Artifacts!.Single(a => a.ArtifactId == "x")));
        Assert.Equal("n", task.Artifacts!.Single(a => a.ArtifactId == "x").Name);
        Assert.Equal(["d"], TextsOf(task.Artifacts!.Single(a => a.ArtifactId == "y")));
    }

    [Fact]
    public async Task Last_chunk_seals_the_stored_artifact_but_not_the_event()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text("a"), "x", cancellationToken: ct);
            await u.AddArtifactAsync(Text("b"), "x", append: true, lastChunk: true, cancellationToken: ct);
            await u.AddArtifactAsync(Text("c"), "single", lastChunk: true, cancellationToken: ct); // created sealed
            await u.CompleteAsync(cancellationToken: ct);
        });
        var context = rig.NewTask();
        var task = await rig.RunAsync(context);
        Assert.All(task.Artifacts!, a => Assert.True(IsSealed(a)));
        Assert.Equal(["a", "b"], TextsOf(task.Artifacts!.Single(a => a.ArtifactId == "x")));
        // The events on the wire do not carry the seal key.
        var artifactEvents = (await rig.EventsAsync(context.TaskId)).OfType<ArtifactUpdateEvent>().ToList();
        Assert.Equal(3, artifactEvents.Count);
        Assert.All(artifactEvents, e => Assert.Null(e.ArtifactUpdate.Artifact.Metadata));
    }

    [Fact]
    public async Task Append_after_seal_throws_at_the_call_and_nothing_is_queued()
    {
        InvalidOperationException? caught = null;
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text("a"), "x", lastChunk: true, cancellationToken: ct);
            try { await u.AddArtifactAsync(Text("late"), "x", append: true, cancellationToken: ct); }
            catch (InvalidOperationException ex) { caught = ex; }
            await u.CompleteAsync(cancellationToken: ct); // the executor can go on
        });
        var context = rig.NewTask();
        var task = await rig.RunAsync(context);
        Assert.Equal(SealedMessage, caught?.Message);
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(["a"], TextsOf(Assert.Single(task.Artifacts!)));
        // snapshot, artifact, status: the rejected append never became an event
        Assert.Equal(3, (await rig.EventsAsync(context.TaskId)).Count);
    }

    [Fact]
    public async Task An_uncaught_append_after_seal_fails_the_task()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text("a"), "x", lastChunk: true, cancellationToken: ct);
            await u.AddArtifactAsync(Text("late"), "x", append: true, cancellationToken: ct);
        });
        var task = await rig.RunAsync(rig.NewTask());
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal("The agent failed to process the request.", task.Status.Message!.GetText());
        Assert.Equal(["a"], TextsOf(Assert.Single(task.Artifacts!)));
    }

    [Fact]
    public async Task Replace_after_seal_works_and_the_new_artifact_is_open_unless_sealed_again()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text("a"), "x", lastChunk: true, cancellationToken: ct);
            await u.AddArtifactAsync(Text("b"), "x", cancellationToken: ct); // replace: open
            await u.AddArtifactAsync(Text("c"), "x", append: true, cancellationToken: ct); // so this works
            await u.AddArtifactAsync(Text("d"), "z", lastChunk: true, cancellationToken: ct);
            await u.AddArtifactAsync(Text("e"), "z", lastChunk: true, cancellationToken: ct); // replace, sealed again
            await u.CompleteAsync(cancellationToken: ct);
        });
        var task = await rig.RunAsync(rig.NewTask());
        var x = task.Artifacts!.Single(a => a.ArtifactId == "x");
        Assert.Equal(["b", "c"], TextsOf(x));
        Assert.False(IsSealed(x));
        var z = task.Artifacts!.Single(a => a.ArtifactId == "z");
        Assert.Equal(["e"], TextsOf(z));
        Assert.True(IsSealed(z));
    }

    [Fact]
    public async Task Replace_after_seal_drops_the_old_seal_and_the_old_metadata()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text("a"), "x", metadata: new() { ["k"] = Json("1") }, lastChunk: true, cancellationToken: ct);
            await u.AddArtifactAsync(Text("b"), "x", cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var x = Assert.Single((await rig.RunAsync(rig.NewTask())).Artifacts!);
        Assert.Null(x.Metadata);
    }

    // Empty parts -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Empty_parts_are_allowed_only_to_append_to_an_artifact_that_exists()
    {
        var errors = new List<Exception>();
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            async Task Try(Func<Task> call) { try { await call(); } catch (Exception ex) { errors.Add(ex); } }
            await Try(async () => await u.AddArtifactAsync([], "x", cancellationToken: ct)); // new, no append
            await Try(async () => await u.AddArtifactAsync([], "x", append: true, cancellationToken: ct)); // unknown artifact
            await u.AddArtifactAsync(Text("a"), "x", cancellationToken: ct);
            await Try(async () => await u.AddArtifactAsync([], "x", cancellationToken: ct)); // replace with nothing
            await u.AddArtifactAsync([], "x", append: true, lastChunk: true, metadata: new() { ["k"] = Json("1") }, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var task = await rig.RunAsync(rig.NewTask());
        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.IsType<ArgumentException>(e));
        var x = Assert.Single(task.Artifacts!);
        Assert.Equal(["a"], TextsOf(x));
        Assert.True(IsSealed(x));
        Assert.Equal(1, x.Metadata!["k"].GetInt32());
    }

    [Fact]
    public async Task A_rejected_artifact_call_is_not_a_task_event_so_a_direct_reply_still_works()
    {
        ArgumentException? caught = null;
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            try { await u.AddArtifactAsync([], "x", cancellationToken: ct); }
            catch (ArgumentException ex) { caught = ex; }
            Assert.False(u.HasTask);
            await u.ReplyAsync("hi", ct);
        });
        var context = rig.NewTask();
        var exec = await rig.Engine.StartForTestAsync(context);
        var seen = new List<SequencedEvent>();
        await foreach (var e in exec.Outgoing.Reader.ReadAllAsync().WithCancellation(new CancellationTokenSource(Wait).Token)) seen.Add(e);
        await exec.Completion.WaitAsync(Wait);
        Assert.NotNull(caught);
        Assert.Equal("hi", Assert.IsType<MessageEvent>(Assert.Single(seen).Event).Message.GetText());
        Assert.Null(await rig.Tasks.GetAsync(rig.Key(context.TaskId)));
    }

    [Fact]
    public async Task Empty_parts_can_seal_an_artifact_an_earlier_run_left()
    {
        await using var rig = Rig.For(async (c, u, ct) =>
        {
            if (c.IsNewTask)
            {
                await u.AddArtifactAsync(Text("a"), "x", cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
            }
            else
            {
                await u.AddArtifactAsync([], "x", append: true, lastChunk: true, cancellationToken: ct);
                await u.CompleteAsync(cancellationToken: ct);
            }
        });
        var first = await rig.RunAsync(rig.NewTask());
        var second = await rig.RunAsync(rig.FollowUp(first));
        var x = Assert.Single(second.Artifacts!);
        Assert.Equal(["a"], TextsOf(x));
        Assert.True(IsSealed(x));
    }

    [Fact]
    public async Task A_part_with_empty_content_is_a_normal_part()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddArtifactAsync(Text(""), "x", cancellationToken: ct);
            await u.AddArtifactAsync(Text("", "b"), "x", append: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var x = Assert.Single((await rig.RunAsync(rig.NewTask())).Artifacts!);
        Assert.Equal(["", "", "b"], TextsOf(x));
    }

    // The seal key is the server's ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_seal_key_set_by_the_agent_is_removed_unless_the_last_chunk_seals()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            var fake = new Dictionary<string, JsonElement> { [SealedKey] = Json("true"), ["k"] = Json("1") };
            await u.AddArtifactAsync(Text("a"), "x", metadata: fake, cancellationToken: ct); // create
            await u.AddArtifactAsync(Text("b"), "x", append: true, metadata: fake, cancellationToken: ct); // merge
            await u.AddArtifactAsync(Text("c"), "x", append: true, cancellationToken: ct); // still open
            await u.AddArtifactAsync(Text("d"), "y", metadata: new() { [SealedKey] = Json("false") }, lastChunk: true, cancellationToken: ct);
            await u.AddArtifactAsync(Text("e"), "w", metadata: new() { [SealedKey] = Json("\"no\"") }, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var task = await rig.RunAsync(rig.NewTask());
        var x = task.Artifacts!.Single(a => a.ArtifactId == "x");
        Assert.Equal(["a", "b", "c"], TextsOf(x));
        Assert.False(x.Metadata!.ContainsKey(SealedKey));
        Assert.Equal(1, x.Metadata["k"].GetInt32());
        Assert.True(IsSealed(task.Artifacts!.Single(a => a.ArtifactId == "y")));
        Assert.Null(task.Artifacts!.Single(a => a.ArtifactId == "w").Metadata);
    }

    // The engine backstop ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Engine_drops_an_append_to_a_sealed_artifact_when_two_executions_race()
    {
        var sealedStored = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateAppend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = Rig.For(async (c, u, ct) =>
        {
            if (c.IsNewTask)
            {
                await u.StartWorkAsync(cancellationToken: ct);
                await u.AddArtifactAsync(Text("one"), "x", cancellationToken: ct);
                await seal.Task.WaitAsync(ct);
                await u.AddArtifactAsync([], "x", append: true, lastChunk: true, cancellationToken: ct);
                sealedStored.TrySetResult();
                await finish.Task.WaitAsync(ct);
                await u.CompleteAsync(cancellationToken: ct);
            }
            else
            {
                // This updater knows the artifact as open, so it lets the append through.
                secondReady.TrySetResult();
                await lateAppend.Task.WaitAsync(ct);
                await u.AddArtifactAsync(Text("late"), "x", append: true, cancellationToken: ct);
            }
        });
        var context = rig.NewTask();
        var first = await rig.Engine.StartForTestAsync(context);
        await WaitForAsync(async () => (await rig.Tasks.GetAsync(rig.Key(context.TaskId)))?.Task.Artifacts is { Count: 1 });
        var second = await rig.Engine.StartForTestAsync(rig.FollowUp(await rig.StoredAsync(context.TaskId)));
        await secondReady.Task.WaitAsync(Wait);

        seal.SetResult();
        await sealedStored.Task.WaitAsync(Wait);
        await WaitForAsync(async () => (await rig.Tasks.GetAsync(rig.Key(context.TaskId)))?.Task.Artifacts is [{ } a] && IsSealed(a));
        var sequence = await rig.Events.LastSequenceAsync(rig.Key(context.TaskId));

        lateAppend.SetResult();
        await second.Completion.WaitAsync(Wait);
        finish.SetResult();
        await first.Completion.WaitAsync(Wait);

        var task = await rig.StoredAsync(context.TaskId);
        Assert.Equal(["one"], TextsOf(Assert.Single(task.Artifacts!)));
        Assert.Equal(TaskState.Completed, task.Status.State);
        // The late append was never stored: the only event after the seal is the completion.
        Assert.Equal(sequence + 1, await rig.Events.LastSequenceAsync(rig.Key(context.TaskId)));
        var warning = Assert.Single(rig.Log.Entries, e => e.Id.Id == 224);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(context.TaskId, warning.Message);
        Assert.Contains("x", warning.Message);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(5);
        }
    }

    // Follow-up runs --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_follow_up_run_still_cannot_append_to_a_sealed_artifact()
    {
        InvalidOperationException? caught = null;
        await using var rig = Rig.For(async (c, u, ct) =>
        {
            if (c.IsNewTask)
            {
                await u.AddArtifactAsync(Text("a"), "x", lastChunk: true, cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
                return;
            }
            try { await u.AddArtifactAsync(Text("late"), "x", append: true, cancellationToken: ct); }
            catch (InvalidOperationException ex) { caught = ex; }
            await u.AddArtifactAsync(Text("b"), "x", cancellationToken: ct); // replace is fine
            await u.CompleteAsync(cancellationToken: ct);
        });
        var first = await rig.RunAsync(rig.NewTask());
        var second = await rig.RunAsync(rig.FollowUp(first));
        Assert.Equal(SealedMessage, caught?.Message);
        Assert.Equal(["b"], TextsOf(Assert.Single(second.Artifacts!)));
    }

    [Fact]
    public async Task CancelAsync_knows_the_seals_of_the_stored_task()
    {
        InvalidOperationException? caught = null;
        var executor = new Executor(
            async (_, u, ct) =>
            {
                await u.AddArtifactAsync(Text("a"), "x", lastChunk: true, cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
            },
            async (_, u, ct) =>
            {
                try { await u.AddArtifactAsync(Text("late"), "x", append: true, cancellationToken: ct); }
                catch (InvalidOperationException ex) { caught = ex; }
                await u.CancelAsync(cancellationToken: ct);
            });
        await using var rig = new Rig(executor);
        var context = rig.NewTask();
        var stored = await rig.RunAsync(context);

        var agent = await rig.Engine.RentAgentAsync(context.Call.Tenant, null, CancellationToken.None);
        var exec = await rig.Engine.StartCancelAsync(rig.Key(context.TaskId), stored, null, agent, pushEnabled: false);
        await exec.Completion.WaitAsync(Wait);
        await rig.Engine.WhenIdleAsync().WaitAsync(Wait);

        Assert.Equal(SealedMessage, caught?.Message);
        Assert.Equal(TaskState.Canceled, (await rig.StoredAsync(context.TaskId)).Status.State);
    }

    // The hash helper -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Hashed_chunks_carry_the_hash_of_the_artifact_so_far()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            Assert.Equal("x", await u.AddHashedArtifactAsync(Text("foo"), "x", name: "n", cancellationToken: ct));
            await u.AddHashedArtifactAsync(Text("bar"), "x", append: true, lastChunk: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var context = rig.NewTask();
        var task = await rig.RunAsync(context);
        var events = (await rig.EventsAsync(context.TaskId)).OfType<ArtifactUpdateEvent>().Select(e => e.ArtifactUpdate).ToList();
        Assert.Equal(Foo, events[0].Artifact.Metadata!["sha256"].GetString());
        Assert.Equal(FooBar, events[1].Artifact.Metadata!["sha256"].GetString());
        // The stored artifact's hash always matches its stored parts.
        var x = Assert.Single(task.Artifacts!);
        Assert.Equal(FooBar, x.Metadata!["sha256"].GetString());
        Assert.True(IsSealed(x));
        Assert.Equal(ArtifactVerificationStatus.Verified, ArtifactVerifier.Verify(x).Status);
    }

    [Fact]
    public async Task Hashed_helper_overwrites_a_sha256_in_metadata_and_keeps_other_keys_without_changing_the_callers_dictionary()
    {
        var mine = new Dictionary<string, JsonElement> { ["sha256"] = Json("\"sha256:bogus\""), ["k"] = Json("1") };
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddHashedArtifactAsync(Text("foo"), "x", metadata: mine, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var x = Assert.Single((await rig.RunAsync(rig.NewTask())).Artifacts!);
        Assert.Equal(Foo, x.Metadata!["sha256"].GetString());
        Assert.Equal(1, x.Metadata["k"].GetInt32());
        Assert.Equal("sha256:bogus", mine["sha256"].GetString());
    }

    [Fact]
    public async Task Hashed_seal_only_event_repeats_the_hash()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddHashedArtifactAsync(Text("foo"), "x", cancellationToken: ct);
            await u.AddHashedArtifactAsync([], "x", append: true, lastChunk: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var context = rig.NewTask();
        var task = await rig.RunAsync(context);
        var last = (await rig.EventsAsync(context.TaskId)).OfType<ArtifactUpdateEvent>().Last().ArtifactUpdate;
        Assert.Empty(last.Artifact.Parts);
        Assert.Equal(Foo, last.Artifact.Metadata!["sha256"].GetString());
        var x = Assert.Single(task.Artifacts!);
        Assert.Equal(["foo"], TextsOf(x));
        Assert.True(IsSealed(x));
        Assert.Equal(ArtifactVerificationStatus.Verified, ArtifactVerifier.Verify(x).Status);
    }

    [Fact]
    public async Task Hashed_append_false_starts_the_hash_over()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddHashedArtifactAsync(Text("zzz"), "x", cancellationToken: ct);
            await u.AddHashedArtifactAsync(Text("foo"), "x", cancellationToken: ct); // replace
            await u.AddHashedArtifactAsync(Text("bar"), "x", append: true, lastChunk: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var x = Assert.Single((await rig.RunAsync(rig.NewTask())).Artifacts!);
        Assert.Equal(FooBar, x.Metadata!["sha256"].GetString());
        Assert.Equal(ArtifactVerificationStatus.Verified, ArtifactVerifier.Verify(x).Status);
    }

    [Fact]
    public async Task Hashed_append_to_an_unknown_artifact_creates_it_with_a_hash_of_that_chunk()
    {
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddHashedArtifactAsync(Text("foo"), "x", append: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var x = Assert.Single((await rig.RunAsync(rig.NewTask())).Artifacts!);
        Assert.Equal(Foo, x.Metadata!["sha256"].GetString());
    }

    [Fact]
    public async Task Mixing_plain_and_hashed_chunks_throws()
    {
        var errors = new List<Exception>();
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            async Task Try(Func<Task> call) { try { await call(); } catch (Exception ex) { errors.Add(ex); } }
            await u.AddHashedArtifactAsync(Text("foo"), "h", cancellationToken: ct);
            await Try(async () => await u.AddArtifactAsync(Text("x"), "h", append: true, cancellationToken: ct)); // plain append to a hashed one
            await u.AddArtifactAsync(Text("foo"), "p", cancellationToken: ct);
            await Try(async () => await u.AddHashedArtifactAsync(Text("x"), "p", append: true, cancellationToken: ct)); // hashed append to a plain one
            await u.AddArtifactAsync(Text("y"), "h", cancellationToken: ct); // a plain replace is fine
            // After the plain replace, the content of "h" is unknown to the hash.
            await Try(async () => await u.AddHashedArtifactAsync(Text("z"), "h", append: true, cancellationToken: ct));
            await u.CompleteAsync(cancellationToken: ct);
        });
        var task = await rig.RunAsync(rig.NewTask());
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal(3, errors.Count);
        Assert.All(errors, e => Assert.IsType<InvalidOperationException>(e));
        Assert.Equal(["foo"], TextsOf(task.Artifacts!.Single(a => a.ArtifactId == "p")));
        Assert.Equal(["y"], TextsOf(task.Artifacts!.Single(a => a.ArtifactId == "h")));
    }

    [Fact]
    public async Task Hashed_append_to_a_sealed_artifact_throws_and_hashed_replace_works()
    {
        InvalidOperationException? caught = null;
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddHashedArtifactAsync(Text("a"), "x", lastChunk: true, cancellationToken: ct);
            try { await u.AddHashedArtifactAsync(Text("b"), "x", append: true, cancellationToken: ct); }
            catch (InvalidOperationException ex) { caught = ex; }
            await u.AddHashedArtifactAsync(Text("foo"), "x", cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var x = Assert.Single((await rig.RunAsync(rig.NewTask())).Artifacts!);
        Assert.Equal(SealedMessage, caught?.Message);
        Assert.Equal(Foo, x.Metadata!["sha256"].GetString());
        Assert.False(IsSealed(x));
    }

    [Fact]
    public async Task A_data_part_that_cannot_be_hashed_throws_and_leaves_the_hash_as_it_was()
    {
        InvalidOperationException? caught = null;
        await using var rig = Rig.For(async (_, u, ct) =>
        {
            await u.AddHashedArtifactAsync(Text("foo"), "x", cancellationToken: ct);
            try { await u.AddHashedArtifactAsync([Part.FromData(Json("""{"n":1e999}"""))], "x", append: true, cancellationToken: ct); }
            catch (InvalidOperationException ex) { caught = ex; }
            await u.AddHashedArtifactAsync(Text("bar"), "x", append: true, lastChunk: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var x = Assert.Single((await rig.RunAsync(rig.NewTask())).Artifacts!);
        Assert.NotNull(caught);
        Assert.Equal(["foo", "bar"], TextsOf(x));
        Assert.Equal(FooBar, x.Metadata!["sha256"].GetString());
    }

    [Fact]
    public async Task A_follow_up_run_continues_the_hash_over_the_stored_parts()
    {
        await using var rig = Rig.For(async (c, u, ct) =>
        {
            if (c.IsNewTask)
            {
                await u.AddHashedArtifactAsync(Text("foo"), "x", cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
                return;
            }
            await u.AddHashedArtifactAsync(Text("bar"), "x", append: true, lastChunk: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var first = await rig.RunAsync(rig.NewTask());
        Assert.Equal(Foo, first.Artifacts![0].Metadata!["sha256"].GetString());
        var second = await rig.RunAsync(rig.FollowUp(first));
        var x = Assert.Single(second.Artifacts!);
        Assert.Equal(["foo", "bar"], TextsOf(x));
        Assert.Equal(FooBar, x.Metadata!["sha256"].GetString());
        Assert.True(IsSealed(x));
        Assert.Equal(ArtifactVerificationStatus.Verified, ArtifactVerifier.Verify(x).Status);
    }

    [Fact]
    public async Task A_follow_up_run_can_hash_an_artifact_that_was_stored_without_a_hash()
    {
        await using var rig = Rig.For(async (c, u, ct) =>
        {
            if (c.IsNewTask)
            {
                await u.AddArtifactAsync(Text("foo"), "x", cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
                return;
            }
            await u.AddHashedArtifactAsync(Text("bar"), "x", append: true, cancellationToken: ct);
            await u.CompleteAsync(cancellationToken: ct);
        });
        var first = await rig.RunAsync(rig.NewTask());
        var x = Assert.Single((await rig.RunAsync(rig.FollowUp(first))).Artifacts!);
        Assert.Equal(FooBar, x.Metadata!["sha256"].GetString());
    }
}
