using System.Collections.Concurrent;
using A2A.Server;
using A2A.Server.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Audit;

/// <summary>A logger provider that keeps what is logged.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, EventId Id, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Capture(categoryName, this);

    public void Dispose() { }

    private sealed class Capture(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, eventId, formatter(state, exception),
                state is IReadOnlyList<KeyValuePair<string, object?>> list ? list.ToList() : []));
    }
}

internal static class AuditTestSupport
{
    /// <summary>A host that writes its audit trail to <paramref name="sink"/>.</summary>
    public static Task<TestAgentHost> StartAsync(A2AAuditSink sink, Action<A2AServerOptions>? configure = null,
        Action<IServiceCollection>? services = null, CapturingLoggerProvider? logs = null, bool requireAuth = true, bool grpc = false) =>
        TestAgentHost.StartAsync(configure, requireAuth: requireAuth, grpc: grpc, services: s =>
        {
            s.Replace(ServiceDescriptor.Singleton(sink));
            if (logs is not null) s.AddLogging(b => b.AddProvider(logs));
            services?.Invoke(s);
        });

    public static IEnumerable<A2AAuditEvent> Final(this IEnumerable<A2AAuditEvent> events) =>
        events.Where(e => e.Outcome != A2AAuditOutcome.Attempted);
}
