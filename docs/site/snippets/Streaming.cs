using A2A.Client;
using A2A.Server;

namespace A2A.DocSnippets.Streaming;

internal static class Samples
{
    public static AgentCard StreamingCard()
    {
        #region Capability
        return new AgentCard
        {
            Name = "My Agent",
            Description = "Streams its answer.",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities { Streaming = true },
        };
        #endregion
    }

    public static async Task StreamAMessage(A2AClient client)
    {
        #region Send
        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("stream this")))
        {
            switch (evt)
            {
                case ArtifactUpdateEvent { ArtifactUpdate: var update }:
                    Console.Write(string.Concat(update.Artifact.Parts.OfType<TextPart>().Select(p => p.Text)));
                    break;
                case StatusUpdateEvent { StatusUpdate: var status } when status.Status.State.IsTerminal():
                    Console.WriteLine($"[{status.Status.State}]");
                    break;
            }
        }
        #endregion
    }

    public static async Task FollowATask(A2AClient client)
    {
        #region Observe
        var started = await client.SendMessageAsync(
            Message.FromUser("do the work"),
            new SendMessageConfiguration { ReturnImmediately = true });

        if (started is TaskResponse { Task: var task })
        {
            await foreach (var evt in client.ObserveTaskAsync(task.Id))
            {
                if (evt is TaskEvent { Task: var snapshot })
                    Console.WriteLine($"snapshot: {snapshot.Status.State}");
                if (evt is StatusUpdateEvent { StatusUpdate: var status })
                    Console.WriteLine(status.Status.State);
            }
        }
        #endregion
    }

    public static async Task SubscribeOnce(A2AClient client, string taskId)
    {
        #region Subscribe
        await foreach (var evt in client.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId }))
            Console.WriteLine(evt);
        #endregion
    }

    public static void Configure(IServiceCollection services)
    {
        #region Options
        services.AddA2AServer(o =>
        {
            o.Streaming.ReplayBufferSize = 5000;
            o.Streaming.TerminalRetention = TimeSpan.FromMinutes(30);
            o.Streaming.KeepAliveInterval = TimeSpan.FromSeconds(10);

            o.Limits.MaxConcurrentStreamsPerTask = 5;
            o.Limits.MaxStreamDuration = TimeSpan.FromMinutes(30);
        });
        #endregion
    }
}
