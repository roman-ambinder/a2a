using System.Security.Cryptography;
using A2A.Integrity;
using A2A.Server.Storage;
using A2A.Server.Storage.S3;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Stores.Tests;

[Collection(ContainersCollection.Name)]
public sealed class S3ArtifactObjectStoreTests(MinioFixture minio) : ArtifactObjectStoreContract, IAsyncLifetime
{
    private static readonly ArtifactObjectInfo Info = new("text/html", "page.html");
    private readonly List<IDisposable> _disposables = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        return Task.CompletedTask;
    }

    private async Task<(S3ArtifactObjectStore Store, AmazonS3Client Client, string Bucket)> NewAsync(string prefix = "")
    {
        var client = minio.NewClient();
        var bucket = await minio.NewBucketAsync(client);
        var store = new S3ArtifactObjectStore(new S3ArtifactStoreOptions { BucketName = bucket, KeyPrefix = prefix, Client = client });
        _disposables.Add(client);
        return (store, client, bucket);
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
        var (store, _, _) = await NewAsync();
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
        var (store, _, _) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(100)), Info, 10_000);

        var url = await store.TryCreateDownloadUrlAsync(key, TimeSpan.FromMinutes(1));

        using var http = new HttpClient();
        using var response = await http.GetAsync(url);
        response.EnsureSuccessStatusCode();
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Fact]
    public async Task Large_object_uploads_in_several_parts()
    {
        var (store, _, _) = await NewAsync();
        var key = NewKey();
        var data = Bytes(20 * 1024 * 1024 + 123); // two full 8 MiB parts and a short last part

        var result = await store.PutAsync(key, new MemoryStream(data), Info, long.MaxValue);

        Assert.Equal(data.Length, result.SizeBytes);
        Assert.Equal(ArtifactHash.Format(SHA256.HashData(data)), result.Sha256);
        var content = await store.OpenReadAsync(key, new ArtifactByteRange(8 * 1024 * 1024 - 5, 10));
        Assert.Equal(data.AsSpan(8 * 1024 * 1024 - 5, 10).ToArray(), await ReadAll(content!));
    }

    [Fact]
    public async Task Object_of_exactly_one_part_works()
    {
        var (store, _, _) = await NewAsync();
        var key = NewKey();
        var data = Bytes(8 * 1024 * 1024);

        var result = await store.PutAsync(key, new MemoryStream(data), Info, long.MaxValue);

        Assert.Equal(data.Length, result.SizeBytes);
        Assert.Equal(data, await ReadAll((await store.OpenReadAsync(key))!));
    }

    [Fact]
    public async Task Failed_large_upload_leaves_no_object_and_no_open_multipart_upload()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();

        await Assert.ThrowsAsync<ArtifactObjectTooLargeException>(async () =>
            await store.PutAsync(key, new MemoryStream(Bytes(17 * 1024 * 1024)), Info, 12 * 1024 * 1024));

        Assert.False(await store.ExistsAsync(key));
        var open = await client.ListMultipartUploadsAsync(bucket);
        Assert.Empty(open.MultipartUploads ?? []);
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

    private static async Task<List<ArtifactObjectEntry>> List(S3ArtifactObjectStore store)
    {
        var entries = new List<ArtifactObjectEntry>();
        await foreach (var e in store.ListAsync(ArtifactObjectKey.TaskPrefix("t", "task"))) entries.Add(e);
        return entries;
    }

    [Fact]
    public async Task Multipart_put_writes_the_completion_stamp_after_the_upload()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();
        var started = DateTimeOffset.UtcNow;

        await store.PutAsync(key, new SlowTailStream(Bytes(10 * 1024 * 1024), TimeSpan.FromSeconds(3)), Info, long.MaxValue);

        var tags = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key });
        var stamp = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(Assert.Single(tags.Tagging, t => t.Key == "a2a-completed-at").Value));
        Assert.True(stamp >= started + TimeSpan.FromSeconds(2.5), $"stamp {stamp:O} is before the end of the upload ({started:O} + 3s).");
        Assert.Equal(stamp, Assert.Single(await List(store)).CreatedAt);
    }

    [Fact]
    public async Task List_uses_the_completion_stamp_of_a_multipart_object()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(9 * 1024 * 1024)), Info, long.MaxValue);
        var known = new DateTimeOffset(2031, 5, 6, 7, 8, 9, 123, TimeSpan.Zero);
        await client.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = "a2a-completed-at", Value = known.ToUnixTimeMilliseconds().ToString() }] },
        });

        Assert.Equal(known, Assert.Single(await List(store)).CreatedAt);
    }

    [Fact]
    public async Task List_falls_back_to_the_s3_date_when_a_multipart_object_has_no_stamp()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(9 * 1024 * 1024)), Info, long.MaxValue);
        await client.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
        var raw = Assert.Single((await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects!);

        var entry = Assert.Single(await List(store));

        Assert.Equal(new DateTimeOffset(DateTime.SpecifyKind(raw.LastModified!.Value, DateTimeKind.Utc)), entry.CreatedAt);
    }

    [Fact]
    public async Task Key_prefix_is_applied_in_the_bucket_and_hidden_from_callers()
    {
        var (store, client, bucket) = await NewAsync("a2a/prod/");
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(10)), Info, 100);

        var raw = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket });
        Assert.Equal(["a2a/prod/" + key], raw.S3Objects!.Select(o => o.Key));
        var listed = new List<string>();
        await foreach (var e in store.ListAsync(ArtifactObjectKey.TaskPrefix("t", "task"))) listed.Add(e.Key);
        Assert.Equal([key], listed);
    }

    [Fact]
    public async Task File_name_with_non_ascii_characters_round_trips()
    {
        var (store, _, _) = await NewAsync();
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
        var client = minio.NewClient();
        _disposables.Add(client);
        var bucket = await minio.NewBucketAsync(client);
        var prod = new S3ArtifactObjectStore(new S3ArtifactStoreOptions { BucketName = bucket, KeyPrefix = "prod", Client = client });
        var prod2 = new S3ArtifactObjectStore(new S3ArtifactStoreOptions { BucketName = bucket, KeyPrefix = "prod2", Client = client });
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

public sealed class S3ArtifactStoreRegistrationTests
{
    [Fact]
    public void AddA2AS3ArtifactStore_registers_the_store_as_the_artifact_object_store()
    {
        using var provider = new Microsoft.Extensions.DependencyInjection.ServiceCollection()
            .AddA2AS3ArtifactStore(o => { o.BucketName = "b"; o.ServiceUrl = "http://localhost:9000"; o.ForcePathStyle = true; })
            .BuildServiceProvider();

        var store = provider.GetRequiredService<ArtifactObjectStore>();

        Assert.IsType<S3ArtifactObjectStore>(store);
        Assert.Same(store, provider.GetRequiredService<ArtifactObjectStore>());
    }

    [Fact]
    public void A_store_without_a_bucket_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new S3ArtifactObjectStore(new S3ArtifactStoreOptions()));
    }
}
