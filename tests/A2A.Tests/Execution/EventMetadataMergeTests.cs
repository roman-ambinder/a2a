using System.Text.Json;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Idempotency;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.Server.Streaming;

namespace A2A.Tests.Execution;

/// <summary>S28 (event metadata merges into the task) and the history stamp of agent messages (S29).</summary>
public sealed class EventMetadataMergeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static JsonElement Str(string s) => JsonSerializer.SerializeToElement(s);
    private static JsonElement Num(int n) => JsonSerializer.SerializeToElement(n);
    private static JsonElement Null() => JsonSerializer.SerializeToElement<string?>(null);

    private static AgentTask Task(Dictionary<string, JsonElement>? metadata = null, TaskState state = TaskState.Working) => new()
    {
        Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = state }, Metadata = metadata,
    };

    private static StreamResponse Status(TaskState state, Dictionary<string, JsonElement>? metadata, Message? message = null) =>
        StreamResponse.Of(new TaskStatusUpdateEvent
        {
            TaskId = "t", ContextId = "c", Metadata = metadata,
            Status = new AgentTaskStatus { State = state, Message = message, Timestamp = DateTimeOffset.UtcNow },
        });

    private static StreamResponse ArtifactEvent(Artifact artifact, bool append, Dictionary<string, JsonElement>? metadata) =>
        StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "t", ContextId = "c", Artifact = artifact, Append = append, Metadata = metadata });

    [Fact]
    public void Status_event_metadata_adds_and_overwrites_and_absent_keys_stay()
    {
        var current = Task(new() { ["a"] = Str("1"), ["b"] = Str("2") });
        var next = TaskExecutionEngine.ApplyToCopy(current, Status(TaskState.Working, new() { ["b"] = Str("new"), ["c"] = Num(3) }))!;

        Assert.Equal("1", next.Metadata!["a"].GetString());   // absent from the update: stays
        Assert.Equal("new", next.Metadata["b"].GetString());  // last write wins
        Assert.Equal(3, next.Metadata["c"].GetInt32());
        Assert.Equal("2", current.Metadata!["b"].GetString()); // the stored task is not changed
        Assert.DoesNotContain("c", current.Metadata.Keys);
    }

    [Fact]
    public void Merge_is_shallow_the_value_replaces_a_nested_object()
    {
        var current = Task(new() { ["o"] = JsonSerializer.SerializeToElement(new { x = 1, y = 2 }) });
        var next = TaskExecutionEngine.ApplyToCopy(current, Status(TaskState.Working, new() { ["o"] = JsonSerializer.SerializeToElement(new { z = 3 }) }))!;
        Assert.Equal("""{"z":3}""", next.Metadata!["o"].GetRawText());
    }

    [Fact]
    public void Null_is_stored_as_a_value()
    {
        var current = Task(new() { ["a"] = Str("1") });
        var next = TaskExecutionEngine.ApplyToCopy(current, Status(TaskState.Working, new() { ["a"] = Null() }))!;
        Assert.True(next.Metadata!.TryGetValue("a", out var v));
        Assert.Equal(JsonValueKind.Null, v.ValueKind);
    }

    [Fact]
    public void No_event_metadata_creates_no_dictionary()
    {
        var next = TaskExecutionEngine.ApplyToCopy(Task(), Status(TaskState.Working, null))!;
        Assert.Null(next.Metadata);
        next = TaskExecutionEngine.ApplyToCopy(Task(), Status(TaskState.Working, []))!;
        Assert.Null(next.Metadata);
    }

    [Fact]
    public void Server_owned_keys_are_skipped()
    {
        var current = Task(new() { [A2AProtocol.ApprovalConsumedKey] = Str("m1") });
        var update = new Dictionary<string, JsonElement>
        {
            [A2AProtocol.ApprovalConsumedKey] = Str("reset"),
            [A2AProtocol.CancelRequestedKey] = Str("true"),
            [A2A.Delegation.ActorChain.MetadataKey] = Str("forged"),
            [A2A.Elicitations.Elicitation.AnsweredKey] = Str("reset"),
            ["ok"] = Str("yes"),
        };
        var next = TaskExecutionEngine.ApplyToCopy(current, Status(TaskState.Working, update))!;

        Assert.Equal("m1", next.Metadata![A2AProtocol.ApprovalConsumedKey].GetString());
        Assert.DoesNotContain(A2AProtocol.CancelRequestedKey, next.Metadata.Keys);
        Assert.DoesNotContain(A2A.Delegation.ActorChain.MetadataKey, next.Metadata.Keys);
        Assert.DoesNotContain(A2A.Elicitations.Elicitation.AnsweredKey, next.Metadata.Keys);
        Assert.Equal("yes", next.Metadata["ok"].GetString());
    }

    [Fact]
    public void Only_server_owned_keys_leave_the_metadata_null()
    {
        var next = TaskExecutionEngine.ApplyToCopy(Task(), Status(TaskState.Working, new() { [A2AProtocol.ApprovalConsumedKey] = Str("x") }))!;
        Assert.Null(next.Metadata);
    }

    [Fact]
    public void A_dropped_event_merges_nothing()
    {
        // Terminal task.
        var terminal = Task(new() { ["a"] = Str("1") }, TaskState.Completed);
        Assert.Null(TaskExecutionEngine.ApplyToCopy(terminal, Status(TaskState.Working, new() { ["a"] = Str("2") })));
        Assert.Equal("1", terminal.Metadata!["a"].GetString());
        // Artifact event on a terminal task.
        var artifact = new Artifact { ArtifactId = "x", Parts = [Part.FromText("p")] };
        Assert.Null(TaskExecutionEngine.ApplyToCopy(terminal, ArtifactEvent(artifact, false, new() { ["a"] = Str("2") })));
        Assert.Equal("1", terminal.Metadata["a"].GetString());
    }

    [Fact]
    public void Artifact_event_metadata_merges_into_the_task()
    {
        var current = Task(new() { ["a"] = Str("1") });
        var artifact = new Artifact { ArtifactId = "x", Parts = [Part.FromText("p")] };
        var next = TaskExecutionEngine.ApplyToCopy(current, ArtifactEvent(artifact, false, new() { ["a"] = Str("2"), ["n"] = Num(1) }))!;
        Assert.Equal("2", next.Metadata!["a"].GetString());
        Assert.Equal(1, next.Metadata["n"].GetInt32());
        Assert.Null(next.Artifacts![0].Metadata); // the event's metadata is not the artifact's
    }

    [Fact]
    public void Artifact_metadata_rule_is_unchanged_append_merges_and_replace_replaces()
    {
        var first = new Artifact { ArtifactId = "x", Parts = [Part.FromText("a")], Metadata = new() { ["k1"] = Str("1") } };
        var stored = TaskExecutionEngine.ApplyToCopy(Task(), ArtifactEvent(first, false, null))!;

        var more = new Artifact { ArtifactId = "x", Parts = [Part.FromText("b")], Metadata = new() { ["k2"] = Str("2") } };
        var appended = TaskExecutionEngine.ApplyToCopy(stored, ArtifactEvent(more, true, null))!;
        var meta = appended.Artifacts![0].Metadata!;
        Assert.Equal("1", meta["k1"].GetString());
        Assert.Equal("2", meta["k2"].GetString());
        Assert.Null(appended.Metadata); // no event metadata: the task has none

        var replacement = new Artifact { ArtifactId = "x", Parts = [Part.FromText("c")], Metadata = new() { ["k3"] = Str("3") } };
        var replaced = TaskExecutionEngine.ApplyToCopy(stored, ArtifactEvent(replacement, false, null))!;
        var rmeta = replaced.Artifacts![0].Metadata!;
        Assert.DoesNotContain("k1", rmeta.Keys);
        Assert.Equal("3", rmeta["k3"].GetString());
    }

    [Fact]
    public void Merged_values_are_clones_that_outlive_the_agents_document()
    {
        var doc = JsonDocument.Parse("""{"v":"alive"}""");
        var update = new Dictionary<string, JsonElement> { ["d"] = doc.RootElement };
        var next = TaskExecutionEngine.ApplyToCopy(Task(), Status(TaskState.Working, update))!;
        doc.Dispose();
        Assert.Equal("""{"v":"alive"}""", next.Metadata!["d"].GetRawText());
    }

    private sealed class DelegateFactory(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken ct) =>
            ValueTask.FromResult(new AgentExecutorLease(new DelegateAgentExecutor(execute), scope: null));
    }

    private static TaskExecutionEngine Engine(TaskStore tasks, EventStore events, Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute)
    {
        var options = new A2AServerOptions();
        options.AgentCard.Name = "Engine Test Agent";
        return new TaskExecutionEngine(tasks, events, new DelegateFactory(execute), new TaskEventBus(), new InMemoryPushOutbox(),
            new IdempotencyCoordinator(new InMemoryIdempotencyStore(options.Idempotency), tasks, options),
            new PushConfigService(new InMemoryPushConfigStore(), new EphemeralPushSecretProtector(), options), options, new AgentCardProvider(options, []));
    }

    [Fact]
    public async Task Engine_stamps_agent_messages_with_the_status_time_merges_metadata_and_leaves_the_agents_objects_alone()
    {
        var events = new InMemoryEventStore(new A2AServerOptions().Streaming);
        var tasks = new InMemoryTaskStore(events, new InMemoryPushOutbox());
        Message? agentMessage = null;
        Dictionary<string, JsonElement>? agentMetadata = null;
        await using var engine = Engine(tasks, events, async (_, updater, ct) =>
        {
            agentMetadata = new() { ["mine"] = Str("v") };
            agentMessage = updater.NewAgentMessage("working");
            agentMessage.Metadata = agentMetadata;
            await updater.UpdateStatusAsync(TaskState.Working, agentMessage, new() { ["progress"] = Num(50), [A2AProtocol.ApprovalConsumedKey] = Str("forged") }, ct);
            await updater.UpdateStatusAsync(TaskState.Working, null, new() { ["progress"] = Num(100) }, ct);
            await updater.CompleteAsync("done", ct);
        });
        var context = new AgentRequestContext { Message = Message.FromUser("go"), TaskId = TaskIds.New(), ContextId = TaskIds.New(), Call = ServerCallContext.Anonymous() };
        var exec = await engine.StartForTestAsync(context);
        await exec.Completion.WaitAsync(Wait);
        await engine.WhenIdleAsync().WaitAsync(Wait);

        var key = new TaskKey(context.Call.Tenant, context.Call.Principal, context.TaskId);
        var task = (await tasks.GetAsync(key))!.Task;
        Assert.Equal(100, task.Metadata!["progress"].GetInt32());  // last write wins
        Assert.DoesNotContain(A2AProtocol.ApprovalConsumedKey, task.Metadata.Keys);

        // The agent's objects were not changed.
        Assert.Same(agentMetadata, agentMessage!.Metadata);
        Assert.Equal(["mine"], agentMetadata!.Keys);

        // The history entry, the status event and its message agree.
        var stored = new List<SequencedEvent>();
        await foreach (var e in events.ReadAfterAsync(key, 0)) stored.Add(e);
        var working = stored.Select(e => e.Event).OfType<StatusUpdateEvent>().First(e => e.StatusUpdate.Status.Message is not null).StatusUpdate;
        var inHistory = task.History!.Single(m => m.MessageId == agentMessage.MessageId);
        var at = A2A.Json.TimestampConverter.FormatUtc(working.Status.Timestamp!.Value);
        Assert.Equal(at, A2A.Json.TimestampConverter.FormatUtc(inHistory.GetRecordedAt()!.Value));
        Assert.Equal(at, A2A.Json.TimestampConverter.FormatUtc(working.Status.Message!.GetRecordedAt()!.Value));
        Assert.Equal("v", inHistory.Metadata!["mine"].GetString());
        Assert.Equal(2, inHistory.Metadata.Count);
        // Every event kept its own metadata in the log.
        Assert.Equal(50, working.Metadata!["progress"].GetInt32());
    }
}
