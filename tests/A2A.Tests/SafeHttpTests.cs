using System.Net;
using A2A.Security.Http;

namespace A2A.Tests;

/// <summary>
///     <see cref="SafeHttp" />: the URL policy checked before connecting, and the limits <see cref="SafeHttp.DownloadAsync" />
///     applies to the response. The connect-time address guard is covered in <see cref="InteropHardeningTests" />.
/// </summary>
public class SafeHttpTests
{
    private static readonly Uri PublicUrl = new("https://files.example/a");

    // ---- EnsureAllowed ----

    [Fact]
    public void Ensure_allowed_needs_a_url() =>
        Assert.Throws<ArgumentNullException>(() => SafeHttp.EnsureAllowed(null!));

    [Theory]
    [InlineData("http://files.example/a")]
    [InlineData("ftp://files.example/a")]
    [InlineData("/relative/path")]
    public void Refused_scheme_names_https_only_by_default(string url)
    {
        var ex = Assert.Throws<HttpRequestException>(() => SafeHttp.EnsureAllowed(new Uri(url, UriKind.RelativeOrAbsolute)));
        Assert.Equal("URL must be an absolute https URL.", ex.Message);
    }

    [Theory]
    [InlineData("ftp://files.example/a")]
    [InlineData("/relative/path")]
    public void Refused_scheme_names_http_too_when_http_is_allowed(string url)
    {
        var ex = Assert.Throws<HttpRequestException>(() =>
            SafeHttp.EnsureAllowed(new Uri(url, UriKind.RelativeOrAbsolute), new SafeHttpOptions { AllowHttp = true }));
        Assert.Equal("URL must be an absolute http(s) URL.", ex.Message);
    }

    [Fact]
    public void Http_is_allowed_when_asked() =>
        SafeHttp.EnsureAllowed(new Uri("http://files.example/a"), new SafeHttpOptions { AllowHttp = true });

    [Fact]
    public void User_info_is_refused()
    {
        var ex = Assert.Throws<HttpRequestException>(() => SafeHttp.EnsureAllowed(new Uri("https://user:pw@files.example/a")));
        Assert.Equal("URL must not contain user info.", ex.Message);
    }

    [Theory]
    [InlineData("https://127.0.0.1/a", "127.0.0.1")]
    [InlineData("https://10.1.2.3/a", "10.1.2.3")]
    [InlineData("https://[::1]/a", "[::1]")]
    [InlineData("https://2130706433/a", "127.0.0.1")] // Uri reads the decimal form as 127.0.0.1
    public void Private_literal_is_refused_and_named(string url, string host)
    {
        var ex = Assert.Throws<HttpRequestException>(() => SafeHttp.EnsureAllowed(new Uri(url)));
        Assert.Equal($"URL host '{host}' is a private address.", ex.Message);
    }

    [Fact]
    public void Private_literal_is_allowed_with_private_networks_on() =>
        SafeHttp.EnsureAllowed(new Uri("https://127.0.0.1/a"), new SafeHttpOptions { AllowPrivateNetworks = true });

    [Fact]
    public void Allowed_network_lets_only_its_own_addresses_through()
    {
        var options = new SafeHttpOptions();
        options.AllowedNetworks.Add((IPAddress.Parse("10.0.0.0"), 8));

        SafeHttp.EnsureAllowed(new Uri("https://10.1.2.3/a"), options);
        Assert.Throws<HttpRequestException>(() => SafeHttp.EnsureAllowed(new Uri("https://192.168.1.1/a"), options));
    }

    [Fact]
    public void Public_literal_is_allowed() => SafeHttp.EnsureAllowed(new Uri("https://93.184.215.14/a"));

    [Fact]
    public void Ambiguous_numeric_host_is_refused_and_named()
    {
        // Five numeric labels: not an address Uri parses, but some resolvers read such names as one.
        var ex = Assert.Throws<HttpRequestException>(() => SafeHttp.EnsureAllowed(new Uri("https://1.2.3.4.5/a")));
        Assert.Equal("URL host '1.2.3.4.5' is an ambiguous numeric address.", ex.Message);
    }

    // ---- CreateClient / CreateHandler ----

    [Fact]
    public void Client_times_out_after_30_seconds()
    {
        using var http = SafeHttp.CreateClient();
        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
    }

    [Fact]
    public async Task Connect_time_refusal_names_the_url()
    {
        using var http = SafeHttp.CreateClient();
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            SafeHttp.DownloadAsync(http, new Uri("https://localhost:1/x"), 1024, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("URL host 'localhost' resolves to a private address.", Messages(ex));
    }

    [Fact]
    public async Task Private_networks_option_reaches_the_handler()
    {
        // Allowed through the guard, so the failure is the closed port, not the private address.
        using var http = SafeHttp.CreateClient(new SafeHttpOptions { AllowPrivateNetworks = true });
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            SafeHttp.DownloadAsync(http, new Uri("https://localhost:1/x"), 1024,
                new SafeHttpOptions { AllowPrivateNetworks = true }, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("private address", Messages(ex));
    }

    // ---- DownloadAsync: arguments and the URL policy ----

    [Fact]
    public async Task Download_needs_a_client() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => SafeHttp.DownloadAsync(null!, PublicUrl, 10));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Download_needs_a_positive_limit(long maxBytes)
    {
        var handler = new StubHandler(_ => Ok(new byte[1]));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SafeHttp.DownloadAsync(http, PublicUrl, maxBytes));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Refused_url_is_never_requested()
    {
        var handler = new StubHandler(_ => Ok(new byte[1]));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => SafeHttp.DownloadAsync(http, new Uri("https://127.0.0.1/a"), 10));
        Assert.Equal(0, handler.Calls);
    }

    // ---- DownloadAsync: status codes ----

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(307)]
    [InlineData(308)]
    [InlineData(399)]
    public async Task Redirect_is_refused(int status)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Headers = { Location = new Uri("https://169.254.169.254/latest") }
        }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SafeHttp.DownloadAsync(http, PublicUrl, 10));
        Assert.Equal("URL redirected; redirects are not followed.", ex.Message);
        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Error_status_is_refused_with_its_code(int status)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SafeHttp.DownloadAsync(http, PublicUrl, 10));
        Assert.Equal($"URL returned {status}.", ex.Message);
        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(203)]
    [InlineData(299)]
    public async Task Success_status_returns_the_body(int status)
    {
        using var http = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage((HttpStatusCode)status) { Content = new ByteArrayContent([1, 2, 3]) }));
        Assert.Equal([1, 2, 3], await SafeHttp.DownloadAsync(http, PublicUrl, 10));
    }

    // ---- DownloadAsync: body size ----

    [Fact]
    public async Task Declared_length_at_the_limit_is_read()
    {
        using var http = new HttpClient(new StubHandler(_ => Ok(Bytes(10))));
        Assert.Equal(Bytes(10), await SafeHttp.DownloadAsync(http, PublicUrl, 10));
    }

    [Fact]
    public async Task Declared_length_over_the_limit_is_refused_before_reading()
    {
        var content = new StreamContent(new UnreadableStream());
        content.Headers.ContentLength = 11;
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SafeHttp.DownloadAsync(http, PublicUrl, 10));
        Assert.Equal("URL body is larger than 10 bytes.", ex.Message);
    }

    [Fact]
    public async Task Undeclared_length_at_the_limit_is_read()
    {
        using var http = new HttpClient(new StubHandler(_ => Chunked(Bytes(10))));
        Assert.Equal(Bytes(10), await SafeHttp.DownloadAsync(http, PublicUrl, 10));
    }

    [Fact]
    public async Task Undeclared_length_over_the_limit_is_refused()
    {
        using var http = new HttpClient(new StubHandler(_ => Chunked(Bytes(11))));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => SafeHttp.DownloadAsync(http, PublicUrl, 10));
        Assert.Equal("URL body is larger than 10 bytes.", ex.Message);
    }

    [Fact]
    public async Task Body_spanning_several_reads_is_returned_whole()
    {
        // Larger than the 16 KiB read buffer, served in uneven pieces.
        var body = Bytes(40_000);
        using var http = new HttpClient(new StubHandler(_ => Chunked(body, pieceSize: 7_000)));
        Assert.Equal(body, await SafeHttp.DownloadAsync(http, PublicUrl, body.Length));
    }

    [Fact]
    public async Task Body_spanning_several_reads_over_the_limit_is_refused()
    {
        var body = Bytes(40_000);
        using var http = new HttpClient(new StubHandler(_ => Chunked(body, pieceSize: 7_000)));
        await Assert.ThrowsAsync<HttpRequestException>(() => SafeHttp.DownloadAsync(http, PublicUrl, body.Length - 1));
    }

    [Fact]
    public async Task Empty_body_is_returned_empty()
    {
        using var http = new HttpClient(new StubHandler(_ => Chunked([])));
        Assert.Empty(await SafeHttp.DownloadAsync(http, PublicUrl, 10));
    }

    // ---- helpers ----

    private static byte[] Bytes(int count) => Enumerable.Range(0, count).Select(i => (byte)(i * 31 + 7)).ToArray();

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    // A body with no Content-Length: StreamContent over a stream that cannot seek.
    private static HttpResponseMessage Chunked(byte[] body, int pieceSize = int.MaxValue)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new PiecewiseStream(body, pieceSize))
        };
        Assert.Null(response.Content.Headers.ContentLength);
        return response;
    }

    private static string Messages(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" | ", parts);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    // Non-seekable; each read returns at most pieceSize bytes.
    private sealed class PiecewiseStream(byte[] data, int pieceSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, pieceSize), data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Fails any read: proves a body was refused from its headers alone.
    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The body was read although its declared length is over the limit.");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
