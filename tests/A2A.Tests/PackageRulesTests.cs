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
        doc.Descendants("PackageReference").Select(e => (string?)e.Attribute("Include") ?? "").ToArray();

    private static string[] RefsOf(string project) =>
        PackageRefs(SrcProjects().Single(p => p.Name == project).Doc);

    [Fact]
    public void Protocol_has_no_package_references() => Assert.Empty(RefsOf("A2A.Protocol"));

    [Fact]
    public void Security_Http_has_no_package_references() => Assert.Empty(RefsOf("A2A.Security.Http"));

    [Fact]
    public void Scanner_finds_projects() => Assert.NotEmpty(SrcProjects());

    private static void AssertOnlyAbstractions(string project, Func<string, bool> allowed) =>
        Assert.True(RefsOf(project).All(allowed), $"{project} references: {string.Join(", ", RefsOf(project))}");

    [Fact]
    public void Client_references_only_logging_abstractions() =>
        AssertOnlyAbstractions("A2A.Client", r => r == "Microsoft.Extensions.Logging.Abstractions");

    [Fact]
    public void Server_references_only_extensions_abstractions() =>
        AssertOnlyAbstractions("A2A.Server", r => r.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
            && (r.EndsWith(".Abstractions", StringComparison.Ordinal) || r == "Microsoft.Extensions.Options"));

    // Server still exposes internals to the ASP.NET Core binding; Task 16 (IA2ARequestHandler) removes it and this entry.
    private static readonly HashSet<(string Owner, string Friend)> KnownExceptions = [("A2A.Server", "A2A.Server.AspNetCore")];

    [Fact]
    public void No_InternalsVisibleTo_targets_a_src_assembly()
    {
        var srcNames = SrcProjects().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var offenders = SrcProjects().SelectMany(p => p.Doc.Descendants("InternalsVisibleTo")
                .Select(e => (Owner: p.Name, Friend: (string?)e.Attribute("Include") ?? "")))
            .Where(x => srcNames.Contains(x.Friend) && !KnownExceptions.Contains(x));
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
        Assert.NotEmpty(assemblies);
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
