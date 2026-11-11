using System.Xml.Linq;

namespace A2A.Tests;

/// <summary>
///     Every package under <c>src/</c> is used by at least one runnable sample under <c>samples/</c>
///     (see docs/superpowers/specs/2026-11-10-samples-single-home-design.md, D5).
/// </summary>
/// <remarks>
///     A package counts as used when a sample project references it, directly or through other <c>src/</c> projects, or
///     when a sample script runs it with <c>--project src/&lt;Name&gt;</c>. <c>samples/Snippets</c> does not count: it
///     only compiles the guide code and runs nothing.
/// </remarks>
public sealed class SamplesCoverageTests
{
    /// <summary>Packages that have no runnable sample yet, and the sample that will cover each one.</summary>
    private static readonly Dictionary<string, string> NotYetCovered = new(StringComparer.Ordinal)
    {
        // Empty: every package has one. A new package that has no sample yet goes here, with the sample planned for it.
    };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "A2A.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("A2A.slnx not found above " + AppContext.BaseDirectory);
    }

    private static bool IsBuildOutput(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) is var parts
        && (parts.Contains("bin") || parts.Contains("obj"));

    /// <summary>
    ///     A template pack (and the template projects inside it) has no API a sample could reference. Its check is
    ///     <c>src/A2A.Templates/test-templates.sh</c>, which builds and runs every template against the packed SDK.
    /// </summary>
    private static bool IsTemplatePack(string project) =>
        project.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("A2A.Templates");

    private static IEnumerable<string> ProjectReferences(string project)
    {
        var dir = Path.GetDirectoryName(project)!;
        return XDocument.Load(project).Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrEmpty(include))
            .Select(include => Path.GetFullPath(Path.Combine(dir, include!.Replace('\\', Path.DirectorySeparatorChar))));
    }

    /// <summary>Names of the <c>src/</c> packages the runnable samples use.</summary>
    private static HashSet<string> UsedPackages(string root)
    {
        var samples = Path.Combine(root, "samples");
        var snippets = Path.Combine(samples, "Snippets") + Path.DirectorySeparatorChar;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(Directory.EnumerateFiles(samples, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p) && !p.StartsWith(snippets, StringComparison.Ordinal)));
        while (pending.TryPop(out var project))
            foreach (var reference in ProjectReferences(project))
                if (seen.Add(reference))
                    pending.Push(reference);

        var used = seen.Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var script in Directory.EnumerateFiles(samples, "*.sh", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(script);
            foreach (var package in SourcePackages(root))
                if (text.Contains($"--project src/{package}", StringComparison.Ordinal))
                    used.Add(package);
        }

        return used;
    }

    private static List<string> SourcePackages(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsBuildOutput(p) && !IsTemplatePack(p))
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Order(StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Every_package_has_a_runnable_sample()
    {
        var root = RepoRoot();
        var used = UsedPackages(root);
        var missing = SourcePackages(root).Where(p => !used.Contains(p) && !NotYetCovered.ContainsKey(p)).ToList();
        Assert.True(missing.Count == 0,
            $"No runnable sample under samples/ uses: {string.Join(", ", missing)}. Add a sample that references it.");
    }

    [Fact]
    public void Not_yet_covered_list_has_no_stale_entries()
    {
        var root = RepoRoot();
        var used = UsedPackages(root);
        var packages = SourcePackages(root);
        var stale = NotYetCovered.Keys.Where(p => used.Contains(p) || !packages.Contains(p)).Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(stale.Count == 0,
            $"Remove from {nameof(NotYetCovered)} (now used by a sample, or no longer in src/): {string.Join(", ", stale)}.");
    }
}
