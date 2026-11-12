using System.Reflection;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Telemetry;

/// <summary>
///     Log event ids of the client, server, ASP.NET Core and gRPC assemblies. These are loaded together in one host, so a
///     reader who filters logs by id must never get two different events for one id.
/// </summary>
public sealed class LogEventIdTests
{
    private static readonly Assembly[] Assemblies =
    [
        typeof(A2AClient).Assembly,
        typeof(A2AServerOptions).Assembly,
        typeof(A2AHttpOptions).Assembly,
        typeof(A2AGrpcExtensions).Assembly,
    ];

    private static IEnumerable<(int Id, string Where)> EventIds() =>
        from assembly in Assemblies
        from type in assembly.GetTypes()
        from method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                                       BindingFlags.Instance | BindingFlags.DeclaredOnly)
        let attribute = method.GetCustomAttribute<LoggerMessageAttribute>()
        where attribute is not null
        select (attribute.EventId, $"{assembly.GetName().Name}: {type.Name}.{method.Name}");

    [Fact]
    public void Event_ids_are_unique_across_the_server_and_client_assemblies()
    {
        var shared = EventIds()
            .GroupBy(e => e.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => e.Where))}")
            .ToList();

        Assert.Empty(shared);
    }

    [Fact]
    public void Security_events_keep_their_documented_ids()
    {
        var ids = EventIds().Where(e => e.Where.Contains(".SecurityEvent", StringComparison.Ordinal)).Select(e => e.Id);

        Assert.Equal([1200, 1201], ids.Order());
    }
}
