using A2A.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace A2A.Mcp;

/// <summary>Registers MCP tools as the A2A agent.</summary>
public static class A2AMcpAgentBuilderExtensions
{
    /// <summary>
    ///     Uses a <see cref="McpToolAgentExecutor" /> as the agent. Scoped: one instance per execution. Replaces an earlier
    ///     executor.
    ///     The <see cref="McpClient" /> comes from DI, or from the keyed registration when <paramref name="serviceKey" /> is
    ///     set. The host owns it and its credentials.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="configure" /> allows no tool.</exception>
    public static A2AServerBuilder AddMcpToolAgent(this A2AServerBuilder builder, Action<McpToolAgentOptions> configure,
        object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new McpToolAgentOptions();
        configure(options);
        options.ThrowIfNoTools();
        builder.Services.Replace(ServiceDescriptor.Scoped<IAgentExecutor>(sp =>
            new McpToolAgentExecutor(
                serviceKey is null
                    ? sp.GetRequiredService<McpClient>()
                    : sp.GetRequiredKeyedService<McpClient>(serviceKey),
                options,
                sp.GetService<ILogger<McpToolAgentExecutor>>())));
        return builder;
    }
}