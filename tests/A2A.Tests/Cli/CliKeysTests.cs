using System.Text.Json;
using A2A.Cli;

namespace A2A.Tests.Cli;

public sealed class CliKeysTests
{
    private static async Task<(int Code, string Out, string Err)> Run(string stdin, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var env = new CliEnvironment(stdout, stderr, new StringReader(stdin), _ => null);
        var code = await CliApp.RunAsync(args, env, CancellationToken.None);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task Keys_new_prints_a_key_and_its_hash_without_an_agent()
    {
        var (code, output, _) = await Run("", "keys", "new");
        Assert.Equal(0, code);
        var key = output.Split('\n').Single(l => l.StartsWith("key:", StringComparison.Ordinal))[4..].Trim();
        var hash = output.Split('\n').Single(l => l.StartsWith("hash:", StringComparison.Ordinal))[5..].Trim();
        Assert.True(A2AApiKey.IsWellFormed(key));
        Assert.Equal(A2AApiKey.ComputeHash(key), hash);
    }

    [Fact]
    public async Task Keys_new_json()
    {
        var (code, output, _) = await Run("", "keys", "new", "--json");
        Assert.Equal(0, code);
        using var doc = JsonDocument.Parse(output);
        var key = doc.RootElement.GetProperty("key").GetString()!;
        Assert.Equal(A2AApiKey.ComputeHash(key), doc.RootElement.GetProperty("hash").GetString());
    }

    [Fact]
    public async Task Keys_hash_reads_the_key_from_stdin()
    {
        var key = A2AApiKey.Create();
        var (code, output, _) = await Run(key.Key + "\n", "keys", "hash");
        Assert.Equal(0, code);
        Assert.Equal(key.Hash, output.Trim());
    }

    [Theory]
    [InlineData("not-a-key", "hash")]
    [InlineData("", "rotate")]
    public async Task Bad_input_is_a_usage_error(string stdin, string command)
    {
        var (code, _, err) = await Run(stdin, "keys", command);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.NotEmpty(err);
    }

    [Fact]
    public async Task General_help_lists_keys() =>
        Assert.Contains("keys", (await Run("", "--help")).Out, StringComparison.Ordinal);
}
