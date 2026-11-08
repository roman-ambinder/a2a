using System.Text.Json;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// <see cref="A2ALimits.MaxTaskBytes"/> and <see cref="A2ALimits.MaxArtifactsPerTask"/>: an event past a cap fails the task and
/// stops the agent; a follow-up past the byte cap is refused.
/// </summary>
public sealed class TaskSizeCapTests
{
    private const int Cap = 4096;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    /// <summary>Acts on the user's text: ok, big, many, exact, input.</summary>
    private sealed class CapAgent : IAgentExecutor
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            try
            {
                switch (context.UserText)
                {
                    case "ok":
                        await updater.AddArtifactAsync([Part.FromText(new string('a', 100))], cancellationToken: ct);
                        await updater.CompleteAsync(cancellationToken: ct);
                        break;
                    case "big":
                        await updater.AddArtifactAsync([Part.FromText(new string('b', Cap * 2))], artifactId: "huge", cancellationToken: ct);
                        await Task.Delay(Timeout.Infinite, ct);
                        break;
                    case "many":
                        for (var i = 0; i < 3; i++)
                            await updater.AddArtifactAsync([Part.FromText("x")], artifactId: $"a{i}", cancellationToken: ct);
                        await Task.Delay(Timeout.Infinite, ct);
                        break;
                    case "exact":
                        // The user message is 5 bytes, so this fills the task to exactly the cap. One more byte goes past it.
                        await updater.AddArtifactAsync([Part.FromText(new string('c', Cap - 5))], artifactId: "fill", cancellationToken: ct);
                        await updater.AddArtifactAsync([Part.FromText("d")], artifactId: "over", cancellationToken: ct);
                        await Task.Delay(Timeout.Infinite, ct);
                        break;
                    case "quiet-status":
                        await updater.AddArtifactAsync([Part.FromText(new string('q', 3000))], artifactId: "fill", cancellationToken: ct);
                        await Gate.Task.WaitAsync(ct);
                        await updater.UpdateStatusAsync(TaskState.InputRequired, cancellationToken: ct); // no message: adds no content
                        break;
                    case "meta-status":
                        await updater.UpdateStatusAsync(TaskState.Working, metadata: Huge(), cancellationToken: ct); // no message, only metadata
                        await Task.Delay(Timeout.Infinite, ct);
                        break;
                    case "meta-artifact":
                        await updater.AddArtifactAsync(
                            new Artifact { ArtifactId = "meta", Parts = [Part.FromText("x")], Metadata = Huge() }, cancellationToken: ct);
                        await Task.Delay(Timeout.Infinite, ct);
                        break;
                    case "input":
                        await updater.RequireInputAsync("more?", ct);
                        break;
                    default:
                        await updater.CompleteAsync(cancellationToken: ct);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
    }

    private static Dictionary<string, JsonElement> Huge() =>
        new() { ["blob"] = JsonSerializer.SerializeToElement(new string('m', Cap * 2)) };

    private sealed class FixedFactory(IAgentExecutor executor) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentExecutorLease(executor, null));
    }

    private static Task<TestAgentHost> StartAsync(CapAgent agent, Action<A2ALimits>? limits = null) =>
        TestAgentHost.StartAsync(o =>
        {
            o.Limits.MaxTaskBytes = Cap;
            limits?.Invoke(o.Limits);
        }, services: s => s.AddSingleton<IAgentExecutorFactory>(new FixedFactory(agent)));

    private static async Task<TaskRecord> StoredAsync(TestAgentHost host, string taskId) =>
        (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(new TaskKey("", "alice", taskId)))!;

    [Fact]
    public async Task Artifact_within_cap_is_stored()
    {
        await using var host = await StartAsync(new CapAgent());
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("ok")).RequireTask();
        await host.Handler.WhenIdleAsync();

        var stored = (await StoredAsync(host, task.Id)).Task;
        Assert.Equal(TaskState.Completed, stored.Status.State);
        Assert.Single(stored.Artifacts!);
    }

    [Fact]
    public async Task Artifact_past_cap_fails_task()
    {
        var agent = new CapAgent();
        await using var host = await StartAsync(agent);
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("big")).RequireTask();
        await host.Handler.WhenIdleAsync();

        var stored = (await StoredAsync(host, task.Id)).Task;
        Assert.Equal(TaskState.Failed, stored.Status.State);
        Assert.DoesNotContain(stored.Artifacts ?? [], a => a.ArtifactId == "huge");
        await agent.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Too_many_artifacts_fails_task()
    {
        var agent = new CapAgent();
        await using var host = await StartAsync(agent, l => l.MaxArtifactsPerTask = 2);
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("many")).RequireTask();
        await host.Handler.WhenIdleAsync();

        var stored = (await StoredAsync(host, task.Id)).Task;
        Assert.Equal(TaskState.Failed, stored.Status.State);
        Assert.Equal(2, stored.Artifacts!.Count);
        await agent.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Failure_event_is_stored_at_the_cap()
    {
        await using var host = await StartAsync(new CapAgent());
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("exact")).RequireTask();
        await host.Handler.WhenIdleAsync();

        var key = new TaskKey("", "alice", task.Id);
        var stored = (await StoredAsync(host, task.Id)).Task;
        Assert.Equal(TaskState.Failed, stored.Status.State);
        Assert.Equal(["fill"], stored.Artifacts!.Select(a => a.ArtifactId));
        Assert.Equal(Cap - 5, Assert.IsType<TextPart>(stored.Artifacts![0].Parts[0]).Text.Length); // the task was at the cap before the failure

        var events = new List<SequencedEvent>();
        await foreach (var e in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(key, 0)) events.Add(e);
        var last = Assert.IsType<StatusUpdateEvent>(events[^1].Event);
        Assert.Equal(TaskState.Failed, last.StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Follow_up_past_cap_is_refused_and_task_unchanged()
    {
        await using var host = await StartAsync(new CapAgent());
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        await host.Handler.WhenIdleAsync();
        var before = await StoredAsync(host, task.Id);

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            client.SendMessageAsync(Message.FromUser(new string('z', Cap + 1), task.ContextId, task.Id), null));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);

        var after = await StoredAsync(host, task.Id);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Task.History!.Count, after.Task.History!.Count);
        Assert.Equal(TaskState.InputRequired, after.Task.Status.State);
    }

    [Fact]
    public async Task Follow_up_with_small_text_and_huge_metadata_is_refused()
    {
        await using var host = await StartAsync(new CapAgent());
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        await host.Handler.WhenIdleAsync();
        var before = await StoredAsync(host, task.Id);

        var message = Message.FromUser("hi", task.ContextId, task.Id);
        message.Metadata = Huge();
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(message, null));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Equal(before.Version, (await StoredAsync(host, task.Id)).Version);
    }

    [Theory]
    [InlineData("meta-status")]
    [InlineData("meta-artifact")]
    public async Task Agent_event_metadata_past_cap_fails_task(string text)
    {
        var agent = new CapAgent();
        await using var host = await StartAsync(agent);
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(text)).RequireTask();
        await host.Handler.WhenIdleAsync();

        var stored = (await StoredAsync(host, task.Id)).Task;
        Assert.Equal(TaskState.Failed, stored.Status.State);
        Assert.DoesNotContain(stored.Artifacts ?? [], a => a.ArtifactId == "meta");
        await agent.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Measure_counts_metadata_names_and_file_names()
    {
        var blob = JsonSerializer.SerializeToElement("abcd"); // 6 characters of JSON
        var message = Message.FromUser("a"); // 1
        message.Metadata = new() { ["k"] = blob }; // 6
        var part = Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream", "f.bin"); // 4 + 5
        var task = new AgentTask
        {
            Id = "t",
            Status = new AgentTaskStatus(),
            History = [message],
            Metadata = new() { ["k"] = blob }, // 6
            Artifacts =
            [
                new Artifact { ArtifactId = "a", Name = "nm", Description = "desc", Parts = [part], Metadata = new() { ["k"] = blob } }, // 2 + 4 + 6
            ],
        };
        Assert.Equal(1 + 6 + 6 + 2 + 4 + 6 + 4 + 5, TaskSize.Measure(task));
    }

    [Fact]
    public async Task Status_event_without_message_does_not_fail_a_task_over_the_cap()
    {
        var agent = new CapAgent();
        A2ALimits? limits = null;
        await using var host = await StartAsync(agent, l => limits = l);
        var send = host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("quiet-status");
        TaskRecord? record = null;
        for (var i = 0; i < 500 && record is null; i++)
        {
            await Task.Delay(10);
            var ids = await host.App.Services.GetRequiredService<TaskStore>().ListAsync(new TaskQuery("", "alice", null, null, null, 10, null));
            record = ids.Records.FirstOrDefault(r => r.Task.Artifacts?.Count > 0);
        }
        Assert.NotNull(record);
        limits!.MaxTaskBytes = 100; // the stored task is now over the cap
        agent.Gate.SetResult();

        var task = (await send).RequireTask();
        await host.Handler.WhenIdleAsync();
        Assert.Equal(TaskState.InputRequired, (await StoredAsync(host, task.Id)).Task.Status.State);
    }

    [Fact]
    public void Measure_counts_text_raw_url_and_data()
    {
        var task = new AgentTask
        {
            Id = "t",
            Status = new AgentTaskStatus { State = TaskState.Working },
            History = [Message.FromUser("héllo")], // 6 UTF-8 bytes
            Artifacts =
            [
                new Artifact
                {
                    ArtifactId = "a",
                    Parts =
                    [
                        Part.FromBytes(new byte[] { 1, 2, 3 }, "application/octet-stream"), // 4 base64 characters
                        Part.FromUrl(new Uri("https://example.com/f")), // 21
                        Part.FromData(JsonDocument.Parse("""{"a":1}""").RootElement), // 7
                    ],
                },
            ],
        };
        Assert.Equal(6 + 4 + 21 + 7, TaskSize.Measure(task));
        Assert.Equal(0, TaskSize.Measure(new AgentTask { Id = "e", Status = new AgentTaskStatus() }));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Caps_apply_on_every_binding(string binding)
    {
        await using var host = await StartAsync(new CapAgent());
        var client = host.Client(binding);

        var big = (await client.SendMessageAsync("big")).RequireTask();
        await host.Handler.WhenIdleAsync();
        Assert.Equal(TaskState.Failed, (await StoredAsync(host, big.Id)).Task.Status.State);

        var asked = (await client.SendMessageAsync("input")).RequireTask();
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            client.SendMessageAsync(Message.FromUser(new string('z', Cap + 1), asked.ContextId, asked.Id), null));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
    }
}
