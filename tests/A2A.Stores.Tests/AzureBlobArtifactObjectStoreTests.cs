using System.Security.Cryptography;
using A2A.Integrity;
using A2A.Server.Storage;
using A2A.Server.Storage.AzureBlob;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Stores.Tests;

[Collection(ContainersCollection.Name)]
public sealed class AzureBlobArtifactObjectStoreTests(AzuriteFixture azurite) : ArtifactObjectStoreContract
{
    private static readonly ArtifactObjectInfo Info = new("text/html", "page.html");

    private async Task<(AzureBlobArtifactObjectStore Store, BlobContainerClient Container)> NewAsync(string prefix = "")
    {
        var container = await azurite.NewContainerAsync();
        return (new AzureBlobArtifactObjectStore(new AzureBlobArtifactStoreOptions { KeyPrefix = prefix, ContainerClient = container }), container);
    }

    protected override async Task<ArtifactObjectStore> CreateStoreAsync() => (await NewAsync()).Store;

    private static byte[] Bytes(int length)
    {
        var b = new byte[length];
        new Random(7).NextBytes(b);
        return b;
    }

    private static string NewKey() => ArtifactObjectKey.Create("t", "task", ArtifactObjectKey.NewObjectId());

    [Fact]
    public async Task Presigned_url_downloads_the_object()
    {
        var (store, _) = await NewAsync();
        var key = NewKey();
        var data = Bytes(1_000);
        await store.PutAsync(key, new MemoryStream(data), Info, 10_000);

        var url = await store.TryCreateDownloadUrlAsync(key, TimeSpan.FromMinutes(1));

        Assert.NotNull(url);
        using var http = new HttpClient();
        Assert.Equal(data, await http.GetByteArrayAsync(url));
    }

    [Fact]
    public async Task Presigned_url_forces_attachment()
    {
        var (store, _) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(100)), Info, 10_000);

        var url = await store.TryCreateDownloadUrlAsync(key, TimeSpan.FromMinutes(1));

        using var http = new HttpClient();
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task A_client_that_cannot_sign_gets_no_download_url()
    {
        var (_, container) = await NewAsync();
        // A container URI with a SAS query has no account key, so it cannot sign.
        var unsigned = new BlobContainerClient(new Uri(container.Uri + "?sv=2023-11-03&sig=x"));
        var store = new AzureBlobArtifactObjectStore(new AzureBlobArtifactStoreOptions { ContainerClient = unsigned });

        Assert.Null(await store.TryCreateDownloadUrlAsync(NewKey(), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Large_object_uploads_in_several_blocks()
    {
        var (store, _) = await NewAsync();
        var key = NewKey();
        var data = Bytes(20 * 1024 * 1024 + 123); // two full 8 MiB blocks and a short last block

        var result = await store.PutAsync(key, new MemoryStream(data), Info, long.MaxValue);

        Assert.Equal(data.Length, result.SizeBytes);
        Assert.Equal(ArtifactHash.Format(SHA256.HashData(data)), result.Sha256);
        var content = await store.OpenReadAsync(key, new ArtifactByteRange(8 * 1024 * 1024 - 5, 10));
        Assert.Equal(data.AsSpan(8 * 1024 * 1024 - 5, 10).ToArray(), await ReadAll(content!));
    }

    [Fact]
    public async Task Object_of_exactly_one_block_works()
    {
        var (store, _) = await NewAsync();
        var key = NewKey();
        var data = Bytes(8 * 1024 * 1024);

        var result = await store.PutAsync(key, new MemoryStream(data), Info, long.MaxValue);

        Assert.Equal(data.Length, result.SizeBytes);
        Assert.Equal(data, await ReadAll((await store.OpenReadAsync(key))!));
    }

    [Fact]
    public async Task Failed_large_upload_leaves_no_visible_blob()
    {
        var (store, container) = await NewAsync();
        var key = NewKey();

        await Assert.ThrowsAsync<ArtifactObjectTooLargeException>(async () =>
            await store.PutAsync(key, new MemoryStream(Bytes(17 * 1024 * 1024)), Info, 12 * 1024 * 1024));

        Assert.False(await store.ExistsAsync(key));
        Assert.Null(await store.OpenReadAsync(key));
        // Staged blocks that were never committed are not a blob: the default listing skips them and the service discards them.
        var names = new List<string>();
        await foreach (var b in container.GetBlobsAsync()) names.Add(b.Name);
        Assert.Empty(names);
    }

    /// <summary>Gives the first 9 MiB at once, then waits before the rest, so an upload spans a known time.</summary>
    private sealed class SlowTailStream(byte[] data, TimeSpan delay) : Stream
    {
        private const int Fast = 9 * 1024 * 1024;
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_pos == Fast) await Task.Delay(delay, cancellationToken);
            var limit = _pos < Fast ? Fast : data.Length;
            var n = Math.Min(buffer.Length, limit - _pos);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }

    [Fact]
    public async Task CreatedAt_is_the_commit_time_not_the_time_the_first_block_was_staged()
    {
        var (store, _) = await NewAsync();
        var key = NewKey();
        var started = DateTimeOffset.UtcNow;

        await store.PutAsync(key, new SlowTailStream(Bytes(10 * 1024 * 1024), TimeSpan.FromSeconds(3)), Info, long.MaxValue);

        var entries = new List<ArtifactObjectEntry>();
        await foreach (var e in store.ListAsync(ArtifactObjectKey.TaskPrefix("t", "task"))) entries.Add(e);
        var created = Assert.Single(entries).CreatedAt;
        // Blob dates have one-second resolution, so allow a second under the 3 s delay.
        Assert.True(created >= started + TimeSpan.FromSeconds(1.9), $"CreatedAt {created:O} is before the end of the upload ({started:O} + 3s).");
    }

    [Fact]
    public async Task Key_prefix_is_applied_in_the_container_and_hidden_from_callers()
    {
        var (store, container) = await NewAsync("a2a/prod/");
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(10)), Info, 100);

        var raw = new List<string>();
        await foreach (var b in container.GetBlobsAsync()) raw.Add(b.Name);
        Assert.Equal(["a2a/prod/" + key], raw);
        var listed = new List<string>();
        await foreach (var e in store.ListAsync(ArtifactObjectKey.TaskPrefix("t", "task"))) listed.Add(e.Key);
        Assert.Equal([key], listed);
    }

    [Fact]
    public async Task File_name_with_non_ascii_characters_round_trips()
    {
        var (store, _) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(10)), new ArtifactObjectInfo("text/plain", "отчёт 100%.txt"), 100);

        var content = await store.OpenReadAsync(key);

        Assert.Equal("отчёт 100%.txt", content!.FileName);
        Assert.Equal("text/plain", content.ContentType);
    }

    private static async Task<byte[]> ReadAll(ArtifactObjectContent content)
    {
        await using (content)
        {
            using var ms = new MemoryStream();
            await content.Content.CopyToAsync(ms);
            return ms.ToArray();
        }
    }

    [Fact]
    public async Task A_prefix_does_not_list_the_objects_of_a_longer_prefix()
    {
        var container = await azurite.NewContainerAsync();
        var prod = new AzureBlobArtifactObjectStore(new AzureBlobArtifactStoreOptions { KeyPrefix = "prod", ContainerClient = container });
        var prod2 = new AzureBlobArtifactObjectStore(new AzureBlobArtifactStoreOptions { KeyPrefix = "prod2", ContainerClient = container });
        var key = NewKey();
        await prod2.PutAsync(key, new MemoryStream(Bytes(10)), Info, 1_000);
        await prod.PutAsync(NewKey(), new MemoryStream(Bytes(10)), Info, 1_000);

        var listed = new List<string>();
        await foreach (var entry in prod.ListAsync("")) listed.Add(entry.Key);

        Assert.Single(listed);
        Assert.DoesNotContain(key, listed);
        Assert.False(await prod.ExistsAsync(key));
    }
}

public sealed class AzureBlobArtifactStoreRegistrationTests
{
    private const string Dev = "UseDevelopmentStorage=true";

    [Fact]
    public void AddA2AAzureBlobArtifactStore_registers_the_store_as_the_artifact_object_store()
    {
        using var provider = new ServiceCollection()
            .AddA2AAzureBlobArtifactStore(o => { o.ContainerName = "c"; o.ConnectionString = Dev; })
            .BuildServiceProvider();

        var store = provider.GetRequiredService<ArtifactObjectStore>();

        Assert.IsType<AzureBlobArtifactObjectStore>(store);
        Assert.Same(store, provider.GetRequiredService<ArtifactObjectStore>());
    }

    [Fact]
    public void A_store_without_a_container_or_client_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new AzureBlobArtifactObjectStore(new AzureBlobArtifactStoreOptions { ConnectionString = Dev }));
        Assert.Throws<ArgumentException>(() => new AzureBlobArtifactObjectStore(new AzureBlobArtifactStoreOptions { ContainerName = "c" }));
    }
}
