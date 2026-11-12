// Shared source, compiled into A2A.Extensions.AI and A2A.Extensions.AgentFramework (linked, not referenced: shipped
// packages do not use InternalsVisibleTo). The namespace differs per assembly so the two internal copies never share a
// full name. All types are internal.

using A2A.Server;
using Microsoft.Extensions.AI;

#if A2A_AGENT_FRAMEWORK
namespace A2A.Extensions.AgentFramework.Shared;
#else
namespace A2A.Extensions.AI.Shared;
#endif

/// <summary>What <see cref="HistoryPrompt.BuildAsync" /> needs from the executor's options.</summary>
/// <param name="Instructions">Added as the first system message. Null or empty for none.</param>
/// <param name="MaxHistoryMessages">How many of the newest history messages to keep. At least 1.</param>
/// <param name="AllowUrlParts">Whether URL parts reach the model.</param>
/// <param name="UseContextMemory">Whether the earlier tasks of the context and the summary go first.</param>
/// <param name="ContextMemoryMaxTasks">With <paramref name="UseContextMemory" />: how many earlier tasks to read.</param>
/// <param name="ContextMemoryMaxBytes">With <paramref name="UseContextMemory" />: the size limit for earlier messages.</param>
internal sealed record HistoryPromptOptions(
    string? Instructions,
    int MaxHistoryMessages,
    bool AllowUrlParts,
    bool UseContextMemory,
    int ContextMemoryMaxTasks,
    long ContextMemoryMaxBytes);

/// <summary>Builds the model prompt from the A2A task history.</summary>
internal static class HistoryPrompt
{
    private const string SummaryPrefix = "Summary of the earlier conversation:\n";

    /// <summary>
    ///     The system message (when there are instructions), the context summary and earlier tasks (with context memory), then
    ///     the task history with the new message last, trimmed to the newest messages and starting at a user turn.
    /// </summary>
    internal static async ValueTask<List<ChatMessage>> BuildAsync(AgentRequestContext context,
        HistoryPromptOptions options, CancellationToken cancellationToken)
    {
        var source = new List<Message>();
        string? summary = null;
        if (options.UseContextMemory && context.Conversation is { } memory)
        {
            summary = await memory.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            source.AddRange(await memory
                .ReadEarlierMessagesAsync(options.ContextMemoryMaxTasks, options.ContextMemoryMaxBytes,
                    cancellationToken).ConfigureAwait(false));
        }

        if (context.Task?.History is { } history) source.AddRange(history);
        // History normally has the new message already; add it only when it is missing.
        if (source.Count == 0 || source[^1].MessageId != context.Message.MessageId) source.Add(context.Message);

        var skip = Math.Max(0, source.Count - options.MaxHistoryMessages);
        // Trimming can leave an agent reply first. Start at a user turn; the last message is the user's, so one always remains.
        while (skip < source.Count - 1 && source[skip].Role == Role.Agent) skip++;
        var messages = new List<ChatMessage>(source.Count - skip + 1);
        if (!string.IsNullOrEmpty(options.Instructions))
            messages.Add(new ChatMessage(ChatRole.System, options.Instructions));
        // The summary comes from caller text, so it is a user message and never gains system rights. It does not count toward MaxHistoryMessages.
        if (!string.IsNullOrEmpty(summary)) messages.Add(new ChatMessage(ChatRole.User, SummaryPrefix + summary));
        for (var i = skip; i < source.Count; i++)
        {
            var contents = new List<AIContent>();
            foreach (var part in source[i].Parts)
                if (ModelContent.ToModelContent(part, options.AllowUrlParts) is { } content)
                    contents.Add(content);
            if (contents.Count == 0) continue;
            messages.Add(new ChatMessage(source[i].Role == Role.Agent ? ChatRole.Assistant : ChatRole.User, contents));
        }

        return messages;
    }
}
