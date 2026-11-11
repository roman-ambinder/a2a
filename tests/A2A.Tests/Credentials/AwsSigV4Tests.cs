using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using A2A.Client;
using A2A.Client.Credentials;
using A2A.Client.Identity.Aws;
using Amazon.Runtime;

namespace A2A.Tests.Credentials;

public class AwsSigV4Tests
{
    private const string AccessKey = "AKIDEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string Rpc = """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{}}""";
    private static readonly DateTimeOffset When = new(2026, 11, 11, 12, 30, 45, TimeSpan.Zero);

    // Reference values from botocore 1.44.0 (botocore.auth.SigV4Auth) with the same keys, time and requests.
    private const string Prefix = "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261111/";

    private sealed class Capture : HttpMessageHandler
    {
        public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();
        public ConcurrentQueue<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            Bodies.Enqueue(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class ThrowingCredentials(Exception ex) : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => throw ex;
        public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromException<ImmutableCredentials>(ex);
    }

    private sealed class CountingCredentials : AWSCredentials
    {
        public int Calls;
        public override ImmutableCredentials GetCredentials()
        {
            Interlocked.Increment(ref Calls);
            return new ImmutableCredentials(AccessKey, SecretKey, null);
        }
    }

    private static AwsSigV4Options Options(string origin = "https://abc123.execute-api.us-east-1.amazonaws.com",
        string region = "us-east-1", string service = "execute-api", AWSCredentials? credentials = null,
        Action<AwsSigV4Options>? configure = null)
    {
        var options = new AwsSigV4Options
        {
            Region = region, Service = service, AgentOrigin = new Uri(origin),
            Credentials = credentials ?? new BasicAWSCredentials(AccessKey, SecretKey),
            TimeProvider = new FakeTimeProvider(When),
        };
        configure?.Invoke(options);
        return options;
    }

    private static async Task<HttpRequestMessage> SendAsync(AwsSigV4Options options, HttpRequestMessage request, Capture? inner = null)
    {
        inner ??= new Capture();
        using var invoker = new HttpMessageInvoker(new AwsSigV4Handler(options, inner));
        using var _ = await invoker.SendAsync(request, default);
        return Assert.Single(inner.Requests);
    }

    private static HttpRequestMessage Post(string url, string body, string contentType)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    private static string Sha256Hex(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Matches_the_AWS_documentation_example()
    {
        // The IAM ListUsers example of the AWS SigV4 documentation, reproduced with botocore.
        var authorization = SigV4.Authorization("GET", new Uri("https://iam.amazonaws.com/?Action=ListUsers&Version=2010-05-08"),
            [new("content-type", "application/x-www-form-urlencoded; charset=utf-8"), new("host", "iam.amazonaws.com"), new("x-amz-date", "20150830T123600Z")],
            SigV4.EmptyPayloadHash, AccessKey, SecretKey, "us-east-1", "iam", new DateTimeOffset(2015, 8, 30, 12, 36, 0, TimeSpan.Zero));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/iam/aws4_request, SignedHeaders=content-type;host;x-amz-date, " +
                     "Signature=5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7", authorization);
    }

    [Fact]
    public async Task Json_rpc_post_matches_botocore()
    {
        var sent = await SendAsync(Options(),
            Post("https://abc123.execute-api.us-east-1.amazonaws.com/prod/a2a", Rpc, "application/json; charset=utf-8"));
        Assert.Equal(Prefix + "us-east-1/execute-api/aws4_request, SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date, " +
                     "Signature=b26a1a748ff5d059921e80e1f4607678174dc39825c9faf8a2a3553f6c9ab720", Header(sent, "Authorization"));
        Assert.Equal("20261111T123045Z", Header(sent, "X-Amz-Date"));
        Assert.Equal(Sha256Hex(Rpc), Header(sent, "X-Amz-Content-SHA256"));
        Assert.Null(Header(sent, "X-Amz-Security-Token"));
    }

    [Fact]
    public async Task Session_token_is_sent_and_signed_like_botocore()
    {
        var options = Options(credentials: new SessionAWSCredentials(AccessKey, SecretKey, "SESSIONTOKEN/EXAMPLE+=="));
        var sent = await SendAsync(options,
            Post("https://abc123.execute-api.us-east-1.amazonaws.com/prod/a2a", Rpc, "application/json; charset=utf-8"));
        Assert.Equal(Prefix + "us-east-1/execute-api/aws4_request, SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-security-token, " +
                     "Signature=55e4d02d781b9af9e8508fb221215780a6bc20702c139befe9f897e39db0eb36", Header(sent, "Authorization"));
        Assert.Equal("SESSIONTOKEN/EXAMPLE+==", Header(sent, "X-Amz-Security-Token"));
    }

    [Fact]
    public async Task Get_with_port_encoded_path_and_repeated_keys_matches_botocore()
    {
        var sent = await SendAsync(Options("https://agent.example.com:8443", "eu-west-1", "lambda"),
            new HttpRequestMessage(HttpMethod.Get, "https://agent.example.com:8443/v1/tasks/task%201?historyLength=5&a=b%20c&a=a"));
        Assert.Equal(Prefix + "eu-west-1/lambda/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
                     "Signature=1690a904816e87d179ff8986bca0871fb65a92c7ee0a010d39826dbf4af85023", Header(sent, "Authorization"));
        Assert.Equal(SigV4.EmptyPayloadHash, Header(sent, "X-Amz-Content-SHA256"));
    }

    [Fact]
    public async Task Special_characters_match_botocore()
    {
        var sent = await SendAsync(Options("https://agent.example.com", "us-west-2"),
            new HttpRequestMessage(HttpMethod.Get, "https://agent.example.com/x/a~b/%C3%A9?q=~%2A&empty=&z=1%2B1"));
        Assert.Equal(Prefix + "us-west-2/execute-api/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, " +
                     "Signature=a01bbbe4f3c2d1806aabfd7c554ea0293b558d88ee3d49e2987e1e9cc704d919", Header(sent, "Authorization"));
    }

    [Fact]
    public async Task UnsignedPayload_matches_botocore()
    {
        var sent = await SendAsync(Options("https://agent.example.com", configure: o => o.UnsignedPayload = true),
            Post("https://agent.example.com/a2a", Rpc, "application/json"));
        Assert.Equal("UNSIGNED-PAYLOAD", Header(sent, "X-Amz-Content-SHA256"));
        Assert.Equal(Prefix + "us-east-1/execute-api/aws4_request, SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date, " +
                     "Signature=da71059328d5b45852b9822e18e936269170d2d9061380f216bbb1e796e8fb18", Header(sent, "Authorization"));
    }

    [Theory]
    [InlineData("?b=2&a=%7e&c&A=%2a", "A=%2A&a=~&b=2&c=")]
    [InlineData("?x=%e2%82%ac&x=b&x=a", "x=%E2%82%AC&x=a&x=b")]
    [InlineData("", "")]
    [InlineData("?&&k=v&", "k=v")]
    public void Query_is_put_in_canonical_form(string query, string expected) =>
        Assert.Equal(expected, SigV4.CanonicalQuery(query));

    [Fact]
    public void Empty_path_is_a_slash_and_S3_is_encoded_once()
    {
        Assert.Equal("/", SigV4.CanonicalPath("", true));
        Assert.Equal("/a%2520b", SigV4.CanonicalPath("/a%20b", true));
        Assert.Equal("/a%20b", SigV4.CanonicalPath("/a%20b", false));
    }

    [Theory]
    [InlineData("https://agent.example.com/a2a", "agent.example.com")]
    [InlineData("https://agent.example.com:8443/a2a", "agent.example.com:8443")]
    [InlineData("http://127.0.0.1:80/a2a", "127.0.0.1")]
    [InlineData("https://[::1]:9000/a2a", "[::1]:9000")]
    [InlineData("https://bücher.example/a2a", "xn--bcher-kva.example")]
    public void Host_is_the_name_the_transport_sends(string url, string expected) =>
        Assert.Equal(expected, AwsSigV4RequestSigner.HostHeader(new Uri(url)));

    [Fact]
    public async Task Requests_to_another_origin_pass_unsigned()
    {
        var sent = await SendAsync(Options(), new HttpRequestMessage(HttpMethod.Get, "https://downloads.example.com/file"));
        Assert.Null(sent.Headers.Authorization);
        Assert.Null(Header(sent, "X-Amz-Date"));
        Assert.Null(Header(sent, "X-Amz-Content-SHA256"));
    }

    [Fact]
    public async Task A_retry_is_signed_again_not_twice()
    {
        var options = Options();
        var time = (FakeTimeProvider)options.TimeProvider;
        var inner = new Capture();
        using var invoker = new HttpMessageInvoker(new AwsSigV4Handler(options, inner));
        using var request = Post("https://abc123.execute-api.us-east-1.amazonaws.com/prod/a2a", Rpc, "application/json");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer old");
        (await invoker.SendAsync(request, default)).Dispose();
        var first = Header(request, "Authorization");
        time.Advance(TimeSpan.FromSeconds(5));
        (await invoker.SendAsync(request, default)).Dispose();

        Assert.Single(request.Headers.GetValues("Authorization"));
        Assert.Single(request.Headers.GetValues("X-Amz-Date"));
        Assert.Equal("20261111T123050Z", Header(request, "X-Amz-Date"));
        Assert.Equal(Sha256Hex(Rpc), Header(request, "X-Amz-Content-SHA256")); // hashed in full again, not an empty read
        Assert.NotEqual(first, Header(request, "Authorization"));
        Assert.StartsWith("AWS4-HMAC-SHA256 ", first, StringComparison.Ordinal);
        Assert.Equal([Rpc, Rpc], inner.Bodies);
    }

    [Fact]
    public async Task A_stream_body_is_buffered_hashed_and_still_sent()
    {
        const string body = "streamed body";
        var request = new HttpRequestMessage(HttpMethod.Post, "https://abc123.execute-api.us-east-1.amazonaws.com/upload")
        {
            Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(body))),
        };
        var inner = new Capture();
        var sent = await SendAsync(Options(), request, inner);
        Assert.Equal(Sha256Hex(body), Header(sent, "X-Amz-Content-SHA256"));
        Assert.Equal(body, Assert.Single(inner.Bodies));
    }

    [Fact]
    public async Task A_stream_larger_than_the_limit_fails_before_sending()
    {
        var inner = new Capture();
        using var invoker = new HttpMessageInvoker(new AwsSigV4Handler(Options(configure: o => o.MaxSignedBodyBytes = 4), inner));
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://abc123.execute-api.us-east-1.amazonaws.com/upload")
        {
            Content = new StreamContent(new NonSeekableStream(new byte[10])),
        };
        var ex = await Assert.ThrowsAsync<A2AException>(() => invoker.SendAsync(request, default));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Contains("UnsignedPayload", ex.Message, StringComparison.Ordinal);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task A_stream_with_a_known_length_over_the_limit_fails_without_reading()
    {
        var inner = new Capture();
        using var invoker = new HttpMessageInvoker(new AwsSigV4Handler(Options(configure: o => o.MaxSignedBodyBytes = 4), inner));
        var content = new StreamContent(new NonSeekableStream(new byte[10]));
        content.Headers.ContentLength = 10;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://abc123.execute-api.us-east-1.amazonaws.com/upload") { Content = content };
        await Assert.ThrowsAsync<A2AException>(() => invoker.SendAsync(request, default));
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Credential_failure_is_Unauthenticated_with_the_type_only()
    {
        var inner = new Capture();
        var options = Options(credentials: new ThrowingCredentials(new InvalidOperationException("profile 'prod' in /home/me/.aws/config")));
        using var invoker = new HttpMessageInvoker(new AwsSigV4Handler(options, inner));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://abc123.execute-api.us-east-1.amazonaws.com/a2a");
        var ex = await Assert.ThrowsAsync<A2AException>(() => invoker.SendAsync(request, default));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Contains("InvalidOperationException", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/me", ex.Message, StringComparison.Ordinal);
        Assert.Empty(inner.Requests);
    }

    [Fact]
    public async Task Credentials_are_read_for_every_request()
    {
        var credentials = new CountingCredentials();
        var inner = new Capture();
        using var invoker = new HttpMessageInvoker(new AwsSigV4Handler(Options(credentials: credentials), inner));
        for (var i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://abc123.execute-api.us-east-1.amazonaws.com/a2a");
            (await invoker.SendAsync(request, default)).Dispose();
        }

        Assert.Equal(3, credentials.Calls);
    }

    [Theory]
    [InlineData("", "execute-api")]
    [InlineData("us-east-1", "")]
    [InlineData("us-east-1/x", "execute-api")]
    [InlineData("us-east-1", "Execute-API")]
    public void Region_and_service_are_checked(string region, string service) =>
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(Options(region: region, service: service)));

    [Fact]
    public void AgentOrigin_is_required_and_must_be_https()
    {
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(Options(configure: o => o.AgentOrigin = null)));
        Assert.Throws<ArgumentException>(() => new AwsSigV4Handler(Options("http://agent.example.com")));
        _ = new AwsSigV4Handler(Options("http://127.0.0.1:8080"));
        _ = new AwsSigV4Handler(Options("http://agent.example.com", configure: o => o.RequireHttps = false));
    }

    [Fact]
    public void A_redirecting_inner_handler_is_refused()
    {
        Assert.Throws<ArgumentException>(() =>
            new AwsSigV4Handler(Options(), new SocketsHttpHandler { AllowAutoRedirect = true }));
        _ = new AwsSigV4Handler(Options(), new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    [Fact]
    public void Provider_needs_a_scheme_name()
    {
        Assert.Throws<ArgumentException>(() => new AwsSigV4CredentialProvider(Options(configure: o => o.AgentOrigin = null)));
    }

    private static A2ACredentialContext Context(string name = "aws", string origin = "https://abc123.execute-api.us-east-1.amazonaws.com/") =>
        new(name, new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "AWS4-HMAC-SHA256" }), [], new Uri(origin));

    [Fact]
    public async Task Provider_handles_its_scheme_name_binds_to_one_origin_and_signs()
    {
        var provider = new AwsSigV4CredentialProvider(Options(configure: o => { o.AgentOrigin = null; o.SchemeName = "aws"; }));
        Assert.False(provider.CanHandle(Context("other")));
        Assert.True(provider.CanHandle(Context()));
        Assert.False(provider.CanHandle(Context(origin: "https://other.example/")));
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            provider.ApplyAsync(new HttpRequestMessage(HttpMethod.Get, "https://other.example/a2a"), Context(origin: "https://other.example/"), default).AsTask());
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);

        using var request = Post("https://abc123.execute-api.us-east-1.amazonaws.com/prod/a2a", Rpc, "application/json; charset=utf-8");
        await provider.ApplyAsync(request, Context(), default);
        Assert.EndsWith("Signature=b26a1a748ff5d059921e80e1f4607678174dc39825c9faf8a2a3553f6c9ab720", Header(request, "Authorization"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_is_chosen_by_card_selection()
    {
        var provider = new AwsSigV4CredentialProvider(Options("https://agent.example", configure: o => o.SchemeName = "aws"));
        var card = Cards.Card(
            schemes: new Dictionary<string, SecurityScheme> { ["aws"] = new HttpAuthScheme(new HttpAuthSecurityScheme { Scheme = "AWS4-HMAC-SHA256" }) },
            requirements: [Cards.Req(("aws", []))]);
        var binding = A2ACredentialBinding.Create(Cards.Resolved(card), card.SupportedInterfaces[0], [provider]);
        var inner = new FakeHandler();
        using var http = new HttpClient(new A2ACredentialHandler(binding, inner));
        using var response = await http.GetAsync(new Uri("https://agent.example/a2a"));
        Assert.True(inner.Requests.TryDequeue(out var sent));
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/", Header(sent!, "Authorization"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task End_to_end_the_card_fetch_and_the_call_arrive_signed()
    {
        await using var origin = await TestOrigin.StartAsync(async (context, o) =>
        {
            if (context.Request.Path == "/.well-known/agent-card.json")
                await TestOrigin.WriteCard(context, TestOrigin.Card(o.Url, [], []));
            else
                await TestOrigin.WriteJson(context, TestOrigin.RpcOk);
        });
        var options = Options(origin.Url.ToString(), configure: o => o.TimeProvider = TimeProvider.System);
        using var http = new HttpClient(new AwsSigV4Handler(options, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }));
        var client = await A2AClientFactory.CreateAsync(origin.Url, http);
        await client.SendMessageAsync("hello");

        var card = Assert.Single(origin.At("/.well-known/agent-card.json"));
        var call = Assert.Single(origin.At("/a2a"));
        Verify(card, "GET", null);
        Verify(call, "POST", call.Header("Content-Type"));

        // Rebuild the signature from what the server received: what was signed is what was sent.
        void Verify(SeenRequest seen, string method, string? contentType)
        {
            var date = seen.Header("X-Amz-Date")!;
            Assert.Equal(Sha256Hex(seen.Body), seen.Header("X-Amz-Content-SHA256"));
            var headers = new List<KeyValuePair<string, string>>
            {
                new("host", seen.Header("Host")!), new("x-amz-date", date), new("x-amz-content-sha256", seen.Header("X-Amz-Content-SHA256")!),
            };
            if (contentType is not null) headers.Add(new("content-type", contentType));
            var when = DateTimeOffset.ParseExact(date, "yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal);
            var expected = SigV4.Authorization(method, new Uri(origin.Url, seen.Path), headers, Sha256Hex(seen.Body),
                AccessKey, SecretKey, "us-east-1", "execute-api", when);
            Assert.Equal(expected, seen.Header("Authorization"));
        }
    }

    /// <summary>A stream that can be read once, like a network or file upload stream.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
