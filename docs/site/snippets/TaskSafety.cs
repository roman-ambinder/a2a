using A2A.Client;
using A2A.Security.Http;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.TaskSafety;

internal sealed class DeleteFilesAgent : IAgentExecutor
{
    #region Approval
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var approval = new PendingApproval("delete-files-17", "Delete 3 files in /reports");

        switch (context.Approval)
        {
            case ApprovalState.Approved:
                // The follow-up carried the hash of the exact action. Do it now.
                await updater.CompleteAsync(cancellationToken: cancellationToken);
                break;
            default:
                // First message, or a follow-up without the hash: ask again.
                await updater.RequireApprovalAsync(approval, "Delete 3 files in /reports?", cancellationToken);
                break;
        }
    }
    #endregion
}

internal static class Samples
{
    public static void ServerLimits()
    {
        var services = new ServiceCollection();
        #region ServerLimits
        services.AddA2AServer(o =>
        {
            o.MaxTaskDuration = TimeSpan.FromMinutes(10);
            o.MaxTurnsPerTask = 20;
            o.CancelGracePeriod = TimeSpan.FromSeconds(5);
        });
        #endregion
    }

    public static async Task ClientHook(HttpClient http)
    {
        #region Hook
        var options = new A2AClientOptions
        {
            OnMessageReceived = (received, ct) =>
            {
                var text = received.Message.GetText();
                if (text.Contains("ignore previous instructions", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Message in task {received.TaskId} rejected.");
                return ValueTask.CompletedTask;
            },
        };
        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), http, options);
        #endregion
        _ = client;
    }

    public static async Task Approve(A2AClient client, string taskId, AgentTask waiting)
    {
        #region Approve
        // The status message of a task waiting for approval carries the hash.
        var hash = waiting.Status.Message!.Metadata![A2AProtocol.ApprovalHashKey];

        // Show the user what the agent asked. If they agree, echo the hash in the follow-up.
        var followUp = Message.FromUser("Yes, go ahead.", waiting.ContextId, taskId);
        followUp.Metadata = new() { [A2AProtocol.ApprovalHashKey] = hash };
        await client.SendMessageAsync(followUp);
        #endregion
    }

    public static async Task<byte[]> Fetch(UrlPart part, CancellationToken cancellationToken)
    {
        #region SafeHttp
        using var http = SafeHttp.CreateClient();
        var bytes = await SafeHttp.DownloadAsync(http, part.Url, maxBytes: 10_000_000, cancellationToken: cancellationToken);
        #endregion
        return bytes;
    }

    public static async Task<string> Untrusted(A2ACardResolver resolver, Uri agentUrl)
    {
        #region Untrusted
        var resolved = await resolver.ResolveAsync(agentUrl);
        // Sanitized: control and bidi characters removed, at most 2000 characters.
        var description = resolved.Description.ToString();
        #endregion
        return description;
    }
}
