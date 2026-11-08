using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using A2A.Json;
using A2A.Server;
using A2A.Server.Storage;
using A2A.Tests.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// A new task gets 50 events (new artifacts, appends with metadata, status messages) and then asks for input. The follow-up
/// changes everything the executor can reach (its view of the task and the message) and then waits until it is cancelled.
/// CancelAsync changes its view of the task too.
/// </summary>
public sealed class OwnershipAgent : IAgentExecutor
{
    public const int Events = 50;

    private static readonly ConcurrentDictionary<string, TaskCompletionSource> FollowUpRunning = new();

    /// <summary>Completes once the follow-up of <paramref name="taskId"/> has changed its view and emitted its events.</summary>
    public static TaskCompletionSource FollowUp(string taskId) =>
        FollowUpRunning.GetOrAdd(taskId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        if (context.Task is null)
        {
            await updater.StartWorkAsync("start", ct);
            for (var i = 0; i < Events; i++)
            {
                switch (i % 3)
                {
                    case 0:
                        await updater.AddArtifactAsync([Part.FromText($"p{i}")], artifactId: $"a{i}", name: $"n{i}", cancellationToken: ct);
                        break;
                    case 1:
                        await updater.AddArtifactAsync([Part.FromText($"p{i}")], artifactId: $"a{i - 1}", description: $"d{i}", append: true,
                            metadata: new() { ["i"] = J(i.ToString(System.Globalization.CultureInfo.InvariantCulture)) }, cancellationToken: ct);
                        break;
                    default:
                        await updater.StartWorkAsync($"step {i}", ct);
                        break;
                }
            }
            await updater.RequireInputAsync("more?", ct);
            return;
        }

        // The executor's own view: changing it must not change the stored task.
        context.Task.Status.State = TaskState.Failed;
        context.Task.Status.Message = null;
        context.Task.History?.Clear();
        context.Task.Artifacts?.Clear();
        context.Message.Parts.Clear();
        await updater.StartWorkAsync("again", ct);
        await updater.AddArtifactAsync([Part.FromText("tail")], artifactId: "a0", append: true, cancellationToken: ct);
        FollowUp(context.TaskId).TrySetResult();
        await Task.Delay(Timeout.Infinite, ct);
    }

    public async Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken ct)
    {
        task.Status.State = TaskState.Completed;
        task.History?.Clear();
        task.Artifacts?.Clear();
        await updater.CancelAsync("stopped", ct);
    }
}

public class TaskStoreOwnershipTests
{
    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    private static Task<TestAgentHost> StartAsync(OwnershipCheckingTaskStore store) =>
        TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton<TaskStore>(store).AddSingleton<EventStore>(store.Events).AddSingleton<A2A.Server.Push.PushOutbox>(store.Outbox);
            s.AddScoped<IAgentExecutor, OwnershipAgent>();
        });

    /// <summary>
    /// Runs the first message (50 events, then input-required) and the follow-up; returns the task id once the follow-up's
    /// events are applied (the signal only says they were emitted).
    /// </summary>
    private static async Task<string> StartFollowUpAsync(TestAgentHost host)
    {
        var first = (await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call())).RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);
        await host.Handler.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("next", taskId: first.Id),
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }, Call());
        await OwnershipAgent.FollowUp(first.Id).Task.WaitAsync(TimeSpan.FromSeconds(10));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var task = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = first.Id }, Call());
            if (task.Artifacts!.Single(a => a.ArtifactId == "a0").Parts.Count == 3) return first.Id;
            Assert.True(DateTime.UtcNow < deadline, "the follow-up's events were not applied");
            await Task.Delay(10);
        }
    }

    private static void AssertArtifacts(AgentTask task, int a0Parts)
    {
        Assert.Equal((OwnershipAgent.Events + 2) / 3, task.Artifacts!.Count);
        var a0 = task.Artifacts.Single(a => a.ArtifactId == "a0");
        Assert.Equal(a0Parts, a0.Parts.Count);
        Assert.Equal("n0", a0.Name);
        Assert.Equal("d1", a0.Description);
        Assert.Equal(1, a0.Metadata!["i"].GetInt32());
    }

    private static void AssertUserMessages(AgentTask task)
    {
        Assert.Contains(task.History!, m => m.Role == Role.User && m.GetText() == "go");
        Assert.Contains(task.History!, m => m.Role == Role.User && m.GetText() == "next");
    }

    [Fact]
    public async Task Handler_never_mutates_returned_record()
    {
        var store = new OwnershipCheckingTaskStore();
        await using var host = await StartAsync(store);
        var id = await StartFollowUpAsync(host);

        var canceled = await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = id }, Call());
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        await host.WaitIdleAsync();

        var got = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = id }, Call());
        var listed = await host.Handler.ListTasksAsync(new ListTasksRequest { IncludeArtifacts = true }, Call());
        store.Verify();
        Assert.Empty(store.Violations);

        Assert.Equal(TaskState.Canceled, got.Status.State);
        AssertArtifacts(got, a0Parts: 3);
        AssertUserMessages(got);
        Assert.Equal(A2AJson.Serialize(got), A2AJson.Serialize(Assert.Single(listed.Tasks)));
    }

    [Fact]
    public async Task Second_get_after_update_sees_new_state()
    {
        var store = new OwnershipCheckingTaskStore();
        var created = await store.CreateAsync(TaskStoreTests.NewRecord("t", "a", "id1"));
        var first = (await store.GetAsync(created.Key))!;

        var next = first.Task.CopyForUpdate();
        next.Status.State = TaskState.Working;
        await store.UpdateAsync(first with { Task = next }, first.Version);

        var second = (await store.GetAsync(created.Key))!;
        Assert.Equal(TaskState.Working, second.Task.Status.State);
        Assert.Equal(2, second.Version);
        Assert.Equal(TaskState.Submitted, first.Task.Status.State);
        store.Verify();
        Assert.Empty(store.Violations);
    }

    [Fact]
    public async Task Executor_view_is_detached()
    {
        var store = new OwnershipCheckingTaskStore();
        await using var host = await StartAsync(store);
        var id = await StartFollowUpAsync(host);

        // The follow-up executor emptied its view and set it to FAILED; the stored task has its own state.
        var stored = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = id }, Call());
        Assert.Equal(TaskState.Working, stored.Status.State);
        Assert.Equal("again", stored.Status.Message!.GetText());
        AssertArtifacts(stored, a0Parts: 3);
        AssertUserMessages(stored);

        await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = id }, Call());
        await host.WaitIdleAsync();
        var canceled = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = id }, Call());
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        AssertArtifacts(canceled, a0Parts: 3);
        AssertUserMessages(canceled);
        store.Verify();
        Assert.Empty(store.Violations);
    }

    /// <summary>
    /// The stores keep the emitted instances, so <see cref="TaskUpdater"/> copies the metadata dictionaries it is given: an
    /// executor that reuses one dictionary across calls does not rewrite what was already stored.
    /// </summary>
    [Fact]
    public async Task Reused_metadata_dictionary_does_not_change_stored_artifacts()
    {
        static JsonElement J(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }

        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<IAgentExecutor>(new DelegateAgentExecutor(async (_, updater, ct) =>
        {
            var metadata = new Dictionary<string, JsonElement> { ["v"] = J("1") };
            await updater.AddArtifactAsync([Part.FromText("one")], artifactId: "a1", metadata: metadata, cancellationToken: ct);
            metadata["v"] = J("2");
            await updater.AddArtifactAsync([Part.FromText("two")], artifactId: "a2", metadata: metadata, cancellationToken: ct);
            metadata["v"] = J("3");
            await updater.CompleteAsync(cancellationToken: ct);
        })));

        var task = (await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call())).RequireTask();
        var stored = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = task.Id }, Call());
        Assert.Equal(TaskState.Completed, stored.Status.State);
        Assert.Equal(2, stored.Artifacts!.Count);
        Assert.Equal(1, stored.Artifacts[0].Metadata!["v"].GetInt32());
        Assert.Equal(2, stored.Artifacts[1].Metadata!["v"].GetInt32());
    }

    [Fact]
    public void A2AJson_Clone_is_not_public()
    {
        Assert.Null(typeof(A2AJson).GetMethod("Clone", BindingFlags.Public | BindingFlags.Static));
    }
}
