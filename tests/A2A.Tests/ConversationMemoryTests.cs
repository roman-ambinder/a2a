using System.Security.Claims;
using A2A.Server;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>The handler gives the executor a <see cref="ConversationMemory"/> for the caller, this context and this task.</summary>
public sealed class ConversationMemoryTests
{
    private static ServerCallContext Call(string user = "alice") => new()
    {
        Principal = user,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "Test")),
    };

    /// <summary>Reads its earlier messages on each run, keyed by the first word of the message, then completes with a reply.</summary>
    private sealed class RecordingAgent : IAgentExecutor
    {
        public Dictionary<string, string[]> Earlier { get; } = [];

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            var memory = context.Conversation ?? throw new InvalidOperationException("The handler did not set Conversation.");
            var earlier = await memory.ReadEarlierMessagesAsync(cancellationToken: cancellationToken);
            lock (Earlier) Earlier[context.UserText] = earlier.Select(m => m.GetText()).ToArray();
            await updater.CompleteAsync("reply to " + context.UserText, cancellationToken);
        }
    }

    [Fact]
    public async Task A_second_task_in_a_context_reads_the_first_tasks_messages_and_not_its_own()
    {
        var agent = new RecordingAgent();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<IAgentExecutor>(agent));

        var first = (await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("one") }, Call())).RequireTask();
        var second = (await host.Handler.SendMessageAsync(
            new SendMessageRequest { Message = Message.FromUser("two", contextId: first.ContextId) }, Call())).RequireTask();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.ContextId, second.ContextId);
        Assert.Empty(agent.Earlier["one"]);
        Assert.Equal(["one", "reply to one"], agent.Earlier["two"]);
    }
}
