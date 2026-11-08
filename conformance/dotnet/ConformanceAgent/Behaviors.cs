// Test behaviors the conformance suites expect from the agent.
//
// ACTS (a2a-itk, spec section 11): the first text part of the first user message starts with a "tck-*" name.
//   The name belongs to the task: multi-turn follow-ups carry no prefix, so it is looked up in the task history.
// TCK (a2a-tck, scenarios/*.feature): the messageId starts with a "tck-*" (or "test-resubscribe-*") name.
// The ACTS check runs first; no TCK message text starts with "tck-".
using System.Text.Json;
using System.Text.RegularExpressions;
using A2A;
using A2A.Server;

namespace ConformanceAgent;

internal sealed partial class Behaviors : IAgentExecutor
{
    // Greedy to the word boundary, so "tck-artifact-file-url" wins over "tck-artifact-file".
    [GeneratedRegex(@"^(tck-[a-z0-9]+(?:-[a-z0-9]+)*)")]
    private static partial Regex ActsName();

    /// <summary>Long enough that a client polling every 2 s sees WORKING at least once.</summary>
    private static readonly TimeSpan LongRunning = TimeSpan.FromSeconds(1);

    /// <summary>TCK resubscribe test: "wait for 2x streaming timeout" (the Python SUT waits 4 s).</summary>
    private static readonly TimeSpan ResubscribeHold = TimeSpan.FromSeconds(4);

    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        if (ActsBehavior(context) is { } acts) return Acts(acts, context, updater, ct);
        return Tck(context.Message.MessageId, updater, ct);
    }

    private static string? FirstText(Message? message) => message?.Parts.OfType<TextPart>().FirstOrDefault()?.Text;

    private static string? ActsNameIn(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : ActsName().Match(text.Trim()) is { Success: true } m ? m.Value : null;

    internal static string? ActsBehavior(AgentRequestContext context)
    {
        if (ActsNameIn(FirstText(context.Message)) is { } named) return named;
        foreach (var earlier in context.Task?.History ?? [])
            if (earlier.Role == Role.User && ActsNameIn(FirstText(earlier)) is { } found) return found;
        return null;
    }

    // ---------------------------------------------------------------- ACTS

    private static async Task Acts(string behavior, AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
    {
        switch (behavior)
        {
            case "tck-message-response":
                await updater.ReplyAsync("tck-message-response ok", ct);
                return;
            case "tck-complete-task":
                await updater.CompleteAsync("tck-complete-task ok", ct);
                return;
            case "tck-task-failure":
                await updater.FailAsync("tck-task-failure: the task failed on purpose", ct);
                return;
            case "tck-reject-task":
                await updater.RejectAsync("tck-reject-task: rejected", ct);
                return;
            case "tck-input-required":
                await updater.RequireInputAsync("tck-input-required: more input needed", ct);
                return;
            case "tck-auth-required":
                await updater.RequireAuthAsync("tck-auth-required: authorize out of band", ct);
                return;
            case "tck-multi-turn":
                if ((FirstText(context.Message) ?? "").Trim().StartsWith("done", StringComparison.OrdinalIgnoreCase))
                    await updater.CompleteAsync("multi-turn complete", ct);
                else
                    await updater.RequireInputAsync("more input please", ct);
                return;
            case "tck-cancel":
                // Stay WORKING until CancelTask fires the token; the default CancelAsync emits CANCELED.
                await updater.StartWorkAsync("waiting for cancel", ct);
                await Task.Delay(Timeout.Infinite, ct);
                return;
            case "tck-long-running":
                await updater.StartWorkAsync("working", ct);
                await Task.Delay(LongRunning, ct);
                // ACTS CORE-EXEC-001 expects the finished task to carry an artifact.
                await updater.AddArtifactAsync([Part.FromText("long running result")], name: "long-running", cancellationToken: ct);
                await updater.CompleteAsync("long running work finished", ct);
                return;
            case "tck-stream-basic":
                await updater.StartWorkAsync("streaming started", ct);
                await updater.AddArtifactAsync([Part.FromText("streamed content")], name: "streamed", cancellationToken: ct);
                await updater.CompleteAsync("tck-stream-basic ok", ct);
                return;
            case "tck-stream-chunked":
                await updater.StartWorkAsync("streaming started", ct);
                string[] chunks = ["chunk one ", "chunk two ", "chunk three"];
                var id = await updater.AddArtifactAsync([Part.FromText(chunks[0])], name: "chunked", cancellationToken: ct);
                for (var i = 1; i < chunks.Length; i++)
                    await updater.AddArtifactAsync([Part.FromText(chunks[i])], artifactId: id, append: true, lastChunk: i == chunks.Length - 1, cancellationToken: ct);
                await updater.CompleteAsync("tck-stream-chunked ok", ct);
                return;
            case "tck-artifact-text":
                await ArtifactAndComplete(updater, Part.FromText("generated text content"), behavior, ct);
                return;
            case "tck-artifact-data":
                await ArtifactAndComplete(updater, Part.FromData(JsonSerializer.SerializeToElement(new { key = "value", count = 1 })), behavior, ct);
                return;
            case "tck-artifact-file":
                await ArtifactAndComplete(updater, Part.FromBytes("file bytes"u8.ToArray(), "text/plain", "document.txt"), behavior, ct);
                return;
            case "tck-artifact-file-url":
                await ArtifactAndComplete(updater, Part.FromUrl(new Uri("https://example.com/document.txt"), "text/plain", "document.txt"), behavior, ct);
                return;
            case ClientParse.Behavior:
                await ClientParse.RunAsync(context.Message, updater, ct);
                return;
            default:
                // Fail loudly: a silent success would claim a behavior the agent does not have.
                await updater.FailAsync($"unimplemented ACTS behavior \"{behavior}\"", ct);
                return;
        }
    }

    private static async Task ArtifactAndComplete(TaskUpdater updater, Part part, string behavior, CancellationToken ct)
    {
        await updater.AddArtifactAsync([part], name: behavior, cancellationToken: ct);
        await updater.CompleteAsync($"{behavior} ok", ct);
    }

    // ---------------------------------------------------------------- TCK

    private static async Task Tck(string messageId, TaskUpdater updater, CancellationToken ct)
    {
        // Longest names first where one is a prefix of another.
        if (messageId.StartsWith("tck-stream-artifact-chunked", StringComparison.Ordinal))
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            var id = await updater.AddArtifactAsync([Part.FromText("chunk-1 ")], cancellationToken: ct);
            await updater.AddArtifactAsync([Part.FromText("chunk-2")], artifactId: id, append: true, lastChunk: true, cancellationToken: ct);
            await updater.CompleteAsync(cancellationToken: ct);
        }
        else if (messageId.StartsWith("test-resubscribe-message-id", StringComparison.Ordinal))
        {
            await updater.StartWorkAsync(cancellationToken: ct);
            await Task.Delay(ResubscribeHold, ct);
            await updater.CompleteAsync(cancellationToken: ct);
        }
        else if (messageId.StartsWith("tck-stream-artifact-text", StringComparison.Ordinal))
            await Stream(updater, Part.FromText("Streamed text content"), ct);
        else if (messageId.StartsWith("tck-stream-artifact-file", StringComparison.Ordinal))
            await Stream(updater, Part.FromBytes("tck"u8.ToArray(), "text/plain", "output.txt"), ct);
        else if (messageId.StartsWith("tck-stream-ordering-001", StringComparison.Ordinal))
            await Stream(updater, Part.FromText("Ordered output"), ct);
        else if (messageId.StartsWith("tck-stream-001", StringComparison.Ordinal))
            await Stream(updater, Part.FromText("Stream hello from TCK"), ct);
        else if (messageId.StartsWith("tck-stream-002", StringComparison.Ordinal))
            await updater.CompleteAsync(cancellationToken: ct);
        else if (messageId.StartsWith("tck-stream-003", StringComparison.Ordinal))
            await Stream(updater, Part.FromText("Stream task lifecycle"), ct);
        else if (messageId.StartsWith("tck-artifact-file-url", StringComparison.Ordinal))
            await TckArtifact(updater, Part.FromUrl(new Uri("https://example.com/output.txt"), "text/plain", "output.txt"), ct);
        else if (messageId.StartsWith("tck-artifact-file", StringComparison.Ordinal))
            await TckArtifact(updater, Part.FromBytes("tck"u8.ToArray(), "text/plain", "output.txt"), ct);
        else if (messageId.StartsWith("tck-artifact-text", StringComparison.Ordinal))
            await TckArtifact(updater, Part.FromText("Generated text content"), ct);
        else if (messageId.StartsWith("tck-artifact-data", StringComparison.Ordinal))
            await TckArtifact(updater, Part.FromData(JsonSerializer.SerializeToElement(new { key = "value", count = 42 })), ct);
        else if (messageId.StartsWith("tck-message-response", StringComparison.Ordinal))
            await updater.ReplyAsync("Direct message response", ct);
        else if (messageId.StartsWith("tck-input-required", StringComparison.Ordinal))
            await updater.RequireInputAsync("Input required", ct);
        else if (messageId.StartsWith("tck-reject-task", StringComparison.Ordinal))
            await updater.RejectAsync("rejected", ct);
        else if (messageId.StartsWith("tck-complete-task", StringComparison.Ordinal))
            await updater.CompleteAsync("Hello from TCK", ct);
        else
            await updater.CompleteAsync("Unhandled messageId prefix: " + messageId, ct);
    }

    private static async Task Stream(TaskUpdater updater, Part part, CancellationToken ct)
    {
        await updater.StartWorkAsync(cancellationToken: ct);
        await updater.AddArtifactAsync([part], cancellationToken: ct);
        await updater.CompleteAsync(cancellationToken: ct);
    }

    private static async Task TckArtifact(TaskUpdater updater, Part part, CancellationToken ct)
    {
        await updater.AddArtifactAsync([part], cancellationToken: ct);
        await updater.CompleteAsync(cancellationToken: ct);
    }
}
