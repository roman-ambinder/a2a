using System.Security.Cryptography;
using System.Text.Json;
using A2A.Cli;
using A2A.Integrity;
using A2A.Server;
using A2A.Server.Execution;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Cli;

/// <summary>The result of one in-process run of the tool.</summary>
internal sealed record CliRun(int Code, string Out, string Error)
{
    public string All => Out + "\n" + Error;

    /// <summary>The output with control characters shown as escapes, for assertion messages.</summary>
    public string Shown => string.Concat(All.Select(c => char.IsControl(c) && c != '\n' || c is '\u202E' or '\u202C' ? $"<U+{(int)c:X4}>" : c.ToString()));
}

/// <summary>A writer that tells a test when the first line was written.</summary>
internal sealed class SignalWriter : StringWriter
{
    public TaskCompletionSource FirstLine { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override void WriteLine(string? value)
    {
        base.WriteLine(value);
        FirstLine.TrySetResult();
    }
}

/// <summary>A temporary folder that is removed when the test ends.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("a2acli").FullName;

    public string Sub(string name) => Directory.CreateDirectory(System.IO.Path.Combine(Path, name)).FullName;

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { /* The temp folder is cleaned by the OS later. */ }
    }
}

/// <summary>Test agent that offers files: the part URLs point at the <c>/files</c> route that <see cref="CliTestSupport.ServeFiles"/> adds.</summary>
internal sealed class FileAgent : IAgentExecutor
{
    public static readonly byte[] Content = "hello from the artifact"u8.ToArray();

    public static string Hash(byte[] bytes) => ArtifactHash.Format(SHA256.HashData(bytes));

    private static UrlPart File(string? filename, string? sha = null) =>
        Part.FromUrl(new Uri("http://localhost/files/" + Guid.NewGuid().ToString("N")), "text/plain", filename) with
        {
            Metadata = new() { ["sha256"] = JsonSerializer.SerializeToElement(sha ?? Hash(Content)) },
        };

    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        var text = context.Message.GetText();
        List<Part> parts = text switch
        {
            "files-ok" => [File("report.txt"), Part.FromText("a note")],
            "files-bad-hash" => [File("bad.txt", Hash("something else"u8.ToArray()))],
            "files-evil" => [File("../../evil.txt"), File("..\\..\\evil2.txt"), File("/etc/passwd")],
            "files-dup" => [File("same.txt"), File("same.txt"), File(null)],
            "files-none" => [Part.FromText("only text")],
            _ => [],
        };
        if (parts.Count == 0)
        {
            await updater.CompleteAsync(text, cancellationToken);
            return;
        }
        await updater.AddArtifactAsync(new Artifact { ArtifactId = "art1", Name = "result", Parts = parts }, lastChunk: true, cancellationToken: cancellationToken);
        await updater.CompleteAsync("done", cancellationToken);
    }
}

internal static class CliTestSupport
{
    public const string Agent = "http://localhost/";

    /// <summary>Starts the test agent. Anonymous access is on unless <paramref name="requireAuth"/> is true.</summary>
    public static Task<TestAgentHost> StartAsync(AgentCard? card = null, bool requireAuth = false, Action<WebApplication>? pipeline = null, bool files = false) =>
        TestAgentHost.StartAsync(
            o =>
            {
                o.AllowAnonymous = !requireAuth;
                if (card is not null) o.AgentCard = card;
            },
            requireAuth: requireAuth,
            configurePipeline: pipeline,
            services: files ? s => s.Replace(ServiceDescriptor.Scoped<IAgentExecutor, FileAgent>()) : null);

    /// <summary>Serves <c>/files/*</c> with <see cref="FileAgent.Content"/>.</summary>
    public static void ServeFiles(WebApplication app) =>
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/files")) { await next(ctx); return; }
            ctx.Response.ContentType = "application/octet-stream";
            await ctx.Response.Body.WriteAsync(FileAgent.Content);
        });

    public static AgentCard SecuredCard(SecurityScheme scheme)
    {
        var card = TestAgentHost.DefaultCard();
        card.SecuritySchemes = new() { ["s"] = scheme };
        card.SecurityRequirements = [new SecurityRequirement { Schemes = { ["s"] = new StringList() } }];
        return card;
    }

    /// <summary>Runs the tool against <paramref name="host"/>. <c>--agent</c> is added unless <paramref name="addAgent"/> is false.</summary>
    public static async Task<CliRun> RunAsync(TestAgentHost host, string[] args, Dictionary<string, string?>? env = null, string stdin = "",
        CancellationToken cancellationToken = default, TextWriter? output = null, bool addAgent = true)
    {
        var all = addAgent ? [args[0], "--agent", Agent, .. args[1..]] : args;
        var stdout = output ?? new StringWriter();
        var stderr = new StringWriter();
        var environment = new CliEnvironment(stdout, stderr, new StringReader(stdin), name => env is not null && env.TryGetValue(name, out var v) ? v : null)
        {
            Handler = host.App.GetTestServer().CreateHandler(),
        };
        var code = await CliApp.RunAsync(all, environment, cancellationToken).WaitAsync(TimeSpan.FromSeconds(60));
        return new CliRun(code, stdout.ToString()!, stderr.ToString());
    }

    public static Task<CliRun> Run(TestAgentHost host, params string[] args) => RunAsync(host, args);

    /// <summary>The task id of a <c>send --json</c> result.</summary>
    public static string TaskIdOf(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("task").GetProperty("id").GetString()!;
    }
}
