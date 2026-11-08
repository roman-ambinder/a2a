using System.Security.Claims;
using A2A.Server;
using A2A.Server.Execution;
using A2A.Tests.Parity;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// A follow-up's message is committed before its execution starts. When the start fails after that, the task is failed with
/// a system status instead of staying INPUT_REQUIRED with a message no agent will answer.
/// </summary>
public sealed class FollowUpStartFailureTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static ServerCallContext Call() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
    };

    /// <summary>The system clock, whose timers fail while <see cref="Fail"/> is set: the execution's heartbeat timer cannot start.</summary>
    private sealed class FailingTimers : TimeProvider
    {
        public volatile bool Fail;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            Fail ? throw new InvalidOperationException("timer unavailable") : System.CreateTimer(callback, state, dueTime, period);
    }

    [Fact]
    public async Task Follow_up_whose_execution_cannot_start_fails_the_task()
    {
        var clock = new FailingTimers();
        var followUpsRun = 0;
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddSingleton<IAgentExecutor>(new DelegateAgentExecutor(async (context, u, ct) =>
            {
                if (!context.IsNewTask) Interlocked.Increment(ref followUpsRun);
                await u.RequireInputAsync("more?", ct);
            }));
        });

        var first = (await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }, Call()).WaitAsync(Wait))
            .RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);

        var followUp = Message.FromUser("more", taskId: first.Id);
        clock.Fail = true;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => host.Handler.SendMessageAsync(new SendMessageRequest { Message = followUp }, Call()).WaitAsync(Wait));
        }
        finally
        {
            clock.Fail = false;
        }

        var task = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = first.Id }, Call());
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal(TaskExecutionEngine.StartFailureText, task.Status.Message!.GetText());
        Assert.Contains(task.History!, m => m.MessageId == followUp.MessageId); // the message was committed
        Assert.Equal(0, Volatile.Read(ref followUpsRun));

        // A retry of the same message is a duplicate: it gets the failed task, and nothing runs.
        var retry = (await host.Handler.SendMessageAsync(new SendMessageRequest { Message = followUp }, Call()).WaitAsync(Wait)).RequireTask();
        Assert.Equal(TaskState.Failed, retry.Status.State);
        Assert.Equal(0, Volatile.Read(ref followUpsRun));
    }
}
