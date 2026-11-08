using Microsoft.Extensions.Options;

namespace A2A.Server.Pipeline;

/// <summary>
/// Lists the URI of every registered <see cref="A2AExtensionOperation"/> in the public Agent Card's
/// <see cref="AgentCapabilities.Extensions"/> (not required), unless the card already lists it.
/// </summary>
internal sealed class ExtensionCardEntries(IEnumerable<A2AExtensionOperation> extensions) : IPostConfigureOptions<A2AServerOptions>
{
    public void PostConfigure(string? name, A2AServerOptions options)
    {
        var capabilities = options.AgentCard.Capabilities ??= new AgentCapabilities();
        foreach (var extension in extensions)
        {
            var list = capabilities.Extensions ??= [];
            if (list.Exists(e => string.Equals(e.Uri, extension.ExtensionUri, StringComparison.Ordinal))) continue;
            list.Add(new AgentExtension { Uri = extension.ExtensionUri });
        }
    }
}
