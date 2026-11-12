namespace A2A.Tests;

/// <summary>The samples stay small: the A2A surface a hello-world agent and client touch is a handful of lines.</summary>
public sealed class HelloWorldGateTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "A2A.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("A2A.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Lines that contain <c>A2A</c> (case-sensitive), not counting comment-only lines and <c>using</c> lines.</summary>
    private static int A2ALines(string sample) =>
        File.ReadAllLines(Path.Combine(RepoRoot(), "samples", sample, "Program.cs"))
            .Select(l => l.Trim())
            .Count(l => l.Contains("A2A", StringComparison.Ordinal) && !l.StartsWith("//", StringComparison.Ordinal)
                && !l.StartsWith("using ", StringComparison.Ordinal));

    [Theory]
    [InlineData("EchoAgent", 2)]
    [InlineData("EchoClient", 4)]
    public void Sample_a2a_lines_within_budget(string sample, int budget) =>
        Assert.True(A2ALines(sample) <= budget, $"{sample} has {A2ALines(sample)} A2A lines; the budget is {budget}.");
}
