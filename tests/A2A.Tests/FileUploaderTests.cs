#nullable enable
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using A2A.Client;
using A2A.Integrity;
using A2A.Json;

namespace A2A.Tests;

/// <summary><see cref="A2AFileUploader"/>: it hashes while sending, and trusts neither the answer nor the redirect.</summary>
public sealed class FileUploaderTests
{
    private static readonly Uri UploadUrl = new("https://agent.example/a2a/uploads/_");
    private const string Link = "https://agent.example/a2a/uploads/_/0123456789abcdef0123456789abcdef";

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

        /// <summary>The content headers as they were while the request was sent (the content is disposed afterwards).</summary>
        public List<(long? Length, string? MediaType, string? FileName)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The headers first: reading the content buffers it, and a buffered body has a length.
            if (request.Content is { } c)
                Sent.Add((c.Headers.ContentLength, c.Headers.ContentType?.MediaType, c.Headers.ContentDisposition?.FileNameStar));
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request, body));
            return respond(request, body);
        }
    }

    /// <summary>A stream that cannot seek and gives its data in small pieces.</summary>
    private sealed class OneWayStream(byte[] data) : Stream
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
            var n = Math.Min(Math.Min(count, 7), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }

    private static HttpResponseMessage Created(byte[] body, string link = Link, long? size = null, string? sha = null, bool metadata = true)
    {
        var meta = new Dictionary<string, JsonElement>();
        if (metadata)
        {
            meta["sha256"] = JsonSerializer.SerializeToElement(sha ?? ArtifactHash.Format(SHA256.HashData(body)));
            meta["sizeBytes"] = JsonSerializer.SerializeToElement(size ?? body.Length);
        }
        Part part = new UrlPart(new Uri(link)) { MediaType = "text/plain", Filename = "a.txt", Metadata = meta };
        return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(A2AJson.Serialize(part)) };
    }

    [Fact]
    public async Task Uploads_a_non_seekable_stream_and_checks_the_hash()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 200)).ToArray();
        var handler = new FakeHandler((_, body) => Created(body));

        var part = await new A2AFileUploader(handler, UploadUrl).UploadAsync(new OneWayStream(data), "text/plain", "a b.txt");

        Assert.Equal(new Uri(Link), part.Url);
        var (request, sent) = Assert.Single(handler.Requests);
        Assert.Equal(data, sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(UploadUrl, request.RequestUri);
        var sentHeaders = Assert.Single(handler.Sent);
        Assert.Equal("text/plain", sentHeaders.MediaType);
        Assert.Equal("a b.txt", sentHeaders.FileName);
        Assert.Null(sentHeaders.Length); // unknown length: sent chunked
    }

    [Fact]
    public async Task A_seekable_stream_announces_its_length()
    {
        var data = new byte[300];
        var handler = new FakeHandler((_, body) => Created(body));

        await new A2AFileUploader(handler, UploadUrl).UploadAsync(new MemoryStream(data), "application/octet-stream");

        Assert.Equal(300, Assert.Single(handler.Sent).Length);
    }

    [Fact]
    public async Task A_hash_that_differs_from_the_bytes_sent_throws()
    {
        var handler = new FakeHandler((_, body) => Created(body, sha: "sha256:" + new string('0', 64)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new A2AFileUploader(handler, UploadUrl).UploadAsync(new MemoryStream([1, 2, 3]), "text/plain"));
    }

    [Fact]
    public async Task A_size_that_differs_from_the_bytes_sent_throws()
    {
        var handler = new FakeHandler((_, body) => Created(body, size: body.Length + 1));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new A2AFileUploader(handler, UploadUrl).UploadAsync(new MemoryStream([1, 2, 3]), "text/plain"));
    }

    [Fact]
    public async Task An_answer_without_the_metadata_throws()
    {
        var handler = new FakeHandler((_, body) => Created(body, metadata: false));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new A2AFileUploader(handler, UploadUrl).UploadAsync(new MemoryStream([1, 2, 3]), "text/plain"));
    }

    [Fact]
    public async Task A_link_on_another_origin_throws()
    {
        var handler = new FakeHandler((_, body) => Created(body, link: "https://evil.example/a2a/uploads/_/0123456789abcdef0123456789abcdef"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new A2AFileUploader(handler, UploadUrl).UploadAsync(new MemoryStream([1, 2, 3]), "text/plain"));
    }

    [Fact]
    public void A_handler_that_follows_redirects_is_rejected_at_construction()
    {
        using var bare = new SocketsHttpHandler { AllowAutoRedirect = true };

        Assert.Throws<ArgumentException>(() => new A2AFileUploader(bare, UploadUrl));
    }

    [Fact]
    public async Task A_handler_that_followed_a_redirect_is_refused()
    {
        var handler = new FakeHandler((request, body) =>
        {
            var response = Created(body);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://other.example/x"); // what a following handler leaves
            return response;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new A2AFileUploader(handler, UploadUrl).UploadAsync(new MemoryStream([1]), "text/plain"));
    }

    [Fact]
    public async Task A_redirect_answer_and_an_error_answer_throw()
    {
        var redirect = new FakeHandler((_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://other.example/x");
            return response;
        });
        var refused = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));

        await Assert.ThrowsAsync<HttpRequestException>(() => new A2AFileUploader(redirect, UploadUrl).UploadAsync(new MemoryStream([1]), "text/plain"));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new A2AFileUploader(refused, UploadUrl).UploadAsync(new MemoryStream([1]), "text/plain"));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.StatusCode);
    }
}
