using A2A.Client;
using A2A.Elicitations;
using A2A.Security.Http;
using A2A.Server;

namespace Snippets.TaskSafety;

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

internal sealed class BookingAgent : IAgentExecutor
{
    #region ElicitServer
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        if (context.ElicitationAnswer is { } answer)
        {
            // The server checked the answer against the form. The agent decides what each action means.
            if (answer.Action == ElicitationAction.Accept)
                await updater.CompleteAsync($"Booked {answer.GetInt64("nights")} nights in {answer.GetString("city")}.", cancellationToken);
            else
                await updater.FailAsync("No booking without details.", cancellationToken);
            return;
        }

        var form = new ElicitationForm(
        [
            ElicitationField.Text("city", "City", required: true, maxLength: 100),
            ElicitationField.Integer("nights", "Nights", required: true, minimum: 1, maximum: 30),
        ]);
        // The text is the question in words. A client that cannot read the form answers it in plain text.
        await updater.RequireInputAsync(context, form, "Which city, and how many nights?", cancellationToken);
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

    public static async Task Elicit(A2AClient client)
    {
        #region ElicitClient
        var started = await client.SendMessageAsync(Message.FromUser("Book me a room"), options: new A2ARequestOptions().UseElicitation());
        var task = ((TaskResponse)started).Task;

        var form = task.GetElicitationForm(); // null when the agent sent no form, or a form this client cannot read
        if (form is not null)
        {
            // Show the user who is asking and the text, then the fields. The user fills them in, not a model.
            var answer = ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo" }, { "nights", 2 } });
            await client.AnswerElicitationAsync(task, answer); // or ElicitationAnswer.Decline(form) / .Cancel(form)
        }
        #endregion
    }
}
