using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// <see cref="A2ALimits.MaxTimelineEntries" />: the stored A2A 1.1 timeline keeps only the newest entries, so an agent that
/// sends progress updates cannot grow its task without bound. Generations stay as they were, the history keeps every
/// message, and a resume reads the event log, not the timeline.
/// </summary>
public sealed class TimelineLimitTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// <c>progress N</c>: N status updates without a message, then COMPLETED. <c>progress-input N</c>: the same, then
    /// INPUT_REQUIRED; the follow-up signals <see cref="FollowUpStarted" /> and waits for <see cref="Gate" /> before it
    /// completes.
    /// </summary>
    private sealed class ProgressAgent : IAgentExecutor
    {
        public TaskCompletionSource FollowUpStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            if (context.Task is { Status.State: TaskState.InputRequired })
            {
                FollowUpStarted.TrySetResult();
                await Gate.Task.WaitAsync(ct);
                await updater.CompleteAsync("finished", ct);
                return;
            }

            var words = context.UserText.Split(' ');
            var count = int.Parse(words[1], CultureInfo.InvariantCulture);
            for (var i = 0; i < count; i++)
                await updater.UpdateStatusAsync(TaskState.Working, cancellationToken: ct); // no message: no history, no bytes
            if (words[0] == "progress-input") await updater.RequireInputAsync("more?", ct);
            else await updater.CompleteAsync("done", ct);
        }
    }

    private sealed class FixedFactory(IAgentExecutor executor) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentExecutorLease(executor, null));
    }

    private static Task<TestAgentHost> StartAsync(ProgressAgent agent, int maxTimelineEntries, int? replayBufferSize = null) =>
        TestAgentHost.StartAsync(o =>
        {
            o.EnableProtocolV11();
            o.Limits.MaxTimelineEntries = maxTimelineEntries;
            if (replayBufferSize is { } size) o.Streaming.ReplayBufferSize = size;
        }, services: s => s.AddSingleton<IAgentExecutorFactory>(new FixedFactory(agent)));

    private static async Task<JsonElement> RpcAsync(TestAgentHost host, string method, object @params)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, "1.1");
        using var response = await host.Http("alice").SendAsync(request);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        if (body.TryGetProperty("error", out var error)) throw new Xunit.Sdk.XunitException($"{method} failed: {error}");
        return body.GetProperty("result");
    }

    private static object Send(string text, string? taskId = null) => new
    {
        message = new { messageId = Guid.NewGuid().ToString(), role = "ROLE_USER", parts = new[] { new { text } }, taskId },
    };

    private static async Task<string> CreateAsync(TestAgentHost host, string text)
    {
        var id = (await RpcAsync(host, "SendMessage", Send(text))).GetProperty("task").GetProperty("id").GetString()!;
        await host.WaitIdleAsync();
        return id;
    }

    private static long Gen(JsonElement e) => long.Parse(e.GetProperty("generation").GetString()!, CultureInfo.InvariantCulture);

    private static List<long> Generations(JsonElement task) =>
        task.TryGetProperty("timeline", out var timeline) ? timeline.EnumerateArray().Select(Gen).ToList() : [];

    private static async Task<AgentTask> StoredAsync(TestAgentHost host, string taskId) =>
        (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(new TaskKey("", "alice", taskId)))!.Task;

    private static async Task<List<JsonElement>> SubscribeAsync(TestAgentHost host, string id, long lastSeenGeneration)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "SubscribeToTask",
                @params = new { id, lastSeenGeneration = lastSeenGeneration.ToString(CultureInfo.InvariantCulture) },
            }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, "1.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.EventStreamMediaType));
        using var response = await host.Http("alice").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var frames = new List<JsonElement>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync().WaitAsync(Wait) is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal))
                frames.Add(JsonDocument.Parse(line["data:".Length..]).RootElement.GetProperty("result").Clone());
        return frames;
    }

    [Fact]
    public void Default_keeps_as_many_entries_as_the_event_log_keeps_events()
    {
        Assert.Equal(1000, new A2ALimits().MaxTimelineEntries);
        Assert.Equal(new StreamingOptions().ReplayBufferSize, new A2ALimits().MaxTimelineEntries);
    }

    [Fact]
    public void Appending_past_the_limit_drops_the_oldest_entries()
    {
        var task = new AgentTask { Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working }, Generation = 0 };
        for (var i = 0; i < 10; i++)
            task = TaskExecutionEngine.ApplyToCopy(task, StreamResponse.Of(new TaskStatusUpdateEvent
            {
                TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working, Timestamp = DateTimeOffset.UtcNow },
            }), maxTimelineEntries: 3)!;

        Assert.Equal(10, task.Generation);
        Assert.Equal([8L, 9L, 10L], task.Timeline!.Select(e => e.Generation));
    }

    [Fact]
    public void Zero_turns_the_limit_off()
    {
        var task = new AgentTask { Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working }, Generation = 0 };
        for (var i = 0; i < 5; i++)
            task = TaskExecutionEngine.ApplyToCopy(task, StreamResponse.Of(new TaskStatusUpdateEvent
            {
                TaskId = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working, Timestamp = DateTimeOffset.UtcNow },
            }), maxTimelineEntries: 0)!;

        Assert.Equal(5, task.Timeline!.Count);
    }

    [Fact]
    public async Task Progress_updates_keep_only_the_newest_entries_and_every_message_stays_in_the_history()
    {
        await using var host = await StartAsync(new ProgressAgent(), maxTimelineEntries: 5);
        var id = await CreateAsync(host, "progress 20");

        // message (1), 20 WORKING (2..21), COMPLETED (22): the newest 5 are kept.
        var stored = await StoredAsync(host, id);
        Assert.Equal(22, stored.Generation);
        Assert.Equal([18L, 19L, 20L, 21L, 22L], stored.Timeline!.Select(e => e.Generation));
        Assert.Equal(TaskState.Completed, stored.Timeline![^1].Status!.State);
        // The client message's entry was dropped from the timeline; the message itself is still in the history.
        Assert.Equal(["progress 20", "done"], stored.History!.Select(m => m.GetText()));

        var task = await RpcAsync(host, "GetTask", new { id });
        Assert.Equal(22, Gen(task));
        Assert.Equal([18L, 19L, 20L, 21L, 22L], Generations(task));
    }

    [Fact]
    public async Task Timeline_length_larger_than_what_is_kept_returns_what_is_kept()
    {
        await using var host = await StartAsync(new ProgressAgent(), maxTimelineEntries: 5);
        var id = await CreateAsync(host, "progress 20");

        Assert.Equal([18L, 19L, 20L, 21L, 22L], Generations(await RpcAsync(host, "GetTask", new { id, timelineLength = 50 })));
        Assert.Equal([21L, 22L], Generations(await RpcAsync(host, "GetTask", new { id, timelineLength = 2 })));
        // The long poll answers from the same stored task.
        var polled = await RpcAsync(host, "GetTask", new { id, currentGeneration = "1", timelineLength = 50 }).WaitAsync(Wait);
        Assert.Equal([18L, 19L, 20L, 21L, 22L], Generations(polled));
    }

    [Fact]
    public async Task A_follow_up_message_trims_the_timeline_too()
    {
        var agent = new ProgressAgent();
        await using var host = await StartAsync(agent, maxTimelineEntries: 2);
        var id = await CreateAsync(host, "progress-input 3"); // message (1), WORKING (2..4), INPUT_REQUIRED (5)
        Assert.Equal([4L, 5L], (await StoredAsync(host, id)).Timeline!.Select(e => e.Generation));

        var followUp = RpcAsync(host, "SendMessage", Send("more", id));
        await agent.FollowUpStarted.Task.WaitAsync(Wait);
        // The follow-up message (6) was committed by the request handler, before the agent sent anything.
        var stored = await StoredAsync(host, id);
        Assert.Equal(6, stored.Generation);
        Assert.Equal([5L, 6L], stored.Timeline!.Select(e => e.Generation));
        Assert.Equal("more", stored.Timeline![^1].Message!.GetText());

        agent.Gate.TrySetResult();
        await followUp.WaitAsync(Wait);
        await host.WaitIdleAsync();
        Assert.Equal([6L, 7L], (await StoredAsync(host, id)).Timeline!.Select(e => e.Generation));
    }

    [Fact]
    public async Task Resume_from_a_generation_the_timeline_dropped_replays_the_retained_events()
    {
        await using var host = await StartAsync(new ProgressAgent(), maxTimelineEntries: 5);
        var id = await CreateAsync(host, "progress 20");

        // Generation 3 is older than the oldest kept timeline entry (18), but its events are still in the event log.
        var frames = await SubscribeAsync(host, id, 3);
        Assert.False(frames[0].TryGetProperty("task", out _));
        Assert.Equal(Enumerable.Range(4, 19).Select(g => (long)g), frames.Select(f => Gen(f.GetProperty("statusUpdate"))));
    }

    [Fact]
    public async Task Resume_from_a_generation_whose_events_are_gone_starts_with_a_snapshot_of_the_kept_timeline()
    {
        await using var host = await StartAsync(new ProgressAgent(), maxTimelineEntries: 5, replayBufferSize: 5);
        var id = await CreateAsync(host, "progress 20");

        var frames = await SubscribeAsync(host, id, 3);
        var snapshot = frames[0].GetProperty("task");
        Assert.Equal(22, Gen(snapshot));
        Assert.Equal([18L, 19L, 20L, 21L, 22L], Generations(snapshot));
    }
}
