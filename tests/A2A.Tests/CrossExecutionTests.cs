using System.Collections.Concurrent;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// Agents that react to cancellation in awkward ways. The first word picks the behaviour; the whole text keys the signals.
/// <list type="bullet">
/// <item><c>stubborn</c>: starts work, then waits for its gate and ignores cancellation.</item>
/// <item><c>late-stop</c>: starts work and stops on cancellation, but only after its gate opens, so its own CANCELED comes
/// after the one from the CancelTask execution and is dropped.</item>
/// </list>
/// </summary>
public sealed class CancelRaceAgent : IAgentExecutor
{
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Gates = new();
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<string>> Started = new();

    public static TaskCompletionSource Gate(string text) =>
        Gates.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    /// <summary>Completes with the task id once the agent for <paramref name="text"/> is working.</summary>
    public static TaskCompletionSource<string> StartedSignal(string text) =>
        Started.GetOrAdd(text, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        var text = context.UserText;
        await updater.StartWorkAsync("working", ct);
        StartedSignal(text).TrySetResult(updater.TaskId);
        switch (text.Split(' ')[0])
        {
            case "stubborn":
                await Gate(text).Task; // ignores ct
                await updater.CompleteAsync("finished anyway", CancellationToken.None);
                break;
            case "late-stop":
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    await Gate(text).Task;
                    throw;
                }
                break;
        }
    }
}

/// <summary>
/// A send (streaming or blocking) also sees terminal events that other executions of its task produce, such as the
/// CancelTask execution, and ends on them.
/// </summary>
public class CrossExecutionTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The host's 2 s cancel grace: these agents never finish in time, so CancelTask always waits it out, and a shorter grace
    /// can end before the CancelTask execution stored CANCELED when the machine is loaded.
    /// </summary>
    private static Task<TestAgentHost> StartAsync() => TestAgentHost.StartAsync(
        services: s => s.AddScoped<IAgentExecutor, CancelRaceAgent>());

    /// <summary>A bare transport: no client-side resume, so the server stream is seen exactly as sent.</summary>
    private static IA2ATransport Transport(TestAgentHost host, string binding)
    {
        var http = host.Http("alice");
        return binding == ProtocolBindings.JsonRpc
            ? new JsonRpcTransport(http, new Uri(TestAgentHost.BaseUrl))
            : new HttpJsonTransport(http, new Uri(TestAgentHost.BaseUrl));
    }

    private static TaskState? StateOf(StreamResponse e) => e switch
    {
        StatusUpdateEvent s => s.StatusUpdate.Status.State,
        TaskEvent t => t.Task.Status.State,
        _ => null,
    };

    /// <summary>Reads the stream to its end, with a time limit.</summary>
    private static async Task<List<StreamResponse>> ReadToEndAsync(IAsyncEnumerator<StreamResponse> stream, List<StreamResponse> seen)
    {
        while (await stream.MoveNextAsync().AsTask().WaitAsync(Wait)) seen.Add(stream.Current);
        return seen;
    }

    /// <summary>The agent signals before its first event is stored: wait until the task is visible in that state.</summary>
    private static async Task WaitForStateAsync(IA2ATransport client, string taskId, TaskState state)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            try
            {
                if ((await client.GetTaskAsync(new GetTaskRequest { Id = taskId })).Status.State == state) return;
            }
            catch (A2AException e) when (e.Code == A2AErrorCode.TaskNotFound) { }
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Task {taskId} never reached {state}.");
            await Task.Delay(10);
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Cancel_ends_a_send_stream_whose_agent_ignores_cancellation(string binding)
    {
        await using var host = await StartAsync();
        var text = $"stubborn {Guid.NewGuid():N}";
        var transport = Transport(host, binding);
        try
        {
            await using var stream = transport.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser(text) }).GetAsyncEnumerator();
            var seen = new List<StreamResponse>();
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Wait)); // sends the request
            seen.Add(stream.Current);
            var taskId = await CancelRaceAgent.StartedSignal(text).Task.WaitAsync(Wait);

            var canceled = await transport.CancelTaskAsync(new CancelTaskRequest { Id = taskId });
            Assert.Equal(TaskState.Canceled, canceled.Status.State);

            await ReadToEndAsync(stream, seen);
            Assert.Equal(TaskState.Canceled, StateOf(seen[^1]));
        }
        finally
        {
            CancelRaceAgent.Gate(text).TrySetResult();
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Cancel_ends_a_send_stream_whose_agent_stops_after_the_cancel_execution(string binding)
    {
        await using var host = await StartAsync();
        var text = $"late-stop {Guid.NewGuid():N}";
        var transport = Transport(host, binding);
        try
        {
            await using var stream = transport.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser(text) }).GetAsyncEnumerator();
            var seen = new List<StreamResponse>();
            Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Wait)); // sends the request
            seen.Add(stream.Current);
            var taskId = await CancelRaceAgent.StartedSignal(text).Task.WaitAsync(Wait);

            var canceled = await transport.CancelTaskAsync(new CancelTaskRequest { Id = taskId });
            Assert.Equal(TaskState.Canceled, canceled.Status.State);
            CancelRaceAgent.Gate(text).TrySetResult(); // the agent now stops; its own CANCELED is dropped

            await ReadToEndAsync(stream, seen);
            Assert.Equal(TaskState.Canceled, StateOf(seen[^1]));
            Assert.Single(seen, e => StateOf(e) == TaskState.Canceled);
        }
        finally
        {
            CancelRaceAgent.Gate(text).TrySetResult();
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Cancel_completes_a_blocking_send_whose_agent_ignores_cancellation(string binding)
    {
        await using var host = await StartAsync();
        var text = $"stubborn {Guid.NewGuid():N}";
        var transport = Transport(host, binding);
        try
        {
            var send = transport.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text) });
            var taskId = await CancelRaceAgent.StartedSignal(text).Task.WaitAsync(Wait);
            await WaitForStateAsync(transport, taskId, TaskState.Working);

            await transport.CancelTaskAsync(new CancelTaskRequest { Id = taskId });

            var task = (await send.WaitAsync(Wait)).RequireTask();
            Assert.Equal(taskId, task.Id);
            Assert.Equal(TaskState.Canceled, task.Status.State);
        }
        finally
        {
            CancelRaceAgent.Gate(text).TrySetResult();
        }
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Cancel_completes_a_blocking_send_whose_agent_stops_after_the_cancel_execution(string binding)
    {
        await using var host = await StartAsync();
        var text = $"late-stop {Guid.NewGuid():N}";
        var transport = Transport(host, binding);
        try
        {
            var send = transport.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser(text) });
            var taskId = await CancelRaceAgent.StartedSignal(text).Task.WaitAsync(Wait);
            await WaitForStateAsync(transport, taskId, TaskState.Working);

            await transport.CancelTaskAsync(new CancelTaskRequest { Id = taskId });

            // The agent has not stopped yet (its gate is closed), yet the send already has its outcome.
            var task = (await send.WaitAsync(Wait)).RequireTask();
            Assert.Equal(TaskState.Canceled, task.Status.State);
        }
        finally
        {
            CancelRaceAgent.Gate(text).TrySetResult();
        }
    }
}
