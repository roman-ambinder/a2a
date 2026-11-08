using A2A.Server;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Extensions.AI;

/// <summary>Registers an <see cref="IChatClient"/> as the A2A agent.</summary>
public static class A2AChatClientAgentBuilderExtensions
{
    /// <summary>
    /// Uses a <see cref="ChatClientAgentExecutor"/> as the agent. Scoped: one instance per execution. Replaces an earlier executor.
    /// The <see cref="IChatClient"/> comes from DI, or from the keyed registration when <paramref name="serviceKey"/> is set.
    /// </summary>
    public static A2AServerBuilder AddChatClientAgent(this A2AServerBuilder builder, Action<ChatClientAgentOptions>? configure = null, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new ChatClientAgentOptions();
        configure?.Invoke(options);
        builder.Services.Replace(ServiceDescriptor.Scoped<IAgentExecutor>(sp =>
            new ChatClientAgentExecutor(
                serviceKey is null ? sp.GetRequiredService<IChatClient>() : sp.GetRequiredKeyedService<IChatClient>(serviceKey),
                options)));
        return builder;
    }
}
