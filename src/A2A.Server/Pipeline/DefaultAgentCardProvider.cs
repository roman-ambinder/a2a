using System.Text.Json;
using A2A.Json;
using Microsoft.Extensions.Options;

namespace A2A.Server.Pipeline;

/// <summary>
/// Default <see cref="IAgentCardProvider"/>: a copy of <see cref="A2AServerOptions.AgentCard"/> that lists the URI of every
/// registered <see cref="A2AExtensionOperation"/> in <see cref="AgentCapabilities.Extensions"/> (not required), unless the card
/// already lists it. Built once, on first use. The host's card is never changed. It adds no interfaces: the core has no addresses.
/// </summary>
internal sealed class DefaultAgentCardProvider : IAgentCardProvider
{
    private readonly Lazy<AgentCard> _card;

    public DefaultAgentCardProvider(IOptions<A2AServerOptions> options, IEnumerable<A2AExtensionOperation> extensions) =>
        _card = new(() => Build(options.Value.AgentCard, extensions), LazyThreadSafetyMode.ExecutionAndPublication);

    private DefaultAgentCardProvider(Func<AgentCard> build) => _card = new(build, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>A provider over <paramref name="options"/> alone, for callers built without DI.</summary>
    public static DefaultAgentCardProvider For(A2AServerOptions options) => new(() => Build(options.AgentCard, []));

    public AgentCard Get() => _card.Value;

    /// <summary>Copies the card through the wire format (cold path, once per process), then adds the extension entries.</summary>
    internal static AgentCard Build(AgentCard hostCard, IEnumerable<A2AExtensionOperation> extensions)
    {
        var card = JsonSerializer.Deserialize(A2AJson.SerializeToUtf8Bytes(hostCard), A2AJson.GetTypeInfo<AgentCard>())
            ?? throw new InvalidOperationException("The Agent Card could not be copied.");
        foreach (var extension in extensions)
        {
            var list = card.Capabilities.Extensions ??= [];
            if (list.Exists(e => string.Equals(e.Uri, extension.ExtensionUri, StringComparison.Ordinal))) continue;
            list.Add(new AgentExtension { Uri = extension.ExtensionUri });
        }
        return card;
    }
}
