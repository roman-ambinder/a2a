using System.Net;
using System.Reflection;
using A2A.Server;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Server.Storage.S3;
using A2A.Tests;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Stores.Tests;

/// <summary>
/// The retention sweep against the S3 store, on a fake S3 that keeps its semantics for dates: a completed multipart object is
/// dated from when its upload was initiated, and the completion stamp is a separate call. No Docker needed. The worst failure
/// is deleting an object whose upload just finished, before the task that refers to it is committed.
/// </summary>
public sealed class S3OrphanSweepTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromHours(1);
    private static readonly ArtifactObjectInfo Info = new("application/octet-stream", null);
    private const int TenMiB = 10 * 1024 * 1024; // two parts

    /// <summary>
    /// An in-memory S3 with the calls the store makes. Each uploaded part advances the clocks by <see cref="PartTime"/>, so
    /// an upload spans a known time. <see cref="BeforeTagging"/> runs between the completion of a multipart upload and its stamp.
    /// </summary>
    public class FakeS3 : DispatchProxy
    {
        private sealed class Obj
        {
            public required string ETag { get; init; }
            public required DateTime LastModified { get; init; }
            public required long Size { get; init; }
            public List<Tag> Tags { get; set; } = [];
        }

        private readonly SortedDictionary<string, Obj> _objects = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Key, DateTime Initiated)> _uploads = [];
        private readonly Dictionary<string, long> _uploadSizes = [];

        /// <summary>S3's clock; it dates every object.</summary>
        public FakeTimeProvider Clock { get; set; } = null!;

        /// <summary>Other clocks that run along with S3's (an uploader whose wall clock is off still ticks at the same rate).</summary>
        public List<FakeTimeProvider> OtherClocks { get; } = [];

        public TimeSpan PartTime { get; set; }
        public Func<Task>? BeforeTagging { get; set; }

        public bool Has(string key) => _objects.ContainsKey(key);
        public void RemoveTags(string key) => _objects[key].Tags = [];
        public void SetTags(string key, params Tag[] tags) => _objects[key].Tags = [.. tags];
        public DateTime LastModifiedOf(string key) => _objects[key].LastModified;

        private DateTime Now => Clock.GetUtcNow().UtcDateTime;

        private static AmazonS3Exception NotFound() => new("Not Found") { StatusCode = HttpStatusCode.NotFound };

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            var request = args is { Length: > 0 } ? args[0] : null;
            return (method!.Name, request) switch
            {
                (nameof(IAmazonS3.Dispose), _) => null,
                (nameof(IAmazonS3.PutObjectAsync), PutObjectRequest r) => Put(r),
                (nameof(IAmazonS3.InitiateMultipartUploadAsync), InitiateMultipartUploadRequest r) => Initiate(r),
                (nameof(IAmazonS3.UploadPartAsync), UploadPartRequest r) => UploadPart(r),
                (nameof(IAmazonS3.CompleteMultipartUploadAsync), CompleteMultipartUploadRequest r) => Complete(r),
                (nameof(IAmazonS3.AbortMultipartUploadAsync), AbortMultipartUploadRequest r) => Abort(r),
                (nameof(IAmazonS3.PutObjectTaggingAsync), PutObjectTaggingRequest r) => PutTagging(r),
                (nameof(IAmazonS3.GetObjectTaggingAsync), GetObjectTaggingRequest r) => GetTagging(r),
                (nameof(IAmazonS3.ListObjectsV2Async), ListObjectsV2Request r) => List(r),
                (nameof(IAmazonS3.DeleteObjectAsync), DeleteObjectRequest r) => Delete(r),
                (nameof(IAmazonS3.GetObjectMetadataAsync), GetObjectMetadataRequest r) => Head(r),
                _ => throw new NotSupportedException(method.Name),
            };
        }

        private Task<PutObjectResponse> Put(PutObjectRequest r)
        {
            _objects[r.Key] = new Obj { ETag = "\"single\"", LastModified = Now, Size = r.InputStream.Length };
            return Task.FromResult(new PutObjectResponse());
        }

        private Task<InitiateMultipartUploadResponse> Initiate(InitiateMultipartUploadRequest r)
        {
            var id = Guid.NewGuid().ToString("N");
            _uploads[id] = (r.Key, Now);
            _uploadSizes[id] = 0;
            return Task.FromResult(new InitiateMultipartUploadResponse { UploadId = id });
        }

        private Task<UploadPartResponse> UploadPart(UploadPartRequest r)
        {
            Clock.Advance(PartTime);
            foreach (var c in OtherClocks) c.Advance(PartTime);
            _uploadSizes[r.UploadId] += r.InputStream.Length;
            return Task.FromResult(new UploadPartResponse { ETag = $"\"part{r.PartNumber}\"" });
        }

        private Task<CompleteMultipartUploadResponse> Complete(CompleteMultipartUploadRequest r)
        {
            var (key, initiated) = _uploads[r.UploadId];
            _uploads.Remove(r.UploadId);
            // S3: the object of a multipart upload is dated from the initiation of the upload, not its completion.
            _objects[key] = new Obj { ETag = $"\"multi-{r.PartETags.Count}\"", LastModified = initiated, Size = _uploadSizes[r.UploadId] };
            return Task.FromResult(new CompleteMultipartUploadResponse());
        }

        private Task<AbortMultipartUploadResponse> Abort(AbortMultipartUploadRequest r)
        {
            _uploads.Remove(r.UploadId);
            return Task.FromResult(new AbortMultipartUploadResponse());
        }

        private async Task<PutObjectTaggingResponse> PutTagging(PutObjectTaggingRequest r)
        {
            if (BeforeTagging is { } before) await before();
            if (!_objects.TryGetValue(r.Key, out var o)) throw NotFound();
            o.Tags = [.. r.Tagging.TagSet];
            return new PutObjectTaggingResponse();
        }

        private Task<GetObjectTaggingResponse> GetTagging(GetObjectTaggingRequest r) =>
            _objects.TryGetValue(r.Key, out var o)
                ? Task.FromResult(new GetObjectTaggingResponse { Tagging = [.. o.Tags] })
                : Task.FromException<GetObjectTaggingResponse>(NotFound());

        private Task<ListObjectsV2Response> List(ListObjectsV2Request r) =>
            Task.FromResult(new ListObjectsV2Response
            {
                S3Objects = _objects.Where(p => p.Key.StartsWith(r.Prefix ?? "", StringComparison.Ordinal))
                    .Select(p => new S3Object { Key = p.Key, ETag = p.Value.ETag, LastModified = p.Value.LastModified, Size = p.Value.Size })
                    .ToList(),
                IsTruncated = false,
            });

        private Task<DeleteObjectResponse> Delete(DeleteObjectRequest r)
        {
            _objects.Remove(r.Key);
            return Task.FromResult(new DeleteObjectResponse());
        }

        private Task<GetObjectMetadataResponse> Head(GetObjectMetadataRequest r) =>
            _objects.ContainsKey(r.Key)
                ? Task.FromResult(new GetObjectMetadataResponse())
                : Task.FromException<GetObjectMetadataResponse>(NotFound());
    }

    private sealed class Rig : IDisposable
    {
        public required FakeTimeProvider Clock { get; init; }
        public required FakeS3 S3 { get; init; }
        public required S3ArtifactObjectStore Store { get; init; }
        public required ServiceProvider Services { get; init; }

        /// <summary>One run of the whole sweep, as the retention service does it, with no task in the task store.</summary>
        public Task SweepAsync() =>
            A2ARetentionService.FromServices(Services, Options.Create(new A2ARetentionOptions())).RunOnceAsync(CancellationToken.None);

        public async Task<List<ArtifactObjectEntry>> ListAsync()
        {
            var entries = new List<ArtifactObjectEntry>();
            await foreach (var e in Store.ListAsync("")) entries.Add(e);
            return entries;
        }

        public void Dispose() => Services.Dispose();
    }

    private static Rig NewRig(TimeSpan partTime, FakeTimeProvider? uploaderClock = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var s3 = DispatchProxy.Create<IAmazonS3, FakeS3>();
        var fake = (FakeS3)(object)s3;
        fake.Clock = clock;
        fake.PartTime = partTime;
        if (uploaderClock is not null) fake.OtherClocks.Add(uploaderClock);
        var store = new S3ArtifactObjectStore(new S3ArtifactStoreOptions { BucketName = "b", Client = s3 }, uploaderClock ?? clock);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddA2AServer(o =>
        {
            o.ArtifactStorage.PublicBaseUrl = new Uri("https://agent.example/");
            o.ArtifactStorage.OrphanGracePeriod = Grace;
        });
        services.AddSingleton<ArtifactObjectStore>(store);
        return new Rig { Clock = clock, S3 = fake, Store = store, Services = services.BuildServiceProvider() };
    }

    private static string NewKey() => ArtifactObjectKey.Create("", "task", ArtifactObjectKey.NewObjectId());

    private static async Task PutAsync(Rig rig, string key, int size) =>
        await rig.Store.PutAsync(key, new MemoryStream(new byte[size]), Info, long.MaxValue);

    [Fact]
    public async Task Sweep_between_completion_and_stamp_keeps_an_object_whose_upload_took_longer_than_the_grace_period()
    {
        using var rig = NewRig(partTime: TimeSpan.FromHours(1)); // two parts: the upload takes 2 h, twice the grace period
        var key = NewKey();
        bool? keptBySweep = null;
        rig.S3.BeforeTagging = async () =>
        {
            // The object is complete and unstamped, and no task refers to it yet: what a sweep on any instance can see.
            await rig.SweepAsync();
            keptBySweep = rig.S3.Has(key);
        };

        await PutAsync(rig, key, TenMiB);

        Assert.True(keptBySweep, "the sweep deleted an object whose upload had just completed");
        Assert.True(rig.S3.Has(key));
    }

    [Fact]
    public async Task Stamped_multipart_object_is_aged_from_its_completion_not_its_start()
    {
        using var rig = NewRig(partTime: TimeSpan.FromHours(1));
        var key = NewKey();
        await PutAsync(rig, key, TenMiB);
        var completed = rig.Clock.GetUtcNow();

        Assert.Equal(completed, Assert.Single(await rig.ListAsync()).CreatedAt);
        rig.Clock.Advance(Grace - TimeSpan.FromMinutes(1));
        await rig.SweepAsync();
        Assert.True(rig.S3.Has(key)); // 3 h after the upload started, but younger than the grace period

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.SweepAsync();
        Assert.False(rig.S3.Has(key)); // an orphan past the grace period is still deleted
    }

    [Fact]
    public async Task Multipart_date_is_on_the_s3_clock_when_the_uploader_clock_is_off()
    {
        // The uploading instance's wall clock is 3 h behind S3's; its stopwatch runs at the same rate.
        var uploader = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero) - TimeSpan.FromHours(3));
        using var rig = NewRig(partTime: TimeSpan.FromMinutes(10), uploaderClock: uploader);
        var key = NewKey();
        await PutAsync(rig, key, TenMiB);

        Assert.Equal(rig.Clock.GetUtcNow(), Assert.Single(await rig.ListAsync()).CreatedAt);
        await rig.SweepAsync();
        Assert.True(rig.S3.Has(key));
    }

    [Fact]
    public async Task Multipart_orphan_that_never_got_its_stamp_is_collected_after_the_allowance()
    {
        using var rig = NewRig(partTime: TimeSpan.FromHours(1));
        var key = NewKey();
        await PutAsync(rig, key, TenMiB);
        rig.S3.RemoveTags(key); // the writer died between completing the upload and stamping it
        var initiated = new DateTimeOffset(DateTime.SpecifyKind(rig.S3.LastModifiedOf(key), DateTimeKind.Utc));

        Assert.Equal(initiated + S3ArtifactObjectStore.UnstampedAllowance, Assert.Single(await rig.ListAsync()).CreatedAt);
        rig.Clock.Advance(initiated + S3ArtifactObjectStore.UnstampedAllowance + Grace - rig.Clock.GetUtcNow() - TimeSpan.FromMinutes(1));
        await rig.SweepAsync();
        Assert.True(rig.S3.Has(key));

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.SweepAsync();
        Assert.False(rig.S3.Has(key));
    }

    [Fact]
    public async Task Earlier_completion_stamp_is_still_read_and_never_dates_an_object_before_its_upload()
    {
        using var rig = NewRig(partTime: TimeSpan.FromMinutes(1));
        var key = NewKey();
        await PutAsync(rig, key, TenMiB);
        var initiated = new DateTimeOffset(DateTime.SpecifyKind(rig.S3.LastModifiedOf(key), DateTimeKind.Utc));
        var later = initiated + TimeSpan.FromMinutes(5);

        rig.S3.SetTags(key, new Tag { Key = "a2a-completed-at", Value = later.ToUnixTimeMilliseconds().ToString() });
        Assert.Equal(later, Assert.Single(await rig.ListAsync()).CreatedAt);

        rig.S3.SetTags(key, new Tag { Key = "a2a-completed-at", Value = (initiated - TimeSpan.FromHours(5)).ToUnixTimeMilliseconds().ToString() });
        Assert.Equal(initiated, Assert.Single(await rig.ListAsync()).CreatedAt);
    }

    [Fact]
    public async Task Single_part_object_is_dated_by_s3_and_swept_after_the_grace_period()
    {
        using var rig = NewRig(partTime: TimeSpan.FromHours(1));
        var key = NewKey();
        var written = rig.Clock.GetUtcNow();
        await PutAsync(rig, key, 1_000);

        Assert.Equal(written, Assert.Single(await rig.ListAsync()).CreatedAt);
        rig.Clock.Advance(Grace - TimeSpan.FromMinutes(1));
        await rig.SweepAsync();
        Assert.True(rig.S3.Has(key));

        rig.Clock.Advance(TimeSpan.FromMinutes(2));
        await rig.SweepAsync();
        Assert.False(rig.S3.Has(key));
    }
}
