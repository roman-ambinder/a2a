using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>Options for <see cref="ChatClientAgentExecutor" />.</summary>
/// <remarks>The reply, history and security settings are on <see cref="A2AModelAgentOptions" />.</remarks>
public sealed class ChatClientAgentOptions : A2AModelAgentOptions
{
    /// <summary>Added as the first system message of every run. Null for none.</summary>
    public string? Instructions { get; set; }

    /// <summary>
    ///     Options for each model call (tools, temperature, ...). Cloned for each run.
    ///     <see cref="Microsoft.Extensions.AI.ChatOptions.ConversationId" /> is always cleared, so callers never share
    ///     provider state.
    /// </summary>
    public ChatOptions? ChatOptions { get; set; }
}
