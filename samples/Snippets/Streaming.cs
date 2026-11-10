using A2A.Client;
using A2A.Server;

namespace Snippets.Streaming;

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

    public static async Task SendChunks(TaskUpdater updater, CancellationToken ct)
    {
        #region Chunks
        var id = await updater.AddArtifactAsync([Part.FromText("Hello, ")], name: "greeting", cancellationToken: ct);
        await updater.AddArtifactAsync([Part.FromText("world")], id, append: true, lastChunk: true, cancellationToken: ct);

        try
        {
            // The artifact is sealed now: an append throws.
            await updater.AddArtifactAsync([Part.FromText("!")], id, append: true, cancellationToken: ct);
        }
        catch (InvalidOperationException)
        {
            // Send it with append: false to replace the artifact, or use a new artifact id.
            await updater.AddArtifactAsync([Part.FromText("Hello, world!")], id, lastChunk: true, cancellationToken: ct);
        }
        #endregion
    }

    public static async Task SendHashedChunks(TaskUpdater updater, CancellationToken ct)
    {
        #region HashedChunks
        var id = await updater.AddHashedArtifactAsync([Part.FromText("Hello, ")], name: "greeting", cancellationToken: ct);
        await updater.AddHashedArtifactAsync([Part.FromText("world")], id, append: true, cancellationToken: ct);

        // No parts: this only seals the artifact. It repeats the hash of the whole artifact.
        await updater.AddHashedArtifactAsync([], id, append: true, lastChunk: true, cancellationToken: ct);
        #endregion
    }

    public static async Task VerifyStream(A2AClient client)
    {
        #region VerifyStream
        using var verifier = new ArtifactVerifier();
        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("stream this")))
        {
            foreach (var result in verifier.Observe(evt))
            {
                if (result.Status is ArtifactVerificationStatus.Mismatch or ArtifactVerificationStatus.Unverifiable)
                    Console.WriteLine($"Artifact {result.ArtifactId}: {result.Status}");
            }
        }
        #endregion
    }

    public static async Task VerifyTask(A2AClient client, string taskId)
    {
        #region VerifyTask
        var task = await client.GetTaskAsync(taskId);
        foreach (var artifact in task.Artifacts ?? [])
            Console.WriteLine($"Artifact {artifact.ArtifactId}: {ArtifactVerifier.Verify(artifact).Status}");
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

#pragma warning disable A2AEXP005 // the A2A 1.1 live exchange is experimental
    public static async Task LiveExchange(A2AClient client, CancellationToken ct)
    {
        #region LiveExchange
        await using var live = await client.OpenLiveAsync(Message.FromUser("start"), cancellationToken: ct);
        await live.SendAsync(Message.FromUser("more input"), ct); // no task id: the exchange's task
        await foreach (var evt in live.Events.WithCancellation(ct))
        {
            // TaskEvent first, then status, artifact and MessageUpdateEvent events; StreamErrorEvent for a refused request.
            if (evt is StreamErrorEvent { Error: var refused })
                Console.WriteLine($"refused {refused.ReferenceId}: {refused.Code}");
        }
        #endregion
    }

    public static async Task ReadLiveInput(AgentRequestContext context, CancellationToken ct)
    {
        #region LiveInput
        await foreach (var input in context.LiveInput!.ReadAllAsync(ct))
        {
            // input.Message and input.Artifacts are already stored on the task.
            Console.WriteLine(input.Message?.GetText());
        }
        #endregion
    }
#pragma warning restore A2AEXP005
}
