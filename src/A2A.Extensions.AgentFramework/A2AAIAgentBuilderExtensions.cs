using System.Diagnostics.CodeAnalysis;
using A2A.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Extensions.AgentFramework;

/// <summary>Registers a Microsoft Agent Framework <see cref="AIAgent" /> as the A2A agent.</summary>
[Experimental("A2AEXP006", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public static class A2AAIAgentBuilderExtensions
{
    /// <summary>
    ///     Uses an <see cref="AIAgentExecutor" /> as the agent. Scoped: one instance per execution, and the
    ///     <see cref="AIAgent" /> is resolved from that execution's scope, so the agent can depend on scoped services.
    ///     Replaces an earlier executor. The agent comes from DI, or from the keyed registration when
    ///     <paramref name="serviceKey" /> is set.
    /// </summary>
    /// <param name="builder">The A2A server builder.</param>
    /// <param name="configure">Sets the options. Called once, here.</param>
    /// <param name="serviceKey">The key of a keyed <see cref="AIAgent" /> registration, or null for the unkeyed one.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="NotSupportedException">The options choose a mode this version cannot run yet.</exception>
    public static A2AServerBuilder AddAIAgent(this A2AServerBuilder builder,
        Action<AIAgentExecutorOptions>? configure = null, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var options = new AIAgentExecutorOptions();
        configure?.Invoke(options);
        options.ThrowIfUnsupported();
        builder.Services.Replace(ServiceDescriptor.Scoped<IAgentExecutor>(sp =>
            new AIAgentExecutor(
                serviceKey is null
                    ? sp.GetRequiredService<AIAgent>()
                    : sp.GetRequiredKeyedService<AIAgent>(serviceKey),
                options,
                sp.GetService<AgentSessionStore>())));
        return builder;
    }
}
