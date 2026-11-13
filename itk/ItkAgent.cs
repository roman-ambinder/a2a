using A2A.Client;
using A2A.Client.Grpc;
using A2A.Client.Grpc.V0_3Compat;
using A2A.Client.V0_3Compat;
using A2A.Server;
using Google.Protobuf;
using Itk;

namespace A2A.Itk;

/// <summary>
///     Runs an a2a-itk traversal instruction (<c>instruction.proto</c>): answer, forward to the next agent with this SDK's
///     client, or run steps in order. The contract is a2a-itk's <c>docs/interoperability/05_sdk-integration.md</c>.
/// </summary>
internal sealed class ItkAgent(HttpClient http, ILogger<ItkAgent> logger) : IAgentExecutor
{
    /// <summary>The media type of an instruction part, as every ITK agent sends it.</summary>
    internal const string InstructionMediaType = "application/x-protobuf";

    internal const string InstructionFilename = "instruction.bin";

    /// <summary>The marker a held task adds to its reply, which the resubscribe behaviour looks for.</summary>
    internal const string TaskFinished = "task-finished";

    /// <summary>The token a push config is registered with; ITK's receiver accepts any.</summary>
    private const string PushToken = "itk-token";

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        if (ReadInstruction(context.Message) is not { } instruction)
        {
            await updater.FailAsync("No valid instruction found in request", cancellationToken);
            return;
        }

        await updater.StartWorkAsync(cancellationToken);
        string reply;
        try
        {
            reply = string.Join("\n", await RunAsync(instruction, cancellationToken));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Instruction failed");
            await updater.FailAsync($"Instruction failed: {ex.GetType().Name}", cancellationToken);
            return;
        }

        if (!Holds(instruction))
        {
            await updater.CompleteAsync(reply, cancellationToken);
            return;
        }

        // hold_task: report the reply and stay WORKING, with a status update every 2 s, until CancelTask stops it.
        await updater.StartWorkAsync(reply + "\n" + TaskFinished, cancellationToken);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                await updater.StartWorkAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // CancelTask: the engine moves the task to CANCELED.
        }
    }

    /// <summary>The instruction in the first part that holds one: a protobuf raw part, or base64 in a text part.</summary>
    internal static Instruction? ReadInstruction(Message message)
    {
        foreach (var part in message.Parts)
            try
            {
                switch (part)
                {
                    case RawPart raw when part.MediaType == InstructionMediaType || part.Filename == InstructionFilename:
                        return Instruction.Parser.ParseFrom(raw.Raw.Span);
                    case TextPart text:
                        return Instruction.Parser.ParseFrom(Convert.FromBase64String(text.Text));
                }
            }
            catch (Exception ex) when (ex is InvalidProtocolBufferException or FormatException)
            {
                // Not an instruction; try the next part.
            }

        return null;
    }

    internal static bool Holds(Instruction instruction)
    {
        return instruction.StepCase switch
        {
            Instruction.StepOneofCase.ReturnResponse => instruction.ReturnResponse.HoldTask,
            Instruction.StepOneofCase.Steps => instruction.Steps.Instructions.Any(Holds),
            _ => false
        };
    }

    private async Task<List<string>> RunAsync(Instruction instruction, CancellationToken cancellationToken)
    {
        switch (instruction.StepCase)
        {
            case Instruction.StepOneofCase.ReturnResponse:
                return [instruction.ReturnResponse.Response];
            case Instruction.StepOneofCase.CallAgent:
                return await CallAsync(instruction.CallAgent, cancellationToken);
            case Instruction.StepOneofCase.Steps:
                var all = new List<string>();
                foreach (var step in instruction.Steps.Instructions)
                    all.AddRange(await RunAsync(step, cancellationToken));
                return all;
            default:
                throw new InvalidOperationException($"Unknown instruction step {instruction.StepCase}");
        }
    }

    /// <summary>ITK's transport names, as a binding name of this SDK.</summary>
    internal static string Binding(string transport)
    {
        return transport.ToUpperInvariant() switch
        {
            "JSONRPC" => ProtocolBindings.JsonRpc,
            "HTTP+JSON" or "HTTP_JSON" or "REST" => ProtocolBindings.HttpJson,
            "GRPC" => ProtocolBindings.Grpc,
            _ => throw new NotSupportedException($"Unsupported transport '{transport}'")
        };
    }

    private async Task<List<string>> CallAsync(CallAgent call, CancellationToken cancellationToken)
    {
        var client = await ConnectAsync(call, cancellationToken);
        var request = new SendMessageRequest
        {
            Message = new Message
            {
                MessageId = Guid.NewGuid().ToString("N"),
                Role = Role.User,
                Parts = [Part.FromBytes(call.Instruction.ToByteArray(), InstructionMediaType, InstructionFilename)]
            }
        };
        if (call.BehaviorCase == CallAgent.BehaviorOneofCase.PushNotification)
        {
            var url = call.PushNotification.Url;
            if (!url.StartsWith("http://", StringComparison.Ordinal) && !url.StartsWith("https://", StringComparison.Ordinal))
                url = "http://" + url;
            request.Configuration = new SendMessageConfiguration
            {
                TaskPushNotificationConfig = new TaskPushNotificationConfig
                    { Url = url.TrimEnd('/') + "/notifications", Token = PushToken }
            };
        }

        if (call.BehaviorCase == CallAgent.BehaviorOneofCase.Resubscribe)
            return await ResubscribeAsync(client, request, cancellationToken);

        var texts = new List<string>();
        if (call.Streaming)
        {
            await foreach (var e in client.SendStreamingMessageAsync(request, cancellationToken: cancellationToken))
                AddText(texts, e);
        }
        else
        {
            var response = await client.SendMessageAsync(request, cancellationToken: cancellationToken);
            switch (response)
            {
                case MessageResponse m:
                    texts.Add(m.Message.GetText());
                    break;
                case TaskResponse t:
                    AddText(texts, t.Task);
                    break;
            }
        }

        return texts;
    }

    /// <summary>
    ///     A client for the agent at <c>agent_card_uri</c>, on the one binding the instruction names. The compat factory
    ///     reads a v1.0 card and a v0.3 one, so the same code calls both lines of every peer.
    /// </summary>
    private async Task<A2AClient> ConnectAsync(CallAgent call, CancellationToken cancellationToken)
    {
        var binding = Binding(call.Transport);
        var options = new A2AClientOptions { SupportedBindings = [binding] };
        // ITK agents list cleartext gRPC as a bare host:port, which this SDK reads as TLS unless told otherwise.
        if (binding == ProtocolBindings.Grpc)
            options.UseGrpc(null, useTlsForSchemeLessAddresses: false).UseGrpcV03(null, useTlsForSchemeLessAddresses: false);

        var uri = new Uri(call.AgentCardUri.EndsWith('/') ? call.AgentCardUri : call.AgentCardUri + "/");
        logger.LogInformation("Calling {Uri} over {Binding}", uri, binding);
        return await V03CompatClientFactory.CreateAsync(uri, http, options, cancellationToken: cancellationToken);
    }

    /// <summary>
    ///     The resubscribe behaviour: start a stream, drop it after the first event that names the task, subscribe again,
    ///     read until the held task reports <see cref="TaskFinished" />, then cancel it.
    /// </summary>
    private async Task<List<string>> ResubscribeAsync(A2AClient client, SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        string? taskId = null;
        await foreach (var e in client.SendStreamingMessageAsync(request, cancellationToken: cancellationToken))
            if ((taskId = e.TaskId) is not null)
                break;
        if (taskId is null) throw new InvalidOperationException("The stream named no task");

        var texts = new List<string>();
        await foreach (var e in client.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = taskId },
                           cancellationToken: cancellationToken))
        {
            var before = texts.Count;
            AddText(texts, e);
            if (texts.Skip(before).Any(t => t.Contains(TaskFinished, StringComparison.Ordinal))) break;
        }

        for (var i = 0; i < texts.Count; i++) texts[i] = texts[i].Replace(TaskFinished, "", StringComparison.Ordinal);
        await client.CancelTaskAsync(new CancelTaskRequest { Id = taskId }, cancellationToken: cancellationToken);
        return texts;
    }

    /// <summary>The text a stream event carries: a message, a status message, artifact parts, or a task snapshot's.</summary>
    internal static void AddText(List<string> texts, StreamResponse e)
    {
        switch (e)
        {
            case TaskEvent t:
                AddText(texts, t.Task);
                break;
            case StatusUpdateEvent s when s.StatusUpdate.Status.Message is { } m:
                AddNonEmpty(texts, m.GetText());
                break;
            default:
                AddNonEmpty(texts, e.GetText());
                break;
        }
    }

    /// <summary>
    ///     The text of a task: its status message, the agent messages in its history, and its artifacts. Repeats do not
    ///     matter: ITK checks that each expected token is in the reply.
    /// </summary>
    internal static void AddText(List<string> texts, AgentTask task)
    {
        if (task.Status.Message is { } status) AddNonEmpty(texts, status.GetText());
        foreach (var m in task.History ?? [])
            if (m.Role == Role.Agent)
                AddNonEmpty(texts, m.GetText());
        foreach (var a in task.Artifacts ?? [])
            AddNonEmpty(texts, a.GetText());
    }

    private static void AddNonEmpty(List<string> texts, string text)
    {
        if (!string.IsNullOrEmpty(text)) texts.Add(text);
    }
}
