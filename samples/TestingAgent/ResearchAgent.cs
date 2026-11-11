using A2A;
using A2A.Client;
using A2A.Server;

namespace TestingAgent;

/// <summary>
///     An agent that answers a question by asking a peer agent. It starts work, asks the peer, saves the answer as the
///     "report" artifact and completes. When the peer fails or gives no answer, the task fails.
/// </summary>
public sealed class ResearchAgent(IA2AClient peer) : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        await updater.StartWorkAsync("Asking the peer", cancellationToken);

        var request = new SendMessageRequest
            { Message = Message.FromUser($"What do you know about: {context.UserText}") };
        var answer = await peer.SendMessageAsync(request, cancellationToken: cancellationToken);

        var text = answer switch
        {
            MessageResponse m => m.Message.GetText(),
            TaskResponse { Task.Status.State: TaskState.Completed } t => t.Task.GetText(),
            _ => null
        };
        if (string.IsNullOrEmpty(text))
        {
            await updater.FailAsync("The peer could not help.", cancellationToken);
            return;
        }

        await updater.AddArtifactAsync([Part.FromText(text)], name: "report", cancellationToken: cancellationToken);
        await updater.CompleteAsync("Report ready", cancellationToken);
    }
}