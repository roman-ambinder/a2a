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

    [Fact(Skip = "Task 6, Task 8")]
    public void Client_and_Server_reference_only_extensions_abstractions()
    {
        foreach (var project in new[] { "A2A.Client", "A2A.Server" })
        {
            var bad = RefsOf(project).Where(r =>
                !(r.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                  && (r.EndsWith(".Abstractions", StringComparison.Ordinal) || r == "Microsoft.Extensions.Options")));
            Assert.Empty(bad);
        }
    }

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
