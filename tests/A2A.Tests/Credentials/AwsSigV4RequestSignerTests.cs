using System.Net.Http.Headers;
using A2A.Client.Identity.Aws;
using Amazon.Runtime;

namespace A2A.Tests.Credentials;

/// <summary>
///     <see cref="AwsSigV4RequestSigner" />: option checks and their messages, the headers it replaces, the Host it signs,
///     the body limit around <see cref="AwsSigV4Options.MaxSignedBodyBytes" />, and unusable credentials. The signature
///     values themselves are pinned to botocore in <see cref="AwsSigV4Tests" />.
/// </summary>
public class AwsSigV4RequestSignerTests
{
    private const string AccessKey = "AKIDEXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private const string Agent = "https://abc123.execute-api.us-east-1.amazonaws.com";
    private static readonly DateTimeOffset When = new(2026, 11, 11, 12, 30, 45, TimeSpan.Zero);

    private const string TooLargeMessage =
        "The request body is larger than MaxSignedBodyBytes (4 bytes), so it cannot be hashed for SigV4. " +
        "Raise MaxSignedBodyBytes, or set UnsignedPayload if the service accepts unsigned payloads.";

    private static AwsSigV4Options Options(Action<AwsSigV4Options>? configure = null)
    {
        var options = new AwsSigV4Options
        {
            Region = "us-east-1", Service = "execute-api", AgentOrigin = new Uri(Agent),
            Credentials = new BasicAWSCredentials(AccessKey, SecretKey),
            TimeProvider = new FakeTimeProvider(When)
        };
        configure?.Invoke(options);
        return options;
    }

    private static AwsSigV4RequestSigner Signer(Action<AwsSigV4Options>? configure = null) => new(Options(configure), true);

    private static string Header(HttpRequestMessage request, string name) => Assert.Single(request.Headers.GetValues(name));

    // ---- options ----

    [Fact]
    public void Options_are_required() =>
        Assert.Throws<ArgumentNullException>(() => new AwsSigV4RequestSigner(null!, false));

    [Theory]
    [InlineData("", "us-east-1", "Region", "Region is required.")]
    [InlineData("us-east-1", "", "Service", "Service is required.")]
    [InlineData("US-EAST-1", "execute-api", "Region", "Region may contain only lower-case letters, digits and '-'.")]
    [InlineData("us_east_1", "execute-api", "Region", "Region may contain only lower-case letters, digits and '-'.")]
    [InlineData("us-east-1", "execute api", "Service", "Service may contain only lower-case letters, digits and '-'.")]
    [InlineData("us-east-1", "lambda/", "Service", "Service may contain only lower-case letters, digits and '-'.")]
    public void Region_and_service_are_checked_with_a_message(string region, string service, string param, string message)
    {
        var ex = Assert.Throws<ArgumentException>(() => Signer(o => { o.Region = region; o.Service = service; }));
        Assert.Equal(param, ex.ParamName);
        Assert.StartsWith(message, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("az09-")]   // the edges of each allowed range
    [InlineData("z")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("a")]
    public void Lower_case_letters_digits_and_dash_are_accepted(string name) =>
        _ = Signer(o => { o.Region = name; o.Service = name; });

    [Fact]
    public void Agent_origin_is_required_when_asked_for()
    {
        var ex = Assert.Throws<ArgumentException>(() => new AwsSigV4RequestSigner(Options(o => o.AgentOrigin = null), true));
        Assert.StartsWith("AgentOrigin is required: the handler signs only requests to this agent.", ex.Message, StringComparison.Ordinal);
        Assert.Equal("options", ex.ParamName);

        Assert.Null(new AwsSigV4RequestSigner(Options(o => o.AgentOrigin = null), false).AgentOrigin);
    }

    [Theory]
    [InlineData("ftp://agent.example")]
    [InlineData("file:///agent")]
    public void Agent_origin_must_be_http_or_https(string origin)
    {
        var ex = Assert.Throws<ArgumentException>(() => Signer(o => o.AgentOrigin = new Uri(origin)));
        Assert.StartsWith("AgentOrigin must be an absolute http or https URL.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Agent_origin_must_be_absolute()
    {
        var ex = Assert.Throws<ArgumentException>(() => Signer(o => o.AgentOrigin = new Uri("agent/a2a", UriKind.Relative)));
        Assert.StartsWith("AgentOrigin must be an absolute http or https URL.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_http_origin_is_refused_unless_https_is_not_required()
    {
        var ex = Assert.Throws<ArgumentException>(() => Signer(o => o.AgentOrigin = new Uri("http://agent.example")));
        Assert.StartsWith("AgentOrigin must use HTTPS. Loopback is exempt.", ex.Message, StringComparison.Ordinal);

        _ = Signer(o => { o.AgentOrigin = new Uri("http://agent.example"); o.RequireHttps = false; });
        _ = Signer(o => o.AgentOrigin = new Uri("http://localhost:8080"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Body_limit_must_be_positive(long limit)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Signer(o => o.MaxSignedBodyBytes = limit));
        Assert.Equal("MaxSignedBodyBytes", ex.ParamName);
    }

    [Fact]
    public void Time_provider_is_required() =>
        Assert.Throws<ArgumentNullException>(() => Signer(o => o.TimeProvider = null!));

    // ---- the request ----

    [Fact]
    public async Task Request_without_a_uri_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, (Uri?)null);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Signer().SignAsync(request, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("The request has no RequestUri to sign.", ex.Message);
    }

    [Fact]
    public async Task Earlier_signer_headers_are_replaced_not_added_to()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Agent + "/a2a");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer old");
        request.Headers.TryAddWithoutValidation("X-Amz-Date", "19990101T000000Z");
        request.Headers.TryAddWithoutValidation("X-Amz-Content-SHA256", "old");
        request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", "old-token");

        await Signer().SignAsync(request, TestContext.Current.CancellationToken);

        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261111/us-east-1/execute-api/aws4_request, ",
            Header(request, "Authorization"), StringComparison.Ordinal);
        Assert.Equal("20261111T123045Z", Header(request, "X-Amz-Date"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Header(request, "X-Amz-Content-SHA256"));
        // No session token in these credentials: the old one must not survive.
        Assert.False(request.Headers.Contains("X-Amz-Security-Token"));
    }

    [Fact]
    public async Task Session_token_replaces_an_earlier_one()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Agent + "/a2a");
        request.Headers.TryAddWithoutValidation("X-Amz-Security-Token", "old-token");

        await Signer(o => o.Credentials = new SessionAWSCredentials(AccessKey, SecretKey, "session-token"))
            .SignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("session-token", Header(request, "X-Amz-Security-Token"));
        Assert.Contains("x-amz-security-token", Header(request, "Authorization"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explicit_host_header_is_the_one_signed()
    {
        var signer = Signer();
        using var plain = new HttpRequestMessage(HttpMethod.Get, Agent + "/a2a");
        using var withHost = new HttpRequestMessage(HttpMethod.Get, Agent + "/a2a");
        withHost.Headers.Host = "gateway.example";

        await signer.SignAsync(plain, TestContext.Current.CancellationToken);
        await signer.SignAsync(withHost, TestContext.Current.CancellationToken);

        Assert.NotEqual(Header(plain, "Authorization"), Header(withHost, "Authorization"));
    }

    // ---- body limit ----

    private static HttpContent Stream(int bytes, long? declaredLength = null)
    {
        var content = new StreamContent(new OneShotStream(new byte[bytes]));
        if (declaredLength is { } length) content.Headers.ContentLength = length;
        return content;
    }

    [Theory]
    [InlineData(2, 2L)]   // declared and below the limit
    [InlineData(4, 4L)]   // declared at the limit
    [InlineData(4, null)] // undeclared at the limit
    public async Task Stream_body_up_to_the_limit_is_hashed(int bytes, long? declared)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Agent + "/upload") { Content = Stream(bytes, declared) };

        await Signer(o => o.MaxSignedBodyBytes = 4).SignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[bytes])),
            Header(request, "X-Amz-Content-SHA256"));
        Assert.Equal(bytes, (await request.Content!.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
    }

    [Fact]
    public async Task Declared_length_over_the_limit_is_refused_before_reading()
    {
        var content = new StreamContent(new UnreadableStream());
        content.Headers.ContentLength = 5;
        using var request = new HttpRequestMessage(HttpMethod.Post, Agent + "/upload") { Content = content };

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            Signer(o => o.MaxSignedBodyBytes = 4).SignAsync(request, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Equal(TooLargeMessage, ex.Message);
        Assert.Null(ex.InnerException); // refused from the header, not from a failed read
    }

    [Fact]
    public async Task Undeclared_length_over_the_limit_is_refused_after_buffering()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Agent + "/upload") { Content = Stream(5) };

        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            Signer(o => o.MaxSignedBodyBytes = 4).SignAsync(request, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(TooLargeMessage, ex.Message);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Byte_array_body_over_the_limit_is_still_hashed()
    {
        // The limit bounds buffering of streams; a byte array is in memory already.
        using var request = new HttpRequestMessage(HttpMethod.Post, Agent + "/upload") { Content = new ByteArrayContent(new byte[10]) };

        await Signer(o => o.MaxSignedBodyBytes = 4).SignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(System.Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(new byte[10])),
            Header(request, "X-Amz-Content-SHA256"));
    }

    [Fact]
    public async Task Unsigned_payload_skips_the_limit_and_the_read()
    {
        var content = new StreamContent(new UnreadableStream());
        content.Headers.ContentLength = 5;
        using var request = new HttpRequestMessage(HttpMethod.Post, Agent + "/upload") { Content = content };

        await Signer(o => { o.MaxSignedBodyBytes = 4; o.UnsignedPayload = true; }).SignAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("UNSIGNED-PAYLOAD", Header(request, "X-Amz-Content-SHA256"));
    }

    [Fact]
    public async Task Content_type_is_signed()
    {
        var signer = Signer();
        using var json = new HttpRequestMessage(HttpMethod.Post, Agent + "/a2a") { Content = new ByteArrayContent([1]) };
        json.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        using var text = new HttpRequestMessage(HttpMethod.Post, Agent + "/a2a") { Content = new ByteArrayContent([1]) };
        text.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/plain");

        await signer.SignAsync(json, TestContext.Current.CancellationToken);
        await signer.SignAsync(text, TestContext.Current.CancellationToken);

        Assert.Contains("SignedHeaders=content-type;host;", Header(json, "Authorization"), StringComparison.Ordinal);
        Assert.NotEqual(Header(json, "Authorization"), Header(text, "Authorization"));
    }

    // ---- credentials ----

    [Fact]
    public async Task Missing_credentials_are_unauthenticated()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Agent + "/a2a");
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            Signer(o => o.Credentials = new FixedCredentials(null)).SignAsync(request, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal("The AWS credentials have no access key or secret key.", ex.Message);
        Assert.False(request.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Credential_lookup_error_names_only_the_type()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Agent + "/a2a");
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            Signer(o => o.Credentials = new FailingCredentials()).SignAsync(request, TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal("The AWS credentials could not be obtained (InvalidOperationException).", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task Cancellation_during_the_credential_lookup_is_not_wrapped()
    {
        using var cts = new CancellationTokenSource();
        using var request = new HttpRequestMessage(HttpMethod.Get, Agent + "/a2a");
        var pending = Signer(o => o.Credentials = new PendingCredentials()).SignAsync(request, cts.Token).AsTask();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // ---- agent origin ----

    [Theory]
    [InlineData("https://agent.example", "https://agent.example/a2a", true)]
    [InlineData("https://agent.example", "https://AGENT.example/a2a", true)]
    [InlineData("https://agent.example", "https://agent.example:443/a2a", true)]
    [InlineData("https://agent.example", "https://agent.example:8443/a2a", false)]
    [InlineData("https://agent.example", "http://agent.example/a2a", false)]
    [InlineData("https://agent.example", "https://other.example/a2a", false)]
    [InlineData("https://agent.example", "https://sub.agent.example/a2a", false)]
    public void Agent_origin_matches_scheme_host_and_port(string origin, string request, bool expected) =>
        Assert.Equal(expected, AwsSigV4RequestSigner.IsAgentOrigin(new Uri(origin), new Uri(request)));

    [Fact]
    public void No_agent_origin_matches_nothing() =>
        Assert.False(AwsSigV4RequestSigner.IsAgentOrigin(null, new Uri("https://agent.example/a2a")));

    // ---- helpers ----

    private sealed class FixedCredentials(ImmutableCredentials? credentials) : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => credentials!;
        public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(credentials!);
    }

    private sealed class FailingCredentials : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => throw new InvalidOperationException("profile 'prod'");
        public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromException<ImmutableCredentials>(new InvalidOperationException("profile 'prod'"));
    }

    private sealed class PendingCredentials : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => throw new NotSupportedException();
        public override Task<ImmutableCredentials> GetCredentialsAsync() => new TaskCompletionSource<ImmutableCredentials>().Task;
    }

    // Non-seekable, so StreamContent reports no length; reads once.
    private sealed class OneShotStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    }

    private sealed class UnreadableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The body was read although its declared length is over the limit.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
