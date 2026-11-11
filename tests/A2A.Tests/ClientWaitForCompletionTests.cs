using System.Reflection;
using A2A.Client;

namespace A2A.Tests;

/// <summary>
/// <see cref="A2AClientOptions.WaitForTaskCompletion"/> (S24): a send that comes back with a task that is still running
/// (some servers and proxies answer a blocking send before the task ends) is polled until the task is finished or needs
/// input.
/// </summary>
public class ClientWaitForCompletionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static AgentTask Task(TaskState state, int historyCount = 0) => new()
    {
        Id = "t1", ContextId = "c1", Status = new AgentTaskStatus { State = state },
        History = historyCount == 0 ? null : Enumerable.Range(0, historyCount).Select(i => Message.FromUser($"m{i}")).ToList(),
    };

    /// <summary>
    /// A transport for an agent that answers the send with a WORKING task, and GetTask with WORKING for the first
    /// <see cref="WorkingPolls"/> calls, then COMPLETED. It records the history length of every GetTask.
    /// </summary>
    public class ScriptedAgent : DispatchProxy
    {
        public int WorkingPolls { get; set; } = 2;
        public List<int?> GetTaskHistoryLengths { get; } = [];

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case nameof(IA2AClient.SendMessageAsync):
                    return System.Threading.Tasks.Task.FromResult<SendMessageResponse>(new TaskResponse(Task(TaskState.Working)));
                case nameof(IA2AClient.GetTaskAsync):
                    var history = ((GetTaskRequest)args![0]!).HistoryLength;
                    GetTaskHistoryLengths.Add(history);
                    var state = GetTaskHistoryLengths.Count <= WorkingPolls ? TaskState.Working : TaskState.Completed;
                    return System.Threading.Tasks.Task.FromResult(Task(state, history ?? 3));
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }

    private static (A2AClient Client, ScriptedAgent Agent) Scripted(bool wait)
    {
        var transport = DispatchProxy.Create<IA2AClient, ScriptedAgent>();
        return (new A2AClient(transport, new A2AClientOptions { WaitForTaskCompletion = wait }), (ScriptedAgent)(object)transport);
    }

    [Fact]
    public async Task A_running_task_is_polled_until_it_completes_and_returned_with_the_requested_history()
    {
        var (client, agent) = Scripted(wait: true);

        var task = (await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("work"), Configuration = new SendMessageConfiguration { HistoryLength = 5 },
        }).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        // Polls read no history; the settled task is read once more with the history the send asked for.
        Assert.Equal([0, 0, 0, 5], agent.GetTaskHistoryLengths);
        Assert.Equal(5, task.History!.Count);
    }

    [Fact]
    public async Task A_send_that_asks_for_no_history_is_not_read_again()
    {
        var (client, agent) = Scripted(wait: true);
        agent.WorkingPolls = 0;

        var task = (await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("work"), Configuration = new SendMessageConfiguration { HistoryLength = 0 },
        }).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Equal([0], agent.GetTaskHistoryLengths);
    }

    [Fact]
    public async Task Without_the_option_the_running_task_is_returned_as_before()
    {
        var (client, agent) = Scripted(wait: false);

        var task = (await client.SendMessageAsync("work").WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Working, task.Status.State);
        Assert.Empty(agent.GetTaskHistoryLengths);
    }

    [Fact]
    public async Task A_request_that_asks_to_return_immediately_is_not_polled()
    {
        var (client, agent) = Scripted(wait: true);

        var task = (await client.SendMessageAsync(new SendMessageRequest
        {
            Message = Message.FromUser("work"), Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        }).WaitAsync(Wait)).RequireTask();

        Assert.Equal(TaskState.Working, task.Status.State);
        Assert.Empty(agent.GetTaskHistoryLengths);
    }

    [Fact]
    public async Task A_task_that_needs_input_is_returned_without_waiting()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { WaitForTaskCompletion = true });

        var task = (await client.SendMessageAsync("input").WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.InputRequired, task.Status.State);
    }

    [Fact]
    public async Task Against_this_server_a_blocking_send_already_returns_the_finished_task()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { WaitForTaskCompletion = true });

        var task = (await client.SendMessageAsync("task").WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
    }
}
