using A2A.Client;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests;

/// <summary>The reply shortcut of AddAgentExecutor, and the TaskUpdater overloads that need no named arguments.</summary>
public class ReplyAgentTests
{
    private static async Task<TestAgentHost> StartAsync(IAgentExecutor executor) =>
        await TestAgentHost.StartAsync(services: s => s.Replace(ServiceDescriptor.Singleton(executor)));

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task A_text_function_answers_a_new_turn_with_a_direct_reply(string binding)
    {
        var services = new ServiceCollection();
        services.AddA2AServer().AddAgentExecutor(ctx => $"You said: {ctx.UserText}");
        var executor = services.BuildServiceProvider().GetRequiredService<IAgentExecutor>();
        await using var host = await StartAsync(executor);

        var reply = await host.Client(binding).StartConversation().SendAsync("hello");
        Assert.NotNull(reply.Message); // no task was made
        Assert.Equal("You said: hello", reply.Text);
    }

    [Fact]
    public async Task An_async_text_function_completes_a_task_that_waited_for_input()
    {
        var reply = new ReplyAgentExecutor(async (ctx, ct) =>
        {
            await Task.Yield();
            return $"got {ctx.UserText}";
        });
        // Asks for input first, so the second message continues a task: the reply becomes an artifact.
        var executor = new DelegateAgentExecutor((ctx, updater, ct) => ctx.IsNewTask
            ? updater.RequireInputAsync("which colour?", ct).AsTask()
            : reply.ExecuteAsync(ctx, updater, ct));
        await using var host = await StartAsync(executor);

        var chat = host.Client(ProtocolBindings.JsonRpc).StartConversation();
        Assert.True((await chat.SendAsync("start")).NeedsInput);
        var done = await chat.SendAsync("blue");
        Assert.True(done.IsCompleted);
        Assert.Equal(TaskState.Completed, done.State);
        Assert.Equal("got blue", done.Text);
        Assert.Equal("answer", Assert.Single(done.Task!.Artifacts!).Name);
    }

    [Fact]
    public async Task Updater_overloads_take_only_a_token_and_add_a_text_artifact()
    {
        var executor = new DelegateAgentExecutor(async (ctx, updater, ct) =>
        {
            await updater.StartWorkAsync(ct);
            await updater.AddArtifactAsync($"echo: {ctx.UserText}", "answer", ct);
            await updater.CompleteAsync(ct);
        });
        await using var host = await StartAsync(executor);

        var reply = await host.Client(ProtocolBindings.HttpJson).StartConversation().SendAsync("ping");
        Assert.True(reply.IsCompleted);
        Assert.Equal("echo: ping", reply.Text);
        Assert.Null(reply.StatusText); // CompleteAsync(ct) sends no status message
    }

    [Fact]
    public void Null_functions_are_refused()
    {
        var builder = new ServiceCollection().AddA2AServer();
        Assert.Throws<ArgumentNullException>(() => builder.AddAgentExecutor((Func<AgentRequestContext, string>)null!));
        Assert.Throws<ArgumentNullException>(() =>
            builder.AddAgentExecutor((Func<AgentRequestContext, CancellationToken, Task<string>>)null!));
    }
}
