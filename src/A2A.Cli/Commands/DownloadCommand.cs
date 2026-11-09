using System.Net;
using A2A.Client;
using A2A.Security.Http;

namespace A2A.Cli.Commands;

/// <summary><c>a2a download</c>: save the files behind the URL parts of a task's artifacts.</summary>
internal static class DownloadCommand
{
    private const long DefaultMaxBytes = 1L << 30;

    private sealed record Job(string ArtifactId, UrlPart Part, string Path);

    public static async Task<int> RunAsync(CommandContext c, CancellationToken cancellationToken)
    {
        var args = c.Args;
        var maxBytes = args.Long("max-bytes", 1) ?? DefaultMaxBytes;
        var force = args.Has("force");
        var wanted = args.Value("artifact");

        var task = await c.Client.GetTaskAsync(new GetTaskRequest { Id = args.Positionals[0], HistoryLength = 0 }, c.RequestOptions(), cancellationToken).ConfigureAwait(false);

        var artifacts = (task.Artifacts ?? []).Where(a => wanted is null || string.Equals(a.ArtifactId, wanted, StringComparison.Ordinal)).ToList();
        if (wanted is not null && artifacts.Count == 0)
            throw new CliException(ExitCodes.NotFound, "The task has no artifact with that id.");

        var directory = Path.GetFullPath(args.Value("output") ?? Directory.GetCurrentDirectory());
        var jobs = new List<Job>();
        var skipped = 0;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artifact in artifacts)
        {
            for (var i = 0; i < artifact.Parts.Count; i++)
            {
                if (artifact.Parts[i] is not UrlPart part) { skipped++; continue; }
                var name = FileNames.Unique(FileNames.Sanitize(part.Filename, $"{artifact.ArtifactId}-{i}"), used);
                jobs.Add(new Job(artifact.ArtifactId, part, Resolve(directory, name)));
            }
        }
        if (jobs.Count == 0)
            throw new CliException(ExitCodes.NotFound, "No file parts to download were found in the task's artifacts.");

        // Check every target first, so a run that cannot finish does not start.
        if (!force)
        {
            foreach (var job in jobs)
                if (File.Exists(job.Path) || Directory.Exists(job.Path))
                    throw new CliException(ExitCodes.Error, $"The file already exists: {c.Out.One(System.IO.Path.GetFileName(job.Path), 220)}. Use --force to replace it.");
        }

        Directory.CreateDirectory(directory);
        var storage = new SafeHttpOptions();
        if (args.Has("allow-private-storage")) { storage.AllowPrivateNetworks = true; storage.AllowHttp = true; }
        var downloader = new A2AArtifactDownloader(c.Connection.AgentHandler!, c.Connection.Binding!.CardOrigin, storage);

        using var overall = c.OverallTimeout(cancellationToken);
        var saved = new List<(Job Job, long Bytes)>();
        foreach (var job in jobs)
        {
            try
            {
                await SaveAsync(downloader, job, directory, maxBytes, force, overall.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or BlockedAddressException
                || (ex is HttpRequestException { StatusCode: null, HttpRequestError: HttpRequestError.Unknown }))
            {
                // The downloader's origin, redirect and address checks.
                throw new CliException(ExitCodes.Integrity, ex.Message);
            }
            saved.Add((job, new FileInfo(job.Path).Length));
            if (!c.Out.Json) c.Out.Line($"Saved {c.Out.One(job.Path, 500)} ({saved[^1].Bytes} bytes)");
        }

        if (c.Out.Json)
        {
            c.Out.Line(Render.Json(w =>
            {
                w.WriteStartObject();
                w.WriteStartArray("files");
                foreach (var (job, bytes) in saved)
                {
                    w.WriteStartObject();
                    w.WriteString("path", job.Path);
                    w.WriteString("artifactId", job.ArtifactId);
                    w.WriteNumber("bytes", bytes);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteNumber("skipped", skipped);
                w.WriteEndObject();
            }));
        }
        else
        {
            c.Out.Line($"{saved.Count} file(s) saved, {skipped} part(s) skipped (not files).");
        }
        return ExitCodes.Success;
    }

    /// <summary>The full path of <paramref name="name"/> in <paramref name="directory"/>. Refuses a path that leaves the directory.</summary>
    private static string Resolve(string directory, string name)
    {
        var full = Path.GetFullPath(Path.Combine(directory, name));
        if (!string.Equals(Path.GetDirectoryName(full)?.TrimEnd(Path.DirectorySeparatorChar), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal))
            throw new CliException(ExitCodes.Error, "The artifact file name does not stay inside the output folder.");
        return full;
    }

    private static async Task SaveAsync(A2AArtifactDownloader downloader, Job job, string directory, long maxBytes, bool force, CancellationToken cancellationToken)
    {
        // Write to a temporary file next to the target and move it into place only when the whole download passed its checks.
        var temp = Path.Combine(directory, $".a2a-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await downloader.DownloadAsync(job.Part, stream, maxBytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, job.Path, overwrite: force);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { /* Nothing more to do; the error that matters is reported. */ }
            catch (UnauthorizedAccessException) { /* Same. */ }
        }
    }
}
