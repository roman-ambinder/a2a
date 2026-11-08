#nullable enable
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using A2A.Client;
using A2A.Integrity;
using A2A.Security.Http;

namespace A2A.Tests;

public sealed class ArtifactDownloaderTests
{
    private static readonly Uri Origin = new("https://agent.example");
    private const string Link = "https://agent.example/artifacts/_/task-1/obj-1";
    private const string StoreLink = "https://store.example/bucket/obj-1?sig=abc";

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class CountingContent(long length, int chunk) : HttpContent
    {
        public long BytesRead;

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => Task.CompletedTask;

        protected override bool TryComputeLength(out long len)
        {
            len = -1;
            return false;
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Source(this, length, chunk));

        private sealed class Source(CountingContent owner, long length, int chunk) : Stream
        {
            private long position;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                var n = (int)Math.Min(Math.Min(buffer.Length, chunk), length - position);
                buffer[..n].Fill(7);
                position += n;
                owner.BytesRead += n;
                return n;
            }

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
        }
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(HttpStatusCode code, string location)
    {
        var response = new HttpResponseMessage(code);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static UrlPart Part(string url = Link, byte[]? content = null, long? size = null, string? sha = null, bool metadata = true)
    {
        var meta = new Dictionary<string, JsonElement>();
        if (metadata && content is not null)
        {
            meta["sha256"] = JsonSerializer.SerializeToElement(sha ?? ArtifactHash.Format(SHA256.HashData(content)));
            meta["sizeBytes"] = JsonSerializer.SerializeToElement(size ?? content.Length);
        }
        return new UrlPart(new Uri(url)) { Metadata = metadata ? meta : null };
    }

    private static A2AArtifactDownloader Downloader(HttpMessageHandler agent, HttpMessageHandler? store = null) =>
        new(new HttpClient(agent), Origin, null, () => new HttpClient(store ?? new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError))));

    [Fact]
    public async Task Foreign_origin_throws_and_sends_nothing()
    {
        var agent = new FakeHandler(_ => Ok([1]));
        var store = new FakeHandler(_ => Ok([1]));
        var downloader = Downloader(agent, store);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            downloader.DownloadAsync(Part("https://evil.example/artifacts/_/task-1/obj-1"), new MemoryStream(), 100, CancellationToken.None));

        Assert.Empty(agent.Requests);
        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task Origin_comparison_ignores_case()
    {
        var agent = new FakeHandler(_ => Ok([1, 2]));
        using var destination = new MemoryStream();

        await Downloader(agent).DownloadAsync(Part("https://AGENT.example/artifacts/_/t/o"), destination, 100, CancellationToken.None);

        Assert.Single(agent.Requests);
    }

    [Fact]
    public async Task Credentialed_request_goes_to_agent_origin()
    {
        var content = "hello"u8.ToArray();
        var agent = new FakeHandler(_ => Ok(content));
        using var destination = new MemoryStream();

        await Downloader(agent).DownloadAsync(Part(content: content), destination, 100, CancellationToken.None);

        var request = Assert.Single(agent.Requests);
        Assert.Equal(new Uri(Link), request.RequestUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(content, destination.ToArray());
    }

    [Fact]
    public async Task Redirect_is_followed_once_without_credentials()
    {
        var content = "stored"u8.ToArray();
        var agent = new FakeHandler(_ => Redirect(HttpStatusCode.Found, StoreLink));
        var store = new FakeHandler(_ => Ok(content));
        using var destination = new MemoryStream();
        var agentClient = new HttpClient(agent);
        agentClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret");
        var downloader = new A2AArtifactDownloader(agentClient, Origin, null, () => new HttpClient(store));

        await downloader.DownloadAsync(Part(content: content), destination, 100, CancellationToken.None);

        Assert.Single(agent.Requests);
        Assert.NotNull(agent.Requests[0].Headers.Authorization);
        var second = Assert.Single(store.Requests);
        Assert.Equal(new Uri(StoreLink), second.RequestUri);
        Assert.Null(second.Headers.Authorization);
        Assert.Equal(content, destination.ToArray());
    }

    [Fact]
    public async Task Second_redirect_throws()
    {
        var agent = new FakeHandler(_ => Redirect(HttpStatusCode.Found, StoreLink));
        var store = new FakeHandler(_ => Redirect(HttpStatusCode.Found, "https://store.example/other"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Downloader(agent, store).DownloadAsync(Part(), new MemoryStream(), 100, CancellationToken.None));

        Assert.Single(store.Requests);
    }

    [Fact]
    public async Task Redirect_to_a_blocked_target_throws_and_sends_nothing()
    {
        var agent = new FakeHandler(_ => Redirect(HttpStatusCode.Found, "http://169.254.169.254/latest"));
        var store = new FakeHandler(_ => Ok([1]));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Downloader(agent, store).DownloadAsync(Part(), new MemoryStream(), 100, CancellationToken.None));

        Assert.Empty(store.Requests);
    }

    [Fact]
    public async Task Agent_client_that_followed_a_redirect_throws()
    {
        var agent = new FakeHandler(request =>
        {
            var response = Ok([1]);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, StoreLink);
            return response;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Downloader(agent).DownloadAsync(Part(), new MemoryStream(), 100, CancellationToken.None));
    }

    [Fact]
    public async Task Not_found_throws_http_request_exception()
    {
        var agent = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Downloader(agent).DownloadAsync(Part(), new MemoryStream(), 100, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Size_mismatch_throws()
    {
        var content = "hello"u8.ToArray();
        var agent = new FakeHandler(_ => Ok(content));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Downloader(agent).DownloadAsync(Part(content: content, size: 99), new MemoryStream(), 100, CancellationToken.None));
    }

    [Fact]
    public async Task Hash_mismatch_throws()
    {
        var content = "hello"u8.ToArray();
        var agent = new FakeHandler(_ => Ok(content));
        var wrong = ArtifactHash.Format(SHA256.HashData("other"u8));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Downloader(agent).DownloadAsync(Part(content: content, sha: wrong), new MemoryStream(), 100, CancellationToken.None));
    }

    [Fact]
    public async Task Over_maxBytes_throws_and_stops_reading()
    {
        var body = new CountingContent(10_000_000, 4096);
        var agent = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = body });

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Downloader(agent).DownloadAsync(Part(metadata: false), new MemoryStream(), 1000, CancellationToken.None));

        Assert.True(body.BytesRead <= 64 * 1024, $"read {body.BytesRead} bytes");
    }

    [Fact]
    public async Task Part_without_metadata_still_downloads()
    {
        var content = "no metadata"u8.ToArray();
        var agent = new FakeHandler(_ => Ok(content));
        using var destination = new MemoryStream();

        await Downloader(agent).DownloadAsync(Part(metadata: false), destination, 100, CancellationToken.None);

        Assert.Equal(content, destination.ToArray());
    }

    [Fact]
    public async Task Malformed_hash_metadata_fails_closed()
    {
        var content = "hello"u8.ToArray();
        var agent = new FakeHandler(_ => Ok(content));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Downloader(agent).DownloadAsync(Part(content: content, sha: "md5:abc"), new MemoryStream(), 100, CancellationToken.None));
    }

    [Fact]
    public void Public_constructor_accepts_storage_options()
    {
        using var http = new HttpClient();
        _ = new A2AArtifactDownloader(http, Origin, new SafeHttpOptions { AllowPrivateNetworks = true });
    }
}
