using A2A.Client;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>Use an <see cref="A2AClient" /> as an AI tool or as an <see cref="IChatClient" />.</summary>
public static class A2AClientAIExtensions
{
    /// <summary>
    ///     Wraps the remote agent as an AI tool. <paramref name="name" /> (letters, digits, <c>_</c> and <c>-</c>, up to 64)
    ///     and
    ///     <paramref name="description" /> are required and are never taken from the Agent Card, because the card is remote
    ///     text
    ///     and a tool description goes straight to the model.
    /// </summary>
    public static A2AAgentFunction AsAIFunction(this A2AClient client, string name, string description,
        A2AAgentFunctionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (!IsValidName(name))
            throw new ArgumentException("The name must be 1 to 64 characters: letters, digits, '_' or '-'.",
                nameof(name));
        return new A2AAgentFunction(client, name, description, options ?? new A2AAgentFunctionOptions());
    }

    /// <summary>
    ///     Uses the remote agent through <see cref="IChatClient" />. Disposing it does not dispose
    ///     <paramref name="client" />.
    /// </summary>
    public static A2AChatClient AsChatClient(this A2AClient client, A2AChatClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new A2AChatClient(client, options ?? new A2AChatClientOptions());
    }

    private static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 64) return false;
        foreach (var c in name)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
                return false;
        return true;
    }
}