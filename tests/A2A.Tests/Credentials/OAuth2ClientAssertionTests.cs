using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using A2A.Client.Credentials;
using A2A.Signing;

namespace A2A.Tests.Credentials;

public class OAuth2ClientAssertionTests
{
    private static readonly Uri Origin = new("https://agent.example/");
    private static readonly Uri Idp = new("https://login.idp.example/tenant/oauth2/v2.0/token");

    private sealed record Seen(string? Authorization, Dictionary<string, string> Form);

    private sealed class TokenHandler : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<Seen> Requests { get; } = new();
        public Func<int, HttpResponseMessage> Respond { get; set; } = n => Json(HttpStatusCode.OK,
            $$"""{"access_token":"tok{{n}}","token_type":"Bearer","expires_in":3600}""");

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var n = Interlocked.Increment(ref _calls);
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&').Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            Requests.Enqueue(new Seen(request.Headers.Authorization?.ToString(), form));
            return Respond(n);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static A2ACredentialContext OAuth(string tokenUrl = "https://agent.example/oauth/token") =>
        new("oauth", new OAuth2Scheme(new OAuth2SecurityScheme
        {
            Flows = new OAuthFlows { ClientCredentials = new ClientCredentialsOAuthFlow { TokenUrl = tokenUrl } },
        }), [], Origin);

    private static (OAuth2ClientCredentialsProvider Provider, TokenHandler Handler, FakeTimeProvider Time) Make(
        Func<OAuth2ClientAssertionContext, CancellationToken, ValueTask<string>> assertion,
        Action<OAuth2ClientCredentialsOptions>? configure = null)
    {
        var handler = new TokenHandler();
        var time = new FakeTimeProvider();
        var options = new OAuth2ClientCredentialsOptions
        {
            ClientId = "id", ClientAssertion = assertion, TokenEndpoint = Idp, TokenHandler = handler, TimeProvider = time,
        };
        configure?.Invoke(options);
        return (new OAuth2ClientCredentialsProvider(options), handler, time);
    }

    private static Func<OAuth2ClientAssertionContext, CancellationToken, ValueTask<string>> Fixed(string value) =>
        (_, _) => ValueTask.FromResult(value);

    private static async Task<string?> Apply(OAuth2ClientCredentialsProvider p, A2ACredentialContext? ctx = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://agent.example/a2a");
        await p.ApplyAsync(request, ctx ?? OAuth(), default);
        return request.Headers.Authorization?.ToString();
    }

    private static async Task<A2AException> Fails(OAuth2ClientCredentialsProvider p)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(() => Apply(p));
        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        return ex;
    }

    [Fact]
    public async Task Token_request_carries_the_assertion_and_no_secret()
    {
        var (p, h, _) = Make(Fixed("header.payload.sig"));
        Assert.Equal("Bearer tok1", await Apply(p));
        var seen = Assert.Single(h.Requests);
        Assert.Null(seen.Authorization);
        Assert.Equal("client_credentials", seen.Form["grant_type"]);
        Assert.Equal("id", seen.Form["client_id"]);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", seen.Form["client_assertion_type"]);
        Assert.Equal("header.payload.sig", seen.Form["client_assertion"]);
        Assert.False(seen.Form.ContainsKey("client_secret"));
    }

    [Fact]
    public async Task ClientAuthentication_is_ignored_with_an_assertion()
    {
        var (p, h, _) = Make(Fixed("a.b.c"), o => o.ClientAuthentication = OAuth2ClientAuthentication.ClientSecretPost);
        await Apply(p);
        var seen = Assert.Single(h.Requests);
        Assert.Null(seen.Authorization);
        Assert.False(seen.Form.ContainsKey("client_secret"));
    }

    [Fact]
    public void Secret_and_assertion_together_are_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() => new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions
        {
            ClientId = "id", ClientSecret = "secret", ClientAssertion = Fixed("a.b.c"),
        }));
        Assert.Contains("not both", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_secret_nor_assertion_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions { ClientId = "id" }));
        Assert.Contains("ClientSecret or ClientAssertion", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("client_assertion")]
    [InlineData("client_assertion_type")]
    public void Assertion_parameters_are_reserved(string name)
    {
        var options = new OAuth2ClientCredentialsOptions { ClientId = "id", ClientAssertion = Fixed("a.b.c") };
        options.AdditionalParameters[name] = "x";
        Assert.Throws<ArgumentException>(() => new OAuth2ClientCredentialsProvider(options));
    }

    [Fact]
    public async Task Assertion_is_fetched_per_token_request_and_not_for_a_cached_token()
    {
        var calls = 0;
        OAuth2ClientAssertionContext? seen = null;
        var (p, h, time) = Make((c, _) =>
        {
            seen = c;
            return ValueTask.FromResult($"a.b.{Interlocked.Increment(ref calls)}");
        });
        await Apply(p);
        await Apply(p);
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(3540));
        Assert.Equal("Bearer tok2", await Apply(p));
        Assert.Equal(2, calls);
        Assert.Equal(["a.b.1", "a.b.2"], h.Requests.Select(r => r.Form["client_assertion"]));
        Assert.Equal("id", seen!.ClientId);
        Assert.Equal(Idp, seen.TokenEndpoint);
    }

    [Fact]
    public async Task Card_token_url_on_the_card_origin_is_the_assertion_audience()
    {
        OAuth2ClientAssertionContext? seen = null;
        var (p, _, _) = Make((c, _) =>
        {
            seen = c;
            return ValueTask.FromResult("a.b.c");
        }, o => o.TokenEndpoint = null);
        await Apply(p);
        Assert.Equal(new Uri("https://agent.example/oauth/token"), seen!.TokenEndpoint);
    }

    [Fact]
    public async Task Assertion_echoed_in_an_error_is_redacted()
    {
        const string assertion = "eyJhbGciOiJFUzI1NiJ9.eyJzdWIiOiJpZCJ9.c2ln";
        var (p, h, _) = Make(Fixed(assertion));
        h.Respond = _ => Json(HttpStatusCode.BadRequest,
            $$"""{"error":"invalid_client","error_description":"bad assertion {{assertion}}"}""");
        var ex = await Fails(p);
        Assert.DoesNotContain(assertion, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("invalid_client", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b c")]
    [InlineData("a.b\nc")]
    public async Task Bad_assertion_fails_with_Unauthenticated_before_the_request(string value)
    {
        var (p, h, _) = Make(Fixed(value));
        await Fails(p);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Oversized_assertion_fails()
    {
        var (p, h, _) = Make(Fixed(new string('a', 64 * 1024 + 1)));
        var ex = await Fails(p);
        Assert.Contains("longer than", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task Throwing_assertion_fails_with_the_type_only()
    {
        var (p, h, _) = Make((_, _) => throw new IOException("secret path /var/run/token"));
        var ex = await Fails(p);
        Assert.Contains("IOException", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("/var/run", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, h.Calls);
    }

    [Fact]
    public async Task FromFile_rereads_a_rotated_file_and_trims()
    {
        var path = Path.Combine(Path.GetTempPath(), "a2a-assertion-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "  first.token.value\n");
            var read = OAuth2ClientAssertions.FromFile(path);
            var context = new OAuth2ClientAssertionContext("id", Idp);
            Assert.Equal("first.token.value", await read(context, default));
            await File.WriteAllTextAsync(path, "second.token.value");
            Assert.Equal("second.token.value", await read(context, default));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FromFile_refuses_a_large_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "a2a-assertion-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, new string('a', 64 * 1024 + 1));
            var read = OAuth2ClientAssertions.FromFile(path);
            await Assert.ThrowsAsync<InvalidDataException>(() => read(new OAuth2ClientAssertionContext("id", Idp), default).AsTask());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FromFile_through_the_provider_sends_the_file_content()
    {
        var path = Path.Combine(Path.GetTempPath(), "a2a-assertion-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(path, "k8s.service.account\n");
            var (p, h, _) = Make(OAuth2ClientAssertions.FromFile(path));
            await Apply(p);
            Assert.Equal("k8s.service.account", Assert.Single(h.Requests).Form["client_assertion"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PrivateKeyJwt_is_signed_and_carries_the_required_claims()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new AgentCardSigningKey(ec, "key-1", "ES256");
        var time = new FakeTimeProvider();
        var make = OAuth2ClientAssertions.PrivateKeyJwt(key, TimeSpan.FromMinutes(2), time);

        var jwt = await make(new OAuth2ClientAssertionContext("client-1", Idp), default);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.True(JwsAlgorithms.Verify("ES256", ec, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.Decode(parts[2])));

        using var header = JsonDocument.Parse(Base64Url.Decode(parts[0]));
        Assert.Equal("ES256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
        Assert.Equal("key-1", header.RootElement.GetProperty("kid").GetString());

        using var payload = JsonDocument.Parse(Base64Url.Decode(parts[1]));
        var claims = payload.RootElement;
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal("client-1", claims.GetProperty("iss").GetString());
        Assert.Equal("client-1", claims.GetProperty("sub").GetString());
        Assert.Equal(Idp.AbsoluteUri, claims.GetProperty("aud").GetString());
        Assert.Equal(now, claims.GetProperty("iat").GetInt64());
        Assert.Equal(now, claims.GetProperty("nbf").GetInt64());
        Assert.Equal(now + 120, claims.GetProperty("exp").GetInt64());
        Assert.Equal(16, Base64Url.Decode(claims.GetProperty("jti").GetString()!).Length);
    }

    [Fact]
    public async Task PrivateKeyJwt_has_a_new_jti_each_time()
    {
        using var rsa = RSA.Create(2048);
        var make = OAuth2ClientAssertions.PrivateKeyJwt(new AgentCardSigningKey(rsa, "k", "RS256"));
        var context = new OAuth2ClientAssertionContext("id", Idp);
        static string Jti(string jwt)
        {
            using var doc = JsonDocument.Parse(Base64Url.Decode(jwt.Split('.')[1]));
            return doc.RootElement.GetProperty("jti").GetString()!;
        }
        Assert.NotEqual(Jti(await make(context, default)), Jti(await make(context, default)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3601)]
    public void PrivateKeyJwt_lifetime_is_bounded(int seconds)
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OAuth2ClientAssertions.PrivateKeyJwt(new AgentCardSigningKey(ec, "k", "ES256"), TimeSpan.FromSeconds(seconds)));
    }

    private static X509Certificate2 RsaCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=a2a-test-client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static X509Certificate2 EcCertificate()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=a2a-test-client", ec, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static (JsonElement Header, JsonElement Claims, bool Verified) Decode(string jwt, string alg, AsymmetricAlgorithm publicKey)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var verified = JwsAlgorithms.Verify(alg, publicKey, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Base64Url.Decode(parts[2]));
        return (JsonDocument.Parse(Base64Url.Decode(parts[0])).RootElement.Clone(),
            JsonDocument.Parse(Base64Url.Decode(parts[1])).RootElement.Clone(), verified);
    }

    [Fact]
    public async Task CertificateJwt_defaults_to_PS256_with_the_SHA256_thumbprint()
    {
        using var certificate = RsaCertificate();
        var time = new FakeTimeProvider();
        var jwt = await OAuth2ClientAssertions.CertificateJwt(certificate, timeProvider: time)(new OAuth2ClientAssertionContext("client-1", Idp), default);
        using var publicKey = certificate.GetRSAPublicKey()!;
        var (header, claims, verified) = Decode(jwt, "PS256", publicKey);

        Assert.True(verified);
        Assert.Equal("PS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(Base64Url.Encode(SHA256.HashData(certificate.RawData)), header.GetProperty("x5t#S256").GetString());
        Assert.False(header.TryGetProperty("x5t", out _));
        Assert.False(header.TryGetProperty("kid", out _));
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        Assert.Equal("client-1", claims.GetProperty("iss").GetString());
        Assert.Equal("client-1", claims.GetProperty("sub").GetString());
        Assert.Equal(Idp.AbsoluteUri, claims.GetProperty("aud").GetString());
        Assert.Equal(now, claims.GetProperty("nbf").GetInt64());
        Assert.Equal(now + 300, claims.GetProperty("exp").GetInt64());
        Assert.Equal(16, Base64Url.Decode(claims.GetProperty("jti").GetString()!).Length);
    }

    [Fact]
    public async Task CertificateJwt_can_send_the_SHA1_thumbprint_as_x5t()
    {
        using var certificate = RsaCertificate();
        var jwt = await OAuth2ClientAssertions.CertificateJwt(certificate, "RS256", OAuth2CertificateThumbprint.Sha1)(
            new OAuth2ClientAssertionContext("client-1", Idp), default);
        using var publicKey = certificate.GetRSAPublicKey()!;
        var (header, _, verified) = Decode(jwt, "RS256", publicKey);

        Assert.True(verified);
        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        // x5t is the SHA-1 thumbprint, the same bytes as X509Certificate2.Thumbprint (hex).
        Assert.Equal(Convert.FromHexString(certificate.Thumbprint), Base64Url.Decode(header.GetProperty("x5t").GetString()!));
        Assert.False(header.TryGetProperty("x5t#S256", out _));
    }

    [Fact]
    public async Task CertificateJwt_signs_with_an_EC_certificate()
    {
        using var certificate = EcCertificate();
        var jwt = await OAuth2ClientAssertions.CertificateJwt(certificate, "ES256")(new OAuth2ClientAssertionContext("client-1", Idp), default);
        using var publicKey = certificate.GetECDsaPublicKey()!;
        Assert.True(Decode(jwt, "ES256", publicKey).Verified);
    }

    [Fact]
    public void CertificateJwt_refuses_a_key_that_does_not_fit_the_algorithm()
    {
        using var rsa = RsaCertificate();
        using var ec = EcCertificate();
        Assert.Throws<ArgumentException>(() => OAuth2ClientAssertions.CertificateJwt(rsa, "ES256"));
        Assert.Throws<ArgumentException>(() => OAuth2ClientAssertions.CertificateJwt(ec, "PS256"));
        Assert.Throws<ArgumentException>(() => OAuth2ClientAssertions.CertificateJwt(rsa, "HS256"));
    }

    [Fact]
    public void CertificateJwt_refuses_a_certificate_without_a_private_key()
    {
        using var withKey = RsaCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.RawData);
        var ex = Assert.Throws<ArgumentException>(() => OAuth2ClientAssertions.CertificateJwt(publicOnly));
        Assert.Contains("private key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CertificateJwt_through_the_provider_reaches_the_token_endpoint()
    {
        using var certificate = RsaCertificate();
        var (p, h, _) = Make(OAuth2ClientAssertions.CertificateJwt(certificate));
        Assert.Equal("Bearer tok1", await Apply(p));
        var assertion = Assert.Single(h.Requests).Form["client_assertion"];
        using var publicKey = certificate.GetRSAPublicKey()!;
        var (header, claims, verified) = Decode(assertion, "PS256", publicKey);
        Assert.True(verified);
        Assert.True(header.TryGetProperty("x5t#S256", out _));
        Assert.Equal(Idp.AbsoluteUri, claims.GetProperty("aud").GetString());
    }
}
