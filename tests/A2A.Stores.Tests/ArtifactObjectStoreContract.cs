using System.Security.Claims;
using System.Security.Cryptography;
using A2A.Integrity;
using A2A.Server;
using A2A.Server.Storage;
using A2A.Server.Uploads;

namespace A2A.Stores.Tests;

/// <summary>Behavior every <see cref="ArtifactObjectStore"/> must have. Each provider runs the same tests through a subclass.</summary>
public abstract class ArtifactObjectStoreContract
{
    /// <summary>A fresh, empty object store.</summary>
    protected abstract Task<ArtifactObjectStore> CreateStoreAsync();

    private static readonly ArtifactObjectInfo Info = new("application/octet-stream", "file.bin");

    private static byte[] Bytes(int length)
    {
        var b = new byte[length];
        new Random(42).NextBytes(b);
        return b;
    }

    private static string NewKey(string task = "task") => ArtifactObjectKey.Create("t", task, ArtifactObjectKey.NewObjectId());

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
    public async Task Exists_is_true_for_a_stored_object_and_false_for_a_missing_one()
    {
        var store = await CreateStoreAsync();
        var key = NewKey();
        Assert.False(await store.ExistsAsync(key));

        await store.PutAsync(key, new MemoryStream(Bytes(10)), Info, 100);

        Assert.True(await store.ExistsAsync(key));
        Assert.False(await store.ExistsAsync(NewKey()));
    }

    /// <summary>A stream that cannot seek and returns data in small pieces; it can fail after some bytes.</summary>
    private sealed class OneWayStream(byte[] data, int failAfter = -1) : Stream
    {
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (failAfter >= 0 && _pos >= failAfter) throw new IOException("boom");
            var n = Math.Min(Math.Min(count, 7), data.Length - _pos);
            if (failAfter >= 0) n = Math.Min(n, failAfter - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override int Read(Span<byte> buffer)
        {
            if (failAfter >= 0 && _pos >= failAfter) throw new IOException("boom");
            var n = Math.Min(Math.Min(buffer.Length, 7), data.Length - _pos);
            if (failAfter >= 0) n = Math.Min(n, failAfter - _pos);
            data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }

    private static async Task<List<string>> Keys(ArtifactObjectStore s, string prefix)
    {
        var keys = new List<string>();
        await foreach (var e in s.ListAsync(prefix)) keys.Add(e.Key);
        return keys;
    }

    [Fact]
    public async Task Put_then_read_round_trips()
    {
        var s = await CreateStoreAsync();
        var data = Bytes(100_000);
        var key = NewKey();

        var result = await s.PutAsync(key, new MemoryStream(data), Info, 1_000_000);

        Assert.Equal(data.Length, result.SizeBytes);
        Assert.Equal(ArtifactHash.Format(SHA256.HashData(data)), result.Sha256);
        var content = await s.OpenReadAsync(key);
        Assert.NotNull(content);
        Assert.Equal("application/octet-stream", content.ContentType);
        Assert.Equal("file.bin", content.FileName);
        Assert.Equal(data.Length, content.Length);
        Assert.Equal(data.Length, content.TotalLength);
        Assert.Equal(data, await ReadAll(content));
    }

    [Fact]
    public async Task Put_of_non_seekable_stream_works()
    {
        var s = await CreateStoreAsync();
        var data = Bytes(5_000);
        var key = NewKey();

        var result = await s.PutAsync(key, new OneWayStream(data), Info, 1_000_000);

        Assert.Equal(data.Length, result.SizeBytes);
        Assert.Equal(ArtifactHash.Format(SHA256.HashData(data)), result.Sha256);
        Assert.Equal(data, await ReadAll((await s.OpenReadAsync(key))!));
    }

    [Fact]
    public async Task Put_over_maxBytes_throws_and_leaves_no_object()
    {
        var s = await CreateStoreAsync();
        var key = NewKey();

        await Assert.ThrowsAsync<ArtifactObjectTooLargeException>(async () =>
            await s.PutAsync(key, new OneWayStream(Bytes(1_000)), Info, 999));

        Assert.Null(await s.OpenReadAsync(key));
        Assert.Empty(await Keys(s, ArtifactObjectKey.TaskPrefix("t", "task")));
    }

    [Fact]
    public async Task Put_of_exactly_maxBytes_succeeds()
    {
        var s = await CreateStoreAsync();
        var key = NewKey();

        var result = await s.PutAsync(key, new OneWayStream(Bytes(1_000)), Info, 1_000);

        Assert.Equal(1_000, result.SizeBytes);
    }

    [Fact]
    public async Task Put_when_stream_throws_leaves_no_object()
    {
        var s = await CreateStoreAsync();
        var key = NewKey();

        await Assert.ThrowsAsync<IOException>(async () =>
            await s.PutAsync(key, new OneWayStream(Bytes(1_000), failAfter: 100), Info, 1_000_000));

        Assert.Null(await s.OpenReadAsync(key));
        Assert.Empty(await Keys(s, ArtifactObjectKey.TaskPrefix("t", "task")));
    }

    [Fact]
    public async Task Read_missing_returns_null()
    {
        var s = await CreateStoreAsync();

        Assert.Null(await s.OpenReadAsync(NewKey()));
    }

    [Fact]
    public async Task Delete_is_idempotent()
    {
        var s = await CreateStoreAsync();
        var key = NewKey();
        await s.PutAsync(key, new MemoryStream(Bytes(10)), Info, 100);

        await s.DeleteAsync(key);
        await s.DeleteAsync(key);
        await s.DeleteAsync(NewKey());

        Assert.Null(await s.OpenReadAsync(key));
    }

    [Fact]
    public async Task List_by_prefix_returns_only_that_task()
    {
        var s = await CreateStoreAsync();
        var a1 = NewKey("a");
        var a2 = NewKey("a");
        var ab = NewKey("ab");
        foreach (var k in new[] { a1, a2, ab }) await s.PutAsync(k, new MemoryStream(Bytes(10)), Info, 100);

        var listed = await Keys(s, ArtifactObjectKey.TaskPrefix("t", "a"));

        Assert.Equal(new[] { a1, a2 }.OrderBy(x => x, StringComparer.Ordinal), listed.OrderBy(x => x, StringComparer.Ordinal));
        await foreach (var e in s.ListAsync(ArtifactObjectKey.TaskPrefix("t", "a")))
            Assert.True((DateTimeOffset.UtcNow - e.CreatedAt).Duration() < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Range_read_returns_requested_bytes()
    {
        var s = await CreateStoreAsync();
        var data = Bytes(1_000);
        var key = NewKey();
        await s.PutAsync(key, new MemoryStream(data), Info, 10_000);

        var content = await s.OpenReadAsync(key, new ArtifactByteRange(100, 50));

        Assert.NotNull(content);
        Assert.Equal(50, content.Length);
        Assert.Equal(1_000, content.TotalLength);
        Assert.Equal(data.AsSpan(100, 50).ToArray(), await ReadAll(content));
    }

    [Fact]
    public async Task Range_read_without_length_reads_to_the_end_and_clamps()
    {
        var s = await CreateStoreAsync();
        var data = Bytes(1_000);
        var key = NewKey();
        await s.PutAsync(key, new MemoryStream(data), Info, 10_000);

        var tail = await s.OpenReadAsync(key, new ArtifactByteRange(900, null));
        var clamped = await s.OpenReadAsync(key, new ArtifactByteRange(990, 500));

        Assert.Equal(100, tail!.Length);
        Assert.Equal(data.AsSpan(900).ToArray(), await ReadAll(tail));
        Assert.Equal(10, clamped!.Length);
        Assert.Equal(1_000, clamped.TotalLength);
        Assert.Equal(data.AsSpan(990).ToArray(), await ReadAll(clamped));
    }

    [Fact]
    public async Task Zero_byte_object_round_trips()
    {
        var s = await CreateStoreAsync();
        var key = NewKey();

        var result = await s.PutAsync(key, new MemoryStream(), Info, 100);

        Assert.Equal(0, result.SizeBytes);
        Assert.Equal(ArtifactHash.Format(SHA256.HashData([])), result.Sha256);
        Assert.True(await s.ExistsAsync(key));
        var whole = await s.OpenReadAsync(key);
        Assert.NotNull(whole);
        Assert.Equal(0, whole.TotalLength);
        Assert.Empty(await ReadAll(whole));
        var ranged = await s.OpenReadAsync(key, new ArtifactByteRange(0, null));
        Assert.NotNull(ranged);
        Assert.Equal(0, ranged.TotalLength);
        Assert.Empty(await ReadAll(ranged));
    }

    [Fact]
    public async Task Range_read_past_the_end_is_empty()
    {
        var s = await CreateStoreAsync();
        var key = NewKey();
        await s.PutAsync(key, new MemoryStream(Bytes(1_000)), Info, 10_000);

        var content = await s.OpenReadAsync(key, new ArtifactByteRange(2_000, 10));

        Assert.NotNull(content);
        Assert.Equal(0, content.Length);
        Assert.Equal(1_000, content.TotalLength);
        Assert.Empty(await ReadAll(content));
        Assert.Null(await s.OpenReadAsync(NewKey(), new ArtifactByteRange(2_000, 10)));
    }

    [Theory]
    [InlineData("t", "")]
    [InlineData("", "task")]
    [InlineData("a b", "50%/..")]
    [InlineData("t", "x%2Fy%25")]
    public async Task Keys_with_an_empty_segment_or_encoded_characters_work(string tenant, string task)
    {
        var s = await CreateStoreAsync();
        var key = ArtifactObjectKey.Create(tenant, task, ArtifactObjectKey.NewObjectId());
        var data = Bytes(100);

        await s.PutAsync(key, new MemoryStream(data), Info, 1_000);

        Assert.True(await s.ExistsAsync(key));
        Assert.Equal(data, await ReadAll((await s.OpenReadAsync(key))!));
        Assert.Equal(new[] { key }, await Keys(s, ArtifactObjectKey.TaskPrefix(tenant, task)));
        await s.DeleteAsync(key);
        Assert.False(await s.ExistsAsync(key));
        Assert.Empty(await Keys(s, ArtifactObjectKey.TaskPrefix(tenant, task)));
    }

    [Fact]
    public async Task Default_download_url_is_null_or_absolute()
    {
        var s = await CreateStoreAsync();
        var key = NewKey();
        await s.PutAsync(key, new MemoryStream(Bytes(10)), Info, 100);

        var url = await s.TryCreateDownloadUrlAsync(key, TimeSpan.FromMinutes(1));

        Assert.True(url is null || url.IsAbsoluteUri);
    }

    // =====================================================================
    // Inbound uploads (InboundFiles): the same behavior on every provider
    // =====================================================================

    private static readonly Uri LinkBase = new("https://agent.test/a2a");

    /// <summary>The real clock plus an offset, so a store that stamps objects with the real time can still be aged.</summary>
    private sealed class OffsetClock : TimeProvider
    {
        public TimeSpan Offset { get; set; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;
    }

    private static ArtifactStorageOptions UploadOptions(Action<ArtifactUploadOptions>? configure = null)
    {
        var storage = new ArtifactStorageOptions { PublicBaseUrl = LinkBase };
        storage.Uploads.Enabled = true;
        configure?.Invoke(storage.Uploads);
        return storage;
    }

    private static ServerCallContext Caller(string principal = "alice", string tenant = "t") => new()
    {
        Principal = principal,
        Tenant = tenant,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", principal)], "test")),
    };

    private static Message MessageWith(params UrlPart[] parts) =>
        new() { MessageId = Guid.NewGuid().ToString(), Role = Role.User, Parts = [.. parts] };

    private static string ObjectIdOf(UrlPart part) => part.Url.AbsolutePath[(part.Url.AbsolutePath.LastIndexOf('/') + 1)..];

    private static async Task<List<ArtifactObjectEntry>> Entries(ArtifactObjectStore s, string prefix)
    {
        var entries = new List<ArtifactObjectEntry>();
        await foreach (var e in s.ListAsync(prefix)) entries.Add(e);
        return entries;
    }

    [Fact]
    public async Task List_reports_the_size_of_every_object()
    {
        var s = await CreateStoreAsync();
        var small = NewKey("sizes");
        var large = NewKey("sizes");
        await s.PutAsync(small, new MemoryStream(Bytes(10)), Info, 100);
        await s.PutAsync(large, new MemoryStream(Bytes(100_000)), Info, 1_000_000);
        var empty = NewKey("sizes");
        await s.PutAsync(empty, new MemoryStream(), Info, 100);

        var entries = (await Entries(s, ArtifactObjectKey.TaskPrefix("t", "sizes"))).ToDictionary(e => e.Key, e => e.SizeBytes);

        Assert.Equal(10, entries[small]);
        Assert.Equal(100_000, entries[large]);
        Assert.Equal(0, entries[empty]);
    }

    [Fact]
    public async Task Upload_round_trips_through_InboundFiles()
    {
        var s = await CreateStoreAsync();
        var files = new InboundFiles(s, UploadOptions(), TimeProvider.System);
        var call = Caller();
        var data = Bytes(50_000);

        var part = await files.UploadAsync(call, new OneWayStream(data), "text/plain", "dir/notes.txt", declaredLength: null, CancellationToken.None);

        Assert.Equal("notes.txt", part.Filename);
        Assert.Equal("text/plain", part.MediaType);
        Assert.Equal(ArtifactHash.Format(SHA256.HashData(data)), part.Metadata!["sha256"].GetString());
        Assert.Equal(data.Length, part.Metadata["sizeBytes"].GetInt64());
        Assert.Equal(ArtifactLink.CreateUpload(LinkBase, "t", ObjectIdOf(part)), part.Url);
        var content = await files.OpenAsync(call, part, null, CancellationToken.None);
        Assert.NotNull(content);
        Assert.Equal("text/plain", content.ContentType);
        Assert.Equal("notes.txt", content.FileName);
        Assert.Equal(data, await ReadAll(content));
        var ranged = await files.OpenAsync(call, ObjectIdOf(part), new ArtifactByteRange(10, 20), CancellationToken.None);
        Assert.Equal(data.AsSpan(10, 20).ToArray(), await ReadAll(ranged!));
    }

    [Fact]
    public async Task Upload_over_the_cap_throws_and_leaves_no_object()
    {
        var s = await CreateStoreAsync();
        var files = new InboundFiles(s, UploadOptions(o => o.MaxUploadBytes = 1_000), TimeProvider.System);

        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
            await files.UploadAsync(Caller(), new OneWayStream(Bytes(1_001)), null, null, null, CancellationToken.None));

        Assert.Equal(413, ex.HttpStatus);
        Assert.Empty(await Entries(s, UploadObjectKey.UploadsPrefix));
        // Announced too large: refused before a byte is read.
        var announced = await Assert.ThrowsAsync<A2AException>(async () =>
            await files.UploadAsync(Caller(), new OneWayStream(Bytes(10)), null, null, 1_001, CancellationToken.None));
        Assert.Equal(413, announced.HttpStatus);
        Assert.Empty(await Entries(s, UploadObjectKey.UploadsPrefix));
    }

    [Fact]
    public async Task Upload_of_another_length_than_announced_is_refused_and_not_kept()
    {
        var s = await CreateStoreAsync();
        var files = new InboundFiles(s, UploadOptions(), TimeProvider.System);

        await Assert.ThrowsAsync<A2AException>(async () =>
            await files.UploadAsync(Caller(), new OneWayStream(Bytes(100)), null, null, 99, CancellationToken.None));

        Assert.Empty(await Entries(s, UploadObjectKey.UploadsPrefix));
    }

    [Fact]
    public async Task Quota_refuses_and_leaves_no_object()
    {
        var s = await CreateStoreAsync();
        var files = new InboundFiles(s, UploadOptions(o =>
        {
            o.MaxStoredBytesPerCaller = 1_500;
            o.MaxUploadsPerCaller = 2;
        }), TimeProvider.System);
        var alice = Caller();
        await files.UploadAsync(alice, new OneWayStream(Bytes(1_000)), null, null, null, CancellationToken.None);

        // 500 bytes are left in the byte quota: a larger stream is cut off and refused with 429, not 413.
        var tooBig = await Assert.ThrowsAsync<A2AException>(async () =>
            await files.UploadAsync(alice, new OneWayStream(Bytes(501)), null, null, null, CancellationToken.None));
        Assert.Equal(429, tooBig.HttpStatus ?? A2AErrors.HttpStatus(tooBig.Code));
        Assert.NotNull(tooBig.RetryAfter);
        Assert.Single(await Entries(s, UploadObjectKey.UploadsPrefix));

        // Another caller has a quota of their own.
        await files.UploadAsync(Caller("bob"), new OneWayStream(Bytes(1_000)), null, null, null, CancellationToken.None);

        // The count quota: alice may keep two files, so the third is refused whatever its size.
        await files.UploadAsync(alice, new OneWayStream(Bytes(100)), null, null, null, CancellationToken.None);
        var third = await Assert.ThrowsAsync<A2AException>(async () =>
            await files.UploadAsync(alice, new OneWayStream(Bytes(1)), null, null, null, CancellationToken.None));
        Assert.Equal(429, third.HttpStatus ?? A2AErrors.HttpStatus(third.Code));
        var alices = await Entries(s, UploadObjectKey.UploadPrefix("t", UploadObjectKey.OwnerHash("alice")));
        Assert.Equal(2, alices.Count);
    }

    [Fact]
    public async Task One_owner_cannot_open_another_owners_upload()
    {
        var s = await CreateStoreAsync();
        var files = new InboundFiles(s, UploadOptions(), TimeProvider.System);
        var part = await files.UploadAsync(Caller("alice"), new MemoryStream(Bytes(100)), null, null, null, CancellationToken.None);

        Assert.NotNull(await files.OpenAsync(Caller("alice"), part, null, CancellationToken.None));
        Assert.Null(await files.OpenAsync(Caller("bob"), part, null, CancellationToken.None));
        Assert.Null(await files.OpenAsync(Caller("bob"), ObjectIdOf(part), null, CancellationToken.None));
        Assert.Null(await files.OpenAsync(Caller("alice", tenant: "other"), ObjectIdOf(part), null, CancellationToken.None));
        // A message of bob that names alice's link is refused, with the one fixed text.
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
            await files.AttachAsync(Caller("bob"), "task-1", MessageWith(part), CancellationToken.None));
        Assert.Equal(InboundFiles.LinkRefusedText, ex.Message);
        Assert.Empty(await Entries(s, UploadObjectKey.ReferencesPrefix));
    }

    [Fact]
    public async Task Sweep_removes_an_unused_upload_after_ttl_and_grace_and_keeps_a_referenced_one()
    {
        var s = await CreateStoreAsync();
        var clock = new OffsetClock();
        var storage = UploadOptions();
        var files = new InboundFiles(s, storage, clock);
        var tasks = new InMemoryTaskStore(new InMemoryEventStore(new StreamingOptions()));
        var alice = Caller();
        var unused = await files.UploadAsync(alice, new MemoryStream(Bytes(100)), null, null, null, CancellationToken.None);
        var used = await files.UploadAsync(alice, new MemoryStream(Bytes(100)), null, null, null, CancellationToken.None);
        var message = MessageWith(used);
        await files.AttachAsync(alice, "task-1", message, CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await tasks.CreateAsync(new TaskRecord("t", "alice", new AgentTask
        {
            Id = "task-1", ContextId = "ctx", Status = new AgentTaskStatus { State = TaskState.Completed }, History = [message],
        }, 0, now, now));
        var unusedKey = UploadObjectKey.CreateUpload("t", UploadObjectKey.OwnerHash("alice"), ObjectIdOf(unused));
        var usedKey = UploadObjectKey.CreateUpload("t", UploadObjectKey.OwnerHash("alice"), ObjectIdOf(used));
        var referenceKey = UploadObjectKey.CreateReference("t", UploadObjectKey.OwnerHash("alice"), ObjectIdOf(used), "task-1");
        Assert.True(await s.ExistsAsync(referenceKey));

        // Past the attach window but not past the grace period: nothing goes yet.
        clock.Offset = storage.Uploads.UnusedUploadTtl + TimeSpan.FromMinutes(1);
        Assert.False(await files.SweepAsync(tasks, 100, CancellationToken.None));
        Assert.True(await s.ExistsAsync(unusedKey));

        clock.Offset = storage.Uploads.UnusedUploadTtl + storage.OrphanGracePeriod + TimeSpan.FromMinutes(1);
        Assert.False(await files.SweepAsync(tasks, 100, CancellationToken.None));

        Assert.False(await s.ExistsAsync(unusedKey));
        Assert.True(await s.ExistsAsync(usedKey));      // its task still refers to it
        Assert.True(await s.ExistsAsync(referenceKey));

        // The task goes: the next sweep finds it gone, deletes the reference, then the upload.
        Assert.True(await tasks.DeleteAsync(new TaskKey("t", "alice", "task-1")));
        Assert.False(await files.SweepAsync(tasks, 100, CancellationToken.None));
        Assert.False(await s.ExistsAsync(usedKey));
        Assert.False(await s.ExistsAsync(referenceKey));
        Assert.Empty(await Entries(s, UploadObjectKey.UploadsPrefix));
        Assert.Empty(await Entries(s, UploadObjectKey.ReferencesPrefix));
    }

    [Fact]
    public async Task Upload_and_reference_keys_are_not_artifact_keys()
    {
        var s = await CreateStoreAsync();
        var files = new InboundFiles(s, UploadOptions(), TimeProvider.System);
        var alice = Caller();
        var part = await files.UploadAsync(alice, new MemoryStream(Bytes(10)), null, null, null, CancellationToken.None);
        await files.AttachAsync(alice, "task-1", MessageWith(part), CancellationToken.None);

        var keys = (await Entries(s, string.Empty)).Select(e => e.Key).ToList();

        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, k => UploadObjectKey.TryParseUpload(k, out _, out _, out _));
        Assert.Contains(keys, k => UploadObjectKey.TryParseReference(k, out _, out _, out _, out _));
        foreach (var key in keys) Assert.False(ArtifactObjectKey.TryParse(key, out _, out _, out _), key);
    }
}

public sealed class InMemoryArtifactObjectStoreTests : ArtifactObjectStoreContract
{
    protected override Task<ArtifactObjectStore> CreateStoreAsync() =>
        Task.FromResult<ArtifactObjectStore>(new InMemoryArtifactObjectStore());
}
