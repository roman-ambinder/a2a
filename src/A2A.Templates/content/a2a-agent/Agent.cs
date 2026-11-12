using A2A.Server;

namespace A2AAgent1;

/// <summary>Answers every message. Replace the body with your agent's work.</summary>
internal sealed class Agent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        await updater.StartWorkAsync(cancellationToken);

        // The writer streams one artifact: each write is a chunk, and completing the task seals it.
        await using var answer = updater.OpenArtifact("answer");
        await answer.WriteAsync("You said: ", cancellationToken);
        await answer.WriteAsync(context.UserText, cancellationToken);

        await updater.CompleteAsync(cancellationToken);
    }
}
