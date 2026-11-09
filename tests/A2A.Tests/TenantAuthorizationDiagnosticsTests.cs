using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>A startup warning for per-tenant agents behind the default allow-all authorization handler.</summary>
public sealed class TenantAuthorizationDiagnosticsTests
{
    private const int EventId = 259;

    private sealed class Cards : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken) => default;
    }

    private sealed class Resolver : AgentExecutorResolver
    {
        public override IAgentExecutor? Resolve(string tenant, IServiceProvider services) => null;
    }

    private sealed class DenyAll : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2A.Server.Pipeline.A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }

    private sealed class CapturingLogger : ILogger<A2AStartupDiagnostics>
    {
        public List<(int Id, LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
    }

    private static async Task<CapturingLogger> RunAsync(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddA2AServer();
        configure(services);
        await using var provider = services.BuildServiceProvider();
        var logger = new CapturingLogger();
        // The same object the host runs, with a logger that records.
        IHostedService diagnostics = new A2AStartupDiagnostics(provider, logger);
        await diagnostics.StartAsync(CancellationToken.None);
        return logger;
    }

    [Fact]
    public async Task Card_provider_with_the_default_handler_warns()
    {
        var logger = await RunAsync(s => s.AddSingleton<TenantCardProvider, Cards>());

        var entry = Assert.Single(logger.Entries, e => e.Id == EventId);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("AddAuthorizationHandler", entry.Message);
        Assert.Contains("tenant", entry.Message);
    }

    [Fact]
    public async Task Builder_registration_of_a_card_provider_warns()
    {
        var logger = await RunAsync(s => s.AddA2AServer().AddTenantCardProvider<Cards>());
        Assert.Single(logger.Entries, e => e.Id == EventId);
    }

    [Fact]
    public async Task Executor_resolver_with_the_default_handler_warns()
    {
        var logger = await RunAsync(s => s.AddA2AServer().AddAgentExecutorResolver<Resolver>());
        Assert.Single(logger.Entries, e => e.Id == EventId);
    }

    [Fact]
    public async Task A_custom_handler_does_not_warn()
    {
        var logger = await RunAsync(s => s.AddA2AServer().AddTenantCardProvider<Cards>().AddAuthorizationHandler<DenyAll>());
        Assert.DoesNotContain(logger.Entries, e => e.Id == EventId);
    }

    [Fact]
    public async Task A_single_agent_host_does_not_warn()
    {
        var logger = await RunAsync(_ => { });
        Assert.DoesNotContain(logger.Entries, e => e.Id == EventId);
    }
}
