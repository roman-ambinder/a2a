using System.Text.Json;
using static A2A.Tests.Cli.CliTestSupport;

namespace A2A.Tests.Cli;

public sealed class CliDownloadTests
{
    private static string[] Names(string directory, bool files) =>
        (files ? Directory.GetFiles(directory) : Directory.GetFileSystemEntries(directory)).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray();

    private static async Task<string> TaskWithAsync(TestAgentHost host, string scenario)
    {
        var run = await Run(host, "send", "--json", scenario);
        Assert.Equal(0, run.Code);
        return TaskIdOf(run.Out);
    }

    [Fact]
    public async Task Happy_path_saves_the_file_after_checking_sha256_and_skips_other_parts()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-ok");

        var run = await Run(host, "download", "--output", dir.Path, "--json", "--", id);
        Assert.Equal(0, run.Code);
        using var doc = JsonDocument.Parse(run.Out);
        Assert.Equal(1, doc.RootElement.GetProperty("skipped").GetInt32());
        var file = Assert.Single(doc.RootElement.GetProperty("files").EnumerateArray());
        Assert.Equal("art1", file.GetProperty("artifactId").GetString());
        Assert.Equal(FileAgent.Content.Length, file.GetProperty("bytes").GetInt64());
        var path = file.GetProperty("path").GetString()!;
        Assert.Equal(Path.Combine(dir.Path, "report.txt"), path);
        Assert.Equal(FileAgent.Content, await File.ReadAllBytesAsync(path));
        Assert.DoesNotContain(".tmp", string.Join(",", Directory.GetFiles(dir.Path)));
        Assert.DoesNotContain("/files/", run.All);
    }

    [Fact]
    public async Task Text_output_never_shows_the_part_url()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-ok");

        var get = await Run(host, "get", "--", id);
        Assert.Contains("[file] report.txt", get.Out);
        Assert.DoesNotContain("/files/", get.Out);

        var run = await Run(host, "download", "-o", dir.Path, "--", id);
        Assert.Equal(0, run.Code);
        Assert.Contains("Saved", run.Out);
        Assert.DoesNotContain("/files/", run.All);
    }

    [Fact]
    public async Task Sha256_mismatch_exits_8_and_leaves_no_file()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-bad-hash");

        var run = await Run(host, "download", "-o", dir.Path, "--", id);
        Assert.Equal(8, run.Code);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Too_large_a_file_exits_8()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-ok");

        var run = await Run(host, "download", "-o", dir.Path, "--max-bytes", "5", "--", id);
        Assert.Equal(8, run.Code);
        Assert.Empty(Directory.GetFileSystemEntries(dir.Path));
    }

    [Fact]
    public async Task Path_traversal_in_file_names_stays_inside_the_output_folder()
    {
        using var dir = new TempDir();
        var output = dir.Sub("out");
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-evil");

        var run = await Run(host, "download", "-o", output, "--", id);
        Assert.Equal(0, run.Code);
        Assert.Equal(["evil.txt", "evil2.txt", "passwd"], Names(output, files: true));
        // Nothing was written next to or above the output folder.
        Assert.Equal(["out"], Names(dir.Path, files: false));
    }

    [Fact]
    public async Task Same_name_twice_gets_a_suffix_and_a_missing_name_uses_the_artifact_id()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-dup");

        var run = await Run(host, "download", "-o", dir.Path, "--", id);
        Assert.Equal(0, run.Code);
        Assert.Equal(["art1-2", "same-2.txt", "same.txt"], Names(dir.Path, files: true));
    }

    [Fact]
    public async Task Existing_file_without_force_exits_1_before_downloading_and_force_replaces_it()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-ok");
        var target = Path.Combine(dir.Path, "report.txt");
        await File.WriteAllTextAsync(target, "old");

        var refused = await Run(host, "download", "-o", dir.Path, "--", id);
        Assert.Equal(1, refused.Code);
        Assert.Contains("--force", refused.Error);
        Assert.Equal("old", await File.ReadAllTextAsync(target));

        var forced = await Run(host, "download", "-o", dir.Path, "--force", "--", id);
        Assert.Equal(0, forced.Code);
        Assert.Equal(FileAgent.Content, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task No_file_parts_exits_5()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-none");

        var run = await Run(host, "download", "-o", dir.Path, "--", id);
        Assert.Equal(5, run.Code);
        Assert.Contains("No file parts", run.Error);
    }

    [Fact]
    public async Task Unknown_artifact_id_exits_5()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        var id = await TaskWithAsync(host, "files-ok");

        var run = await Run(host, "download", "-o", dir.Path, "--artifact", "nope", "--", id);
        Assert.Equal(5, run.Code);
        var named = await Run(host, "download", "-o", dir.Path, "--artifact", "art1", "--", id);
        Assert.Equal(0, named.Code);
    }

    [Fact]
    public async Task Unknown_task_exits_5()
    {
        using var dir = new TempDir();
        await using var host = await StartAsync(pipeline: ServeFiles, files: true);
        Assert.Equal(5, (await Run(host, "download", "no-such-task", "-o", dir.Path)).Code);
    }
}
