using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Telemetry;

/// <summary>
///     Log event ids. Packages are loaded together in one host, so a reader who filters logs by id must never get two
///     different events for one id.
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

    // Every package, also those this test project does not reference (Postgres, Redis, MCP, v0.3): a host can load any of
    // them together, so the ids are read from the source.
    private static readonly Regex LoggerMessageId = new(@"\[LoggerMessage\(\s*EventId\s*=\s*(\d+)", RegexOptions.Compiled);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "A2A.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("A2A.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Event_ids_are_unique_across_every_package()
    {
        var root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        var ids = (
            from file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            where !file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) &&
                  !file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            from Match m in LoggerMessageId.Matches(File.ReadAllText(file))
            select (Id: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                Where: Path.GetRelativePath(root, file))).ToList();

        Assert.True(ids.Count > 100, $"Found only {ids.Count} [LoggerMessage] ids under src/; did the pattern stop matching?");
        var shared = ids
            .GroupBy(e => e.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(e => e.Where))}")
            .ToList();
        Assert.Empty(shared);
    }

    [Fact]
    public void Every_log_message_has_an_explicit_event_id()
    {
        // Without EventId the generator makes one up, which is not in any range and can collide unseen.
        var root = RepoRoot();
        var sep = Path.DirectorySeparatorChar;
        var missing = (
            from file in Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            where !file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) &&
                  !file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            from line in File.ReadLines(file).Select((text, i) => (text, n: i + 1))
            where line.text.Contains("[LoggerMessage(", StringComparison.Ordinal) &&
                  !LoggerMessageId.IsMatch(line.text)
            select $"{Path.GetRelativePath(root, file)}:{line.n}").ToList();

        Assert.Empty(missing);
    }

    [Fact]
    public void Security_events_keep_their_documented_ids()
    {
        // 1200 information, 1201 warning, 1202 anomaly (observability guide, spec decisions).
        var ids = EventIds().Where(e => e.Where.Contains("ServerLog.Security", StringComparison.Ordinal)).Select(e => e.Id);

        Assert.Equal([1200, 1201, 1202], ids.Order());
    }
}
