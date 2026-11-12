using System.Diagnostics.CodeAnalysis;
using A2A.Server;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using A2A.Server.Coordination;
using A2A.Server.Retention;
using A2A.Server.Storage;

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
    /// <remarks>
    ///     With <see cref="AgentSessionMode.Stored" />, register an <see cref="AgentSessionStore" /> as well: the host
    ///     refuses to start without one. The stored session of a context is deleted when the retention sweep
    ///     (<c>AddA2ARetention</c>) deletes the context.
    /// </remarks>
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
                options.SessionMode == AgentSessionMode.Stored ? sp.GetService<AgentSessionStore>() : null,
                sp.GetService<ILogger<AIAgentExecutor>>(), sp.GetService<TaskStore>(),
                sp.GetService<ExecutionCoordinator>(), sp.GetService<TimeProvider>())));
        // The last call wins, as for the executor. The hook and the check read it, so they follow a later call too.
        builder.Services.Replace(ServiceDescriptor.Singleton(new AIAgentRegistration(options.SessionMode, serviceKey)));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IA2AContextLifecycle, AgentSessionContextLifecycle>());
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, StoredSessionCheck>());
        return builder;
    }
}
