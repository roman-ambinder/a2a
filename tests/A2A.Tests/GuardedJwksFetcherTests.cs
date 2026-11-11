using System.Net;
using System.Net.Http.Headers;
using System.Text;
using A2A.Security.Http;
using A2A.Signing;

namespace A2A.Tests;

/// <summary>
///     <see cref="GuardedJwksFetcher" />: status codes, the 256 KiB cap with and without a declared length, read errors,
///     the 10 second timeout, the cache lifetime read from the response, and the guarded default client.
/// </summary>
public class GuardedJwksFetcherTests
{
    private const int MaxBytes = 256 * 1024;
    private static readonly Uri Jku = new("https://keys.example/jwks");
    private const string KeySetJson = """{"keys":[{"kty":"EC","kid":"k1","crv":"P-256","x":"eA","y":"eQ"}]}""";

    private static GuardedJwksFetcher Fetcher(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
        new(new HttpMessageInvoker(new Stub(respond)));

    private static GuardedJwksFetcher Fetcher(Func<HttpResponseMessage> respond) => Fetcher((_, _) => Task.FromResult(respond()));

    private static HttpResponseMessage Ok(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private static HttpResponseMessage Ok(string json) => Ok(new StringContent(json, Encoding.UTF8, "application/json"));

    // A JSON key set padded with spaces to exactly `length` bytes, sent without a Content-Length.
    private static HttpResponseMessage Streamed(int length)
    {
        var bytes = Encoding.UTF8.GetBytes("""{"keys":[]}""".PadRight(length));
        var response = Ok(new StreamContent(new NonSeekableStream(bytes)));
        Assert.Null(response.Content.Headers.ContentLength);
        return response;
    }

    // ---- result ----

    [Fact]
    public async Task Key_set_is_read_from_the_body()
    {
        var keys = await Fetcher(() => Ok(KeySetJson)).FetchAsync(Jku, TestContext.Current.CancellationToken);

        var key = Assert.Single(keys.Keys);
        Assert.Equal("k1", key.Kid);
        Assert.Equal("P-256", key.Crv);
    }

    [Fact]
    public async Task Null_body_is_an_empty_key_set() =>
        Assert.Empty((await Fetcher(() => Ok("null")).FetchAsync(Jku, TestContext.Current.CancellationToken)).Keys);

    [Fact]
    public async Task Request_is_a_get_of_the_jku()
    {
        HttpRequestMessage? seen = null;
        await Fetcher((request, _) =>
        {
            seen = request;
            return Task.FromResult(Ok(KeySetJson));
        }).FetchAsync(Jku, TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Equal(Jku, seen.RequestUri);
    }

    [Fact]
    public async Task Jku_is_required() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            Fetcher(() => Ok(KeySetJson)).FetchWithLifetimeAsync(null!, TestContext.Current.CancellationToken).AsTask());

    [Fact]
    public async Task Lifetime_comes_from_the_response()
    {
        var result = await Fetcher(() =>
        {
            var response = Ok(KeySetJson);
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromMinutes(5) };
            return response;
        }).FetchWithLifetimeAsync(Jku, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromMinutes(5), result.MaxAge);
        Assert.Single(result.Keys.Keys);
    }

    // ---- status codes ----

    [Theory]
    [InlineData(301)]
    [InlineData(304)]
    [InlineData(404)]
    [InlineData(500)]
    public async Task Status_other_than_success_is_refused_with_its_code(int status)
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Fetcher(() => new HttpResponseMessage((HttpStatusCode)status)).FetchAsync(Jku, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal($"JWKS fetch returned {status}.", ex.Message);
        Assert.Equal((HttpStatusCode)status, ex.StatusCode);
    }

    // ---- size cap ----

    [Fact]
    public async Task Declared_length_over_the_cap_is_refused_before_reading()
    {
        var content = new StreamContent(new UnreadableStream());
        content.Headers.ContentLength = MaxBytes + 1;
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Fetcher(() => Ok(content)).FetchAsync(Jku, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("JWKS response exceeds 262144 bytes.", ex.Message);
    }

    [Fact]
    public async Task Declared_length_at_the_cap_is_read()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"keys":[]}""".PadRight(MaxBytes));
        Assert.Empty((await Fetcher(() => Ok(new ByteArrayContent(bytes))).FetchAsync(Jku, TestContext.Current.CancellationToken)).Keys);
    }

    [Fact]
    public async Task Streamed_body_at_the_cap_is_read() =>
        Assert.Empty((await Fetcher(() => Streamed(MaxBytes)).FetchAsync(Jku, TestContext.Current.CancellationToken)).Keys);

    [Fact]
    public async Task Streamed_body_over_the_cap_is_refused()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Fetcher(() => Streamed(MaxBytes + 1)).FetchAsync(Jku, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("JWKS response exceeds 262144 bytes.", ex.Message);
    }

    // ---- read errors and timeout ----

    [Fact]
    public async Task Read_error_is_an_http_request_exception()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            Fetcher(() => Ok(new StreamContent(new FailingStream()))).FetchAsync(Jku, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("JWKS fetch failed.", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact(Timeout = 60_000)]
    public async Task Fetch_that_never_answers_is_cancelled_after_ten_seconds()
    {
        var started = DateTimeOffset.UtcNow;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Fetcher(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return Ok(KeySetJson);
            }).FetchAsync(Jku, TestContext.Current.CancellationToken).AsTask());
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(9), "cancelled before the 10 second timeout");
    }

    // ---- default client ----

    [Fact]
    public async Task Default_client_refuses_private_addresses_and_names_the_jwks_host()
    {
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            new GuardedJwksFetcher().FetchAsync(new Uri("https://localhost:1/jwks"), TestContext.Current.CancellationToken).AsTask());

        var messages = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException) messages.Add(e.Message);
        Assert.Contains("JWKS host 'localhost' resolves to a private address.", messages);
    }

    // ---- MaxAgeOf ----

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static HttpResponseMessage Response(Action<HttpResponseMessage> configure)
    {
        var response = Ok("{}");
        configure(response);
        return response;
    }

    [Fact]
    public void No_cache_headers_give_no_lifetime() =>
        Assert.Null(GuardedJwksFetcher.MaxAgeOf(Response(_ => { }), Now));

    [Fact]
    public void No_store_is_zero() =>
        Assert.Equal(TimeSpan.Zero, GuardedJwksFetcher.MaxAgeOf(Response(r =>
            r.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, MaxAge = TimeSpan.FromMinutes(5) }), Now));

    [Fact]
    public void No_cache_is_zero() =>
        Assert.Equal(TimeSpan.Zero, GuardedJwksFetcher.MaxAgeOf(Response(r =>
            r.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, MaxAge = TimeSpan.FromMinutes(5) }), Now));

    [Theory]
    [InlineData(600, null, 600)]
    [InlineData(600, 100, 500)]
    [InlineData(600, 600, 0)]
    [InlineData(600, 900, 0)]
    public void Max_age_less_age(int maxAge, int? age, int expected) =>
        Assert.Equal(TimeSpan.FromSeconds(expected), GuardedJwksFetcher.MaxAgeOf(Response(r =>
        {
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(maxAge) };
            if (age is { } a) r.Headers.Age = TimeSpan.FromSeconds(a);
        }), Now));

    [Fact]
    public void Max_age_wins_over_expires() =>
        Assert.Equal(TimeSpan.FromSeconds(60), GuardedJwksFetcher.MaxAgeOf(Response(r =>
        {
            r.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
            r.Content.Headers.Expires = Now.AddHours(1);
        }), Now));

    [Fact]
    public void Expires_is_measured_from_the_date_header_not_from_now()
    {
        // Date and Expires five minutes apart; "now" an hour later must not shorten the lifetime.
        var date = Now.AddHours(-1);
        Assert.Equal(TimeSpan.FromMinutes(5), GuardedJwksFetcher.MaxAgeOf(Response(r =>
        {
            r.Headers.Date = date;
            r.Content.Headers.Expires = date.AddMinutes(5);
        }), Now));
    }

    [Fact]
    public void Expires_without_a_date_is_measured_from_now() =>
        Assert.Equal(TimeSpan.FromMinutes(3), GuardedJwksFetcher.MaxAgeOf(Response(r => r.Content.Headers.Expires = Now.AddMinutes(3)), Now));

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public void Expires_not_after_the_date_is_zero(int offsetSeconds) =>
        Assert.Equal(TimeSpan.Zero, GuardedJwksFetcher.MaxAgeOf(Response(r =>
        {
            r.Headers.Date = Now;
            r.Content.Headers.Expires = Now.AddSeconds(offsetSeconds);
        }), Now));

    // ---- helpers ----

    private sealed class Stub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private abstract class ReadOnlyStream : Stream
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

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class NonSeekableStream(byte[] data) : ReadOnlyStream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(count, data.Length - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }

    private sealed class FailingStream : ReadOnlyStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");
    }

    private sealed class UnreadableStream : ReadOnlyStream
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The body was read although its declared length is over the cap.");
    }
}
