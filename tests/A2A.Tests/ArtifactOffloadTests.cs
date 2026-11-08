using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using A2A.Client;
using A2A.Integrity;
using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests;

/// <summary>
/// Large raw parts go to the object store from <see cref="TaskUpdater"/>, before the artifact hash, so clients see ordinary
/// <c>url</c> parts. Appended chunks, text and small parts stay inline.
/// </summary>
public sealed class ArtifactOffloadTests
{
    private const int Threshold = 16;
    private static readonly Uri Base = new("https://agent.test/a2a");

    /// <summary>An in-memory store that counts writes and can be made to fail.</summary>
    private sealed class SpyStore : ArtifactObjectStore
    {
        public InMemoryArtifactObjectStore Inner { get; } = new();
        public int Puts;
        public Exception? Fail { get; set; }

        public override ValueTask<ArtifactObjectWriteResult> PutAsync(string key, Stream content, ArtifactObjectInfo info, long maxBytes, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Puts);
            if (Fail is not null) throw Fail;
            return Inner.PutAsync(key, content, info, maxBytes, cancellationToken);
        }

        public override ValueTask<ArtifactObjectContent?> OpenReadAsync(string key, ArtifactByteRange? range = null, CancellationToken cancellationToken = default) =>
            Inner.OpenReadAsync(key, range, cancellationToken);

        public override ValueTask DeleteAsync(string key, CancellationToken cancellationToken = default) => Inner.DeleteAsync(key, cancellationToken);

        public override IAsyncEnumerable<ArtifactObjectEntry> ListAsync(string prefix, CancellationToken cancellationToken = default) =>
            Inner.ListAsync(prefix, cancellationToken);
    }

    private sealed class Rig
    {
        public required TaskUpdater Updater { get; init; }
        public required Channel<StreamResponse> Events { get; init; }
        public required SpyStore Store { get; init; }
        public required ArtifactStorageOptions Options { get; init; }

        public Artifact Delivered()
        {
            Assert.True(Events.Reader.TryRead(out var evt), "no event was written");
            return Assert.IsType<ArtifactUpdateEvent>(evt).ArtifactUpdate.Artifact;
        }
    }

    private static Rig NewRig(string tenant = "", bool withStore = true, Action<ArtifactStorageOptions>? configure = null)
    {
        var options = new ArtifactStorageOptions { InlineThresholdBytes = Threshold, PublicBaseUrl = Base };
        configure?.Invoke(options);
        var store = new SpyStore();
        var events = Channel.CreateUnbounded<StreamResponse>();
        var offload = withStore ? new ArtifactOffload(store, options, tenant, "t1") : null;
        return new Rig { Updater = new TaskUpdater("t1", "c1", true, events.Writer, null, offload), Events = events, Store = store, Options = options };
    }

    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();

    private static long SizeOf(Part part) => part.Metadata!["sizeBytes"].GetInt64();

    [Fact]
    public async Task Raw_part_over_threshold_becomes_url_part()
    {
        var rig = NewRig();
        var bytes = Bytes(Threshold + 1);

        await rig.Updater.AddArtifactAsync([Part.FromBytes(bytes, "application/pdf", "a.pdf")], artifactId: "a");

        var part = Assert.IsType<UrlPart>(Assert.Single(rig.Delivered().Parts));
        Assert.Equal("application/pdf", part.MediaType);
        Assert.Equal("a.pdf", part.Filename);
        Assert.Equal(bytes.Length, SizeOf(part));
        Assert.Equal(ArtifactHash.Format(SHA256.HashData(bytes)), part.Metadata!["sha256"].GetString());
        Assert.True(ArtifactLink.TryGetObjectId(part.Url, out var objectId));
        await using var stored = await rig.Store.OpenReadAsync(ArtifactObjectKey.Create("", "t1", objectId!));
        Assert.NotNull(stored);
        Assert.Equal("application/pdf", stored.ContentType);
        Assert.Equal(bytes.Length, stored.TotalLength);
    }

    [Fact]
    public async Task Raw_part_at_threshold_stays_inline()
    {
        var rig = NewRig();

        await rig.Updater.AddArtifactAsync([Part.FromBytes(Bytes(Threshold), "application/octet-stream")]);

        Assert.IsType<RawPart>(Assert.Single(rig.Delivered().Parts));
        Assert.Equal(0, rig.Store.Puts);
    }

    [Fact]
    public async Task Text_part_stays_inline()
    {
        var rig = NewRig();

        await rig.Updater.AddArtifactAsync([Part.FromText(new string('t', Threshold * 10))]);

        Assert.IsType<TextPart>(Assert.Single(rig.Delivered().Parts));
        Assert.Equal(0, rig.Store.Puts);
    }

    [Fact]
    public async Task Append_is_never_offloaded()
    {
        var rig = NewRig();

        await rig.Updater.AddArtifactAsync([Part.FromBytes(Bytes(Threshold * 4), "application/octet-stream")], artifactId: "a", append: true);

        Assert.IsType<RawPart>(Assert.Single(rig.Delivered().Parts));
        Assert.Equal(0, rig.Store.Puts);
    }

    [Fact]
    public async Task Hashed_artifact_verifies_end_to_end()
    {
        var rig = NewRig();

        await rig.Updater.AddHashedArtifactAsync([Part.FromText("head "), Part.FromBytes(Bytes(Threshold * 3), "application/octet-stream")],
            artifactId: "a", lastChunk: true);

        var artifact = rig.Delivered();
        Assert.IsType<UrlPart>(artifact.Parts[1]);
        Assert.Equal(ArtifactVerificationStatus.Verified, ArtifactVerifier.Verify(artifact).Status);
    }

    [Fact]
    public async Task Artifact_overload_offloads_too()
    {
        var rig = NewRig();

        await rig.Updater.AddArtifactAsync(new Artifact { ArtifactId = "a", Parts = [Part.FromBytes(Bytes(Threshold + 1), "image/png")] });

        Assert.IsType<UrlPart>(Assert.Single(rig.Delivered().Parts));
    }

    [Fact]
    public async Task Stream_method_exceeds_MaxEventBytes()
    {
        const int size = 5 * 1024 * 1024; // MaxEventBytes is 4 MiB
        var store = new InMemoryArtifactObjectStore();
        var agent = new DelegateAgentExecutor(async (_, updater, ct) =>
        {
            using var content = new MemoryStream(Bytes(size));
            await updater.StartWorkAsync(cancellationToken: ct);
            await updater.AddArtifactFromStreamAsync(content, "application/pdf", artifactId: "report", name: "report.pdf", cancellationToken: ct);
            await updater.CompleteAsync(cancellationToken: ct);
        });
        await using var host = await TestAgentHost.StartAsync(o => o.ArtifactStorage.PublicBaseUrl = Base,
            services: s => s.AddSingleton<ArtifactObjectStore>(store).AddSingleton<IAgentExecutorFactory>(new FixedFactory(agent)));

        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("go")).RequireTask();
        await host.Handler.WhenIdleAsync();

        var stored = (await host.App.Services.GetRequiredService<TaskStore>().GetAsync(new TaskKey("", "alice", task.Id)))!.Task;
        Assert.Equal(TaskState.Completed, stored.Status.State);
        var artifact = Assert.Single(stored.Artifacts!);
        var part = Assert.IsType<UrlPart>(Assert.Single(artifact.Parts));
        Assert.Equal(size, SizeOf(part));
        Assert.True(ArtifactSeal.IsSealed(artifact));
        Assert.True(artifact.Metadata!.ContainsKey("sha256"));
        Assert.Equal(ArtifactVerificationStatus.Verified, ArtifactVerifier.Verify(artifact).Status);
    }

    [Fact]
    public async Task Stream_over_MaxObjectBytes_throws_to_agent()
    {
        var rig = NewRig(configure: o => o.MaxObjectBytes = 10);
        using var content = new MemoryStream(Bytes(11));

        await Assert.ThrowsAsync<ArtifactObjectTooLargeException>(async () =>
            await rig.Updater.AddArtifactFromStreamAsync(content, "application/octet-stream"));

        Assert.False(rig.Events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Stream_method_without_store_throws_InvalidOperation()
    {
        var rig = NewRig(withStore: false);
        using var content = new MemoryStream(Bytes(4));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Updater.AddArtifactFromStreamAsync(content, "text/plain"));

        Assert.Contains("ArtifactObjectStore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Upload_failure_reaches_the_agent()
    {
        var rig = NewRig();
        rig.Store.Fail = new IOException("disk full");

        var ex = await Assert.ThrowsAsync<IOException>(async () =>
            await rig.Updater.AddArtifactAsync([Part.FromBytes(Bytes(Threshold + 1), "application/octet-stream")]));

        Assert.Equal("disk full", ex.Message);
        Assert.False(rig.Events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task No_upload_when_updater_is_terminal()
    {
        var rig = NewRig();
        await rig.Updater.CompleteAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Updater.AddArtifactAsync([Part.FromBytes(Bytes(Threshold + 1), "application/octet-stream")]));
        using var content = new MemoryStream(Bytes(4));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await rig.Updater.AddArtifactFromStreamAsync(content, "text/plain"));

        Assert.Equal(0, rig.Store.Puts);
    }

    [Fact]
    public async Task No_store_means_large_part_stays_inline()
    {
        var rig = NewRig(withStore: false);

        await rig.Updater.AddArtifactAsync([Part.FromBytes(Bytes(Threshold * 10), "application/octet-stream")]);

        Assert.IsType<RawPart>(Assert.Single(rig.Delivered().Parts));
    }

    [Fact]
    public async Task Link_contains_tenant_and_uses_resolver_when_set()
    {
        var rig = NewRig(tenant: "acme", configure: o => o.PublicBaseUrlResolver = t => new Uri($"https://{t}.example.test/x/"));

        await rig.Updater.AddArtifactAsync([Part.FromBytes(Bytes(Threshold + 1), "application/octet-stream")]);

        var url = Assert.IsType<UrlPart>(Assert.Single(rig.Delivered().Parts)).Url;
        Assert.StartsWith("https://acme.example.test/x/artifacts/acme/t1/", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.True(ArtifactLink.TryGetObjectId(url, out _));
    }

    [Fact]
    public async Task Default_tenant_segment_is_underscore()
    {
        var rig = NewRig();

        await rig.Updater.AddArtifactAsync([Part.FromBytes(Bytes(Threshold + 1), "application/octet-stream")]);

        var url = Assert.IsType<UrlPart>(Assert.Single(rig.Delivered().Parts)).Url;
        Assert.StartsWith("https://agent.test/a2a/artifacts/_/t1/", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_round_trips_and_encodes_ids()
    {
        var id = ArtifactObjectKey.NewObjectId();
        var url = ArtifactLink.Create(Base, "a/b", "t 1", id);

        Assert.Equal($"https://agent.test/a2a/artifacts/a%2Fb/t%201/{id}", url.AbsoluteUri);
        Assert.True(ArtifactLink.TryGetObjectId(url, out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(ArtifactLink.TryGetObjectId(new Uri("https://agent.test/a2a/artifacts/x"), out _));
        Assert.False(ArtifactLink.TryGetObjectId(new Uri($"https://agent.test/other/x/y/{id}"), out _));
    }

    [Fact]
    public void Link_refuses_ids_a_url_parser_would_remove()
    {
        var id = ArtifactObjectKey.NewObjectId();

        Assert.Throws<ArgumentException>(() => ArtifactLink.Create(Base, "..", "t", id));
        Assert.Throws<ArgumentException>(() => ArtifactLink.Create(Base, "", ".", id));
        Assert.StartsWith("https://agent.test/a2a/artifacts/_/a.b/", ArtifactLink.Create(Base, "", "a.b", id).AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_without_PublicBaseUrl_fails_at_startup()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<ArtifactObjectStore>(new InMemoryArtifactObjectStore()));
            _ = host.Engine;
        });
    }

    private sealed class DurableStub : DelegatingTaskStore;

    private sealed class CaptureLogger : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(CaptureLogger owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (owner.Entries) owner.Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    [Fact]
    public async Task Durable_task_store_with_in_memory_objects_warns()
    {
        var logs = new CaptureLogger();
        await using var host = await TestAgentHost.StartAsync(o => o.ArtifactStorage.PublicBaseUrl = Base, services: s =>
        {
            s.AddTaskStore(new DurableStub());
            s.AddSingleton<ArtifactObjectStore>(new InMemoryArtifactObjectStore());
            s.AddLogging(b => b.AddProvider(logs));
        });

        lock (logs.Entries)
        {
            var entry = Assert.Single(logs.Entries, e => e.Message.Contains("InMemoryArtifactObjectStore", StringComparison.Ordinal));
            Assert.Equal(LogLevel.Warning, entry.Level);
        }
    }

    private sealed class FixedFactory(IAgentExecutor executor) : IAgentExecutorFactory
    {
        public ValueTask<AgentExecutorLease> RentAsync(string tenant, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentExecutorLease(executor, null));
    }
}
