using A2A.Server;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Extensions.AI;

/// <summary>Registers an <see cref="IChatClient" /> as the A2A agent.</summary>
public static class A2AChatClientAgentBuilderExtensions
{
    /// <summary>
    ///     Uses a <see cref="ChatClientAgentExecutor" /> as the agent. Scoped: one instance per execution. Replaces an earlier
    ///     executor.
    ///     The <see cref="IChatClient" /> comes from DI, or from the keyed registration when <paramref name="serviceKey" /> is
    ///     set.
    /// </summary>
    public static A2AServerBuilder AddChatClientAgent(this A2AServerBuilder builder,
        Action<ChatClientAgentOptions>? configure = null, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new ChatClientAgentOptions();
        configure?.Invoke(options);
        builder.Services.Replace(ServiceDescriptor.Scoped<IAgentExecutor>(sp =>
            new ChatClientAgentExecutor(
                serviceKey is null
                    ? sp.GetRequiredService<IChatClient>()
                    : sp.GetRequiredKeyedService<IChatClient>(serviceKey),
                options)));
        return builder;
    }

    /// <summary>
    ///     Uses <paramref name="chatClient" /> itself as the model of the A2A agent, without registering it in DI first.
    ///     The same client serves every execution, as an <see cref="IChatClient" /> usually does. Otherwise as
    ///     <see cref="AddChatClientAgent(A2AServerBuilder, Action{ChatClientAgentOptions}?, object?)" />.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="builder" /> or <paramref name="chatClient" /> is null.</exception>
    public static A2AServerBuilder AddChatClientAgent(this A2AServerBuilder builder, IChatClient chatClient,
        Action<ChatClientAgentOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(chatClient);
        var key = new InstanceKey(); // private to this registration, so it never meets a host's own keyed client
        builder.Services.AddKeyedSingleton(key, chatClient);
        return builder.AddChatClientAgent(configure, key);
    }

    private sealed class InstanceKey;
}