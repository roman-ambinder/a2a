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

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        foreach (var d in _disposables) d.Dispose();
        return ValueTask.CompletedTask;
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

    /// <summary>
    /// Forwards to the real client, but reports a failure after S3 already applied the named call: what a client timeout or a
    /// lost response looks like.
    /// </summary>
    public class FailAfterApplyClient : System.Reflection.DispatchProxy
    {
        public IAmazonS3 Inner { get; set; } = null!;
        public string FailAfter { get; set; } = "";

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            var result = method!.Invoke(Inner, args);
            if (method.Name != FailAfter || result is not Task task) return result;
            return FailAfterAsync(task, method.ReturnType);
        }

        private static object FailAfterAsync(Task call, Type returnType)
        {
            var type = returnType.GetGenericArguments()[0];
            var fail = typeof(FailAfterApplyClient).GetMethod(nameof(Typed), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(type);
            return fail.Invoke(null, [call])!;
        }

        private static async Task<T> Typed<T>(Task call)
        {
            await call;
            throw new AmazonS3Exception("The response was lost.");
        }
    }

    [Theory]
    [InlineData(nameof(IAmazonS3.PutObjectAsync), 1_000)] // one PUT
    [InlineData(nameof(IAmazonS3.CompleteMultipartUploadAsync), 9 * 1024 * 1024)] // multipart
    public async Task A_write_that_fails_after_S3_applied_it_leaves_no_object(string failAfter, int size)
    {
        var (_, client, bucket) = await NewAsync();
        var proxy = System.Reflection.DispatchProxy.Create<IAmazonS3, FailAfterApplyClient>();
        ((FailAfterApplyClient)(object)proxy).Inner = client;
        ((FailAfterApplyClient)(object)proxy).FailAfter = failAfter;
        var store = new S3ArtifactObjectStore(new S3ArtifactStoreOptions { BucketName = bucket, Client = proxy });
        var key = NewKey();

        await Assert.ThrowsAsync<AmazonS3Exception>(async () => await store.PutAsync(key, new MemoryStream(Bytes(size)), Info, long.MaxValue));

        Assert.False(await store.ExistsAsync(key));
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

    private static DateTimeOffset S3Date(S3Object o) => new(DateTime.SpecifyKind(o.LastModified!.Value, DateTimeKind.Utc));

    private static async Task<S3Object> RawAsync(AmazonS3Client client, string bucket) =>
        Assert.Single((await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket })).S3Objects!);

    private static Task SetTagAsync(IAmazonS3 client, string bucket, string key, string tag, string value) =>
        client.PutObjectTaggingAsync(new PutObjectTaggingRequest
        {
            BucketName = bucket,
            Key = key,
            Tagging = new Tagging { TagSet = [new Tag { Key = tag, Value = value }] },
        });

    [Fact]
    public async Task Multipart_put_stamps_the_upload_time_after_the_upload()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();
        var started = DateTimeOffset.UtcNow;

        await store.PutAsync(key, new SlowTailStream(Bytes(10 * 1024 * 1024), TimeSpan.FromSeconds(3)), Info, long.MaxValue);

        var tags = await client.GetObjectTaggingAsync(new GetObjectTaggingRequest { BucketName = bucket, Key = key });
        var uploadMs = long.Parse(Assert.Single(tags.Tagging, t => t.Key == "a2a-upload-ms").Value);
        Assert.True(uploadMs >= 2_500, $"the upload took 3 s, but its stamp says {uploadMs} ms.");
        // Still written, for the sweep of an earlier version.
        var completedAt = DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(Assert.Single(tags.Tagging, t => t.Key == "a2a-completed-at").Value));
        Assert.True(completedAt >= started + TimeSpan.FromSeconds(2.5), $"stamp {completedAt:O} is before the end of the upload ({started:O} + 3s).");
        // Dated on the S3 clock: the S3 date plus the measured upload time.
        var created = Assert.Single(await List(store)).CreatedAt;
        Assert.Equal(S3Date(await RawAsync(client, bucket)) + TimeSpan.FromMilliseconds(uploadMs), created);
        Assert.True(created >= started + TimeSpan.FromSeconds(2.5), $"created {created:O} is before the end of the upload ({started:O} + 3s).");
    }

    [Fact]
    public async Task List_dates_a_multipart_object_by_its_s3_date_plus_its_upload_time()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(9 * 1024 * 1024)), Info, long.MaxValue);
        await SetTagAsync(client, bucket, key, "a2a-upload-ms", (5 * 3600 * 1000).ToString());

        Assert.Equal(S3Date(await RawAsync(client, bucket)) + TimeSpan.FromHours(5), Assert.Single(await List(store)).CreatedAt);
    }

    [Fact]
    public async Task List_reads_the_completion_stamp_of_an_earlier_version()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(9 * 1024 * 1024)), Info, long.MaxValue);
        var known = new DateTimeOffset(2031, 5, 6, 7, 8, 9, 123, TimeSpan.Zero);
        await SetTagAsync(client, bucket, key, "a2a-completed-at", known.ToUnixTimeMilliseconds().ToString());

        Assert.Equal(known, Assert.Single(await List(store)).CreatedAt);
    }

    [Fact]
    public async Task List_takes_a_multipart_object_without_a_stamp_as_too_new_to_judge()
    {
        var (store, client, bucket) = await NewAsync();
        var key = NewKey();
        await store.PutAsync(key, new MemoryStream(Bytes(9 * 1024 * 1024)), Info, long.MaxValue);
        await client.DeleteObjectTaggingAsync(new DeleteObjectTaggingRequest { BucketName = bucket, Key = key });
        var raw = await RawAsync(client, bucket);

        var entry = Assert.Single(await List(store));

        Assert.Equal(S3Date(raw) + S3ArtifactObjectStore.UnstampedAllowance, entry.CreatedAt);
    }

    /// <summary>Forwards to the real client, and lists the store just before the completion stamp is written.</summary>
    public class ListBeforeStampClient : System.Reflection.DispatchProxy
    {
        public IAmazonS3 Inner { get; set; } = null!;
        public Func<Task>? BeforeStamp { get; set; }

        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAmazonS3.PutObjectTaggingAsync) && BeforeStamp is { } before)
                return ThenAsync(before, () => (Task<PutObjectTaggingResponse>)method.Invoke(Inner, args)!);
            return method.Invoke(Inner, args);
        }

        private static async Task<PutObjectTaggingResponse> ThenAsync(Func<Task> before, Func<Task<PutObjectTaggingResponse>> call)
        {
            await before();
            return await call();
        }
    }

    [Fact]
    public async Task Multipart_object_listed_between_completion_and_stamp_is_too_new_to_judge()
    {
        var (_, client, bucket) = await NewAsync();
        var proxy = System.Reflection.DispatchProxy.Create<IAmazonS3, ListBeforeStampClient>();
        var store = new S3ArtifactObjectStore(new S3ArtifactStoreOptions { BucketName = bucket, Client = proxy });
        var key = NewKey();
        ArtifactObjectEntry? seen = null;
        ((ListBeforeStampClient)(object)proxy).Inner = client;
        ((ListBeforeStampClient)(object)proxy).BeforeStamp = async () => seen = Assert.Single(await List(store));

        await store.PutAsync(key, new MemoryStream(Bytes(9 * 1024 * 1024)), Info, long.MaxValue);

        // The object exists and has no stamp yet: its S3 date is the upload start, which says nothing about its age.
        Assert.NotNull(seen);
        Assert.Equal(S3Date(await RawAsync(client, bucket)) + S3ArtifactObjectStore.UnstampedAllowance, seen.CreatedAt);
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
