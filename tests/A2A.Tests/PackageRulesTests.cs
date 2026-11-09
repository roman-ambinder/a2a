using System.Xml.Linq;

namespace A2A.Tests;

/// <summary>Dependency rules for the packages under <c>src/</c>, read from their project files.</summary>
public sealed class PackageRulesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "A2A.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("A2A.slnx not found above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string Name, XDocument Doc)> SrcProjects() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetDirectoryName(p) is { } d && Path.GetDirectoryName(d) == Path.Combine(RepoRoot(), "src"))
            .Select(p => (Path.GetFileNameWithoutExtension(p), XDocument.Load(p)));

    private static string[] PackageRefs(XDocument doc) =>
        doc.Descendants("PackageReference")
            // The rule covers the net8/net10 graph. netstandard2.0 groups hold its compatibility packages and a build-only polyfill generator.
            .Where(e => !(((string?)e.Parent?.Attribute("Condition")) ?? "").Contains("netstandard2.0", StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute("Include") ?? "").ToArray();

    private static string[] RefsOf(string project) =>
        PackageRefs(SrcProjects().Single(p => p.Name == project).Doc);

    [Fact]
    public void Protocol_has_no_package_references() => Assert.Empty(RefsOf("A2A.Protocol"));

    [Fact]
    public void Security_Http_has_no_package_references() => Assert.Empty(RefsOf("A2A.Security.Http"));

    [Fact]
    public void Scanner_finds_projects() => Assert.NotEmpty(SrcProjects());

    [Fact]
    public void Public_cancellation_token_parameters_are_named_cancellationToken()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.GetFiles(src, "PublicAPI.*.txt", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        var offenders = files
            .SelectMany(f => File.ReadLines(f).Select(l => (File: Path.GetRelativePath(src, f), Line: l)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.Line, @"System\.Threading\.CancellationToken ct\b"))
            .Select(x => $"{x.File}: {x.Line}")
            .ToArray();
        Assert.True(offenders.Length == 0, $"{offenders.Length} public members name the token 'ct':\n{string.Join("\n", offenders)}");
    }

    private static void AssertOnlyAbstractions(string project, Func<string, bool> allowed) =>
        Assert.True(RefsOf(project).All(allowed), $"{project} references: {string.Join(", ", RefsOf(project))}");

    [Fact]
    public void Client_references_only_logging_abstractions() =>
        AssertOnlyAbstractions("A2A.Client", r => r == "Microsoft.Extensions.Logging.Abstractions");

    [Fact]
    public void Server_references_only_extensions_abstractions() =>
        AssertOnlyAbstractions("A2A.Server", r => r.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            && (r.EndsWith(".Abstractions", StringComparison.Ordinal) || r == "Microsoft.Extensions.Options"));

    [Fact]
    public void TestKit_references_no_test_framework()
    {
        var offenders = RefsOf("A2A.TestKit").Where(r =>
            r.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) || r.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase)
            || r.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) || r.StartsWith("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Empty(offenders);
    }

    private static bool IsExtensionsAbstraction(string r) =>
        r.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && r.EndsWith(".Abstractions", StringComparison.Ordinal);

    [Fact]
    public void Postgres_references_only_npgsql_and_extensions_abstractions() =>
        AssertOnlyAbstractions("A2A.Server.Postgres", r => r == "Npgsql" || IsExtensionsAbstraction(r));

    [Fact]
    public void Client_Postgres_references_only_npgsql() =>
        AssertOnlyAbstractions("A2A.Client.Postgres", r => r == "Npgsql");

    [Fact]
    public void Client_Postgres_does_not_reference_the_server()
    {
        var doc = SrcProjects().Single(p => p.Name == "A2A.Client.Postgres").Doc;
        var projects = doc.Descendants("ProjectReference").Select(e => (string?)e.Attribute("Include") ?? "").ToArray();
        Assert.All(projects, p => Assert.DoesNotContain("A2A.Server", p, StringComparison.Ordinal));
    }

    [Fact]
    public void Redis_references_only_stackexchange_redis_and_extensions_abstractions() =>
        AssertOnlyAbstractions("A2A.Server.Redis", r => r == "StackExchange.Redis" || IsExtensionsAbstraction(r));

    [Fact]
    public void No_InternalsVisibleTo_targets_a_src_assembly()
    {
        var srcNames = SrcProjects().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var offenders = SrcProjects().SelectMany(p => p.Doc.Descendants("InternalsVisibleTo")
                .Select(e => (Owner: p.Name, Friend: (string?)e.Attribute("Include") ?? "")))
            // The v0.3 compat family (A2A.V0_3Compat, A2A.Server.V0_3Compat, A2A.Client.V0_3Compat) is built on core internals on purpose.
            .Where(x => srcNames.Contains(x.Friend) && !x.Friend.Contains("V0_3Compat", StringComparison.Ordinal));
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_SerializeToElement_on_response_paths()
    {
        // JSON-RPC envelopes are written once (JsonRpcWriter); encoding a payload to a JsonElement first is a second encode.
        // No exceptions: neither folder uses it after Task 22.
        var offenders = new List<string>();
        foreach (var folder in new[] { "src/A2A.Server.AspNetCore", "src/A2A.Client/Transports" })
        {
            var dir = Path.Combine(RepoRoot(), folder);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Split(Path.DirectorySeparatorChar);
                if (rel[0] is "bin" or "obj") continue;
                if (File.ReadAllText(file).Contains("SerializeToElement", StringComparison.Ordinal)) offenders.Add(Path.GetRelativePath(RepoRoot(), file));
            }
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void Client_sources_do_not_read_ambient_http_context()
    {
        // A client must never pick up the caller's Authorization header from the host's HttpContext: that would forward it.
        var src = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();
        foreach (var project in Directory.GetDirectories(src, "A2A.Client*"))
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(project, file).Split(Path.DirectorySeparatorChar);
            if (rel.Contains("bin") || rel.Contains("obj")) continue;
            if (File.ReadAllText(file).Contains("IHttpContextAccessor", StringComparison.Ordinal)) offenders.Add(Path.GetRelativePath(RepoRoot(), file));
        }
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_unbounded_channels_in_src()
    {
        // Every per-task channel is bounded (spec D12): a slow reader or a fast agent cannot grow memory without limit.
        var src = Path.Combine(RepoRoot(), "src");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar);
            if (rel.Contains("bin") || rel.Contains("obj")) continue;
            if (File.ReadAllText(file).Contains("CreateUnbounded", StringComparison.Ordinal)) offenders.Add(Path.GetRelativePath(RepoRoot(), file));
        }
        Assert.Empty(offenders);
    }
}

/// <summary>Namespaces follow assemblies and folders.</summary>
public sealed class NamespaceRulesTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "A2A.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("A2A.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool Under(string ns, string root) =>
        ns == root || ns.StartsWith(root + ".", StringComparison.Ordinal);

    [Fact]
    public void Every_public_type_lives_in_a_namespace_owned_by_its_assembly()
    {
        System.Reflection.Assembly[] assemblies =
        [
            typeof(A2A.AgentCard).Assembly,
            typeof(A2A.Security.Http.GuardedJwksFetcher).Assembly,
            typeof(A2A.Client.A2AClient).Assembly,
            typeof(A2A.Client.A2AClientServiceCollectionExtensions).Assembly,
            typeof(A2A.Server.A2ARequestHandler).Assembly,
            typeof(A2A.Server.AspNetCore.A2AEndpointRouteBuilderExtensions).Assembly,
        ];
        var offenders = new List<string>();
        var inspected = 0;
        foreach (var asm in assemblies)
        {
            var name = asm.GetName().Name!;
            var root = name == "A2A.Protocol" ? "A2A" : name;
            foreach (var t in asm.GetExportedTypes())
            {
                inspected++;
                var ns = t.Namespace ?? "";
                var ok = Under(ns, root)
                    || (name == "A2A.Client.DependencyInjection" && ns == "A2A.Client");
                if (name == "A2A.Protocol" && (Under(ns, "A2A.Server") || Under(ns, "A2A.Client"))) ok = false;
                if (!ok) offenders.Add($"{name}: {t.FullName}");
            }
        }
        Assert.True(inspected > 0, "no exported types were inspected");
        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_source_file_namespace_matches_its_folder()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var nsRegex = new System.Text.RegularExpressions.Regex(@"^\s*namespace\s+([\w.]+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var projDir in Directory.GetDirectories(src))
        {
            var project = Path.GetFileName(projDir);
            // Shared holds the netstandard2.0 polyfills, which live in System namespaces on purpose.
            if (project == "Shared") continue;
            var root = project == "A2A.Protocol" ? "A2A" : project;
            foreach (var file in Directory.EnumerateFiles(projDir, "*.cs", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(projDir, file);
                var parts = rel.Split(Path.DirectorySeparatorChar);
                if (parts[0] is "bin" or "obj") continue;
                scanned++;
                var m = nsRegex.Match(File.ReadAllText(file));
                if (!m.Success) continue;
                string expected;
                if (parts.Length == 1 || (project == "A2A.Protocol" && parts[0] is "Models" or "Errors"))
                    expected = root;
                else
                    expected = root + "." + string.Join('.', parts[..^1]);
                // Client.DependencyInjection keeps the A2A.Client namespace so AddA2AClient needs no new using.
                if (project == "A2A.Client.DependencyInjection" && parts.Length == 1) expected = "A2A.Client";
                if (m.Groups[1].Value != expected) offenders.Add($"{project}/{rel}: {m.Groups[1].Value} (expected {expected})");
            }
        }
        Assert.True(scanned > 0, "no source files were scanned");
        Assert.Empty(offenders);
    }
}
