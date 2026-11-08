using System.Security.Cryptography;
using A2A.Integrity;
using A2A.Server.Storage;

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
}

public sealed class InMemoryArtifactObjectStoreTests : ArtifactObjectStoreContract
{
    protected override Task<ArtifactObjectStore> CreateStoreAsync() =>
        Task.FromResult<ArtifactObjectStore>(new InMemoryArtifactObjectStore());
}
