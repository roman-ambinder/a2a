using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using A2A.Client.Credentials;
using static A2A.Tests.Credentials.Cards;

namespace A2A.Tests.Credentials;

public class MutualTlsTests
{
    private static X509Certificate2 NewCert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=client", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static A2ACredentialContext Ctx(SecurityScheme scheme, string name = "mtls") =>
        new(name, scheme, [], new Uri("https://agent.example"));

    private static X509Certificate? Select(SocketsHttpHandler h, string host) =>
        h.SslOptions.LocalCertificateSelectionCallback!(new object(), host, new X509CertificateCollection(), null, []);

    [Fact]
    public void Callback_returns_cert_for_agent_host_only()
    {
        using var cert = NewCert();
        using var h = A2AMutualTls.CreateHandler(cert, new Uri("https://Agent.Example/a2a"));
        Assert.Same(cert, Select(h, "agent.example"));
        Assert.Same(cert, Select(h, "AGENT.EXAMPLE"));
        Assert.Null(Select(h, "other.example"));
        Assert.Null(Select(h, "agent.example.evil.test"));
    }

    [Fact]
    public void Handler_does_not_redirect_or_use_cookies()
    {
        using var cert = NewCert();
        using var h = A2AMutualTls.CreateHandler(cert, new Uri("https://agent.example/a2a"));
        Assert.False(h.AllowAutoRedirect);
        Assert.False(h.UseCookies);
    }

    [Fact]
    public void Certificate_without_private_key_is_refused()
    {
        using var cert = NewCert();
        using var pubOnly = X509Certificate2.CreateFromPem(cert.ExportCertificatePem());
        Assert.Throws<ArgumentException>(() => A2AMutualTls.CreateHandler(pubOnly, new Uri("https://agent.example/")));
    }

    [Fact]
    public void Arguments_are_validated()
    {
        using var cert = NewCert();
        Assert.Throws<ArgumentNullException>(() => A2AMutualTls.CreateHandler(null!, new Uri("https://agent.example/")));
        Assert.Throws<ArgumentNullException>(() => A2AMutualTls.CreateHandler(cert, null!));
        Assert.Throws<ArgumentException>(() => A2AMutualTls.CreateHandler(cert, new Uri("/relative", UriKind.Relative)));
    }

    [Fact]
    public async Task Provider_handles_only_mutual_tls_and_applies_nothing()
    {
        var p = new MutualTlsCredentialProvider();
        var mtls = new MutualTlsScheme(new MutualTlsSecurityScheme());
        Assert.True(p.CanHandle(Ctx(mtls)));
        Assert.False(p.CanHandle(Ctx(SecurityScheme.ApiKey("X-Key"))));
        Assert.False(p.CanHandle(Ctx(SecurityScheme.Bearer())));

        var r = new HttpRequestMessage(HttpMethod.Post, "https://agent.example/a2a");
        await p.ApplyAsync(r, Ctx(mtls), default);
        Assert.Empty(r.Headers);
        Assert.Equal("https://agent.example/a2a", r.RequestUri!.ToString());
    }

    [Fact]
    public void Scheme_name_filter_applies()
    {
        var p = new MutualTlsCredentialProvider("wanted");
        var mtls = new MutualTlsScheme(new MutualTlsSecurityScheme());
        Assert.True(p.CanHandle(Ctx(mtls, "wanted")));
        Assert.False(p.CanHandle(Ctx(mtls, "other")));
    }

    [Fact]
    public void Binding_with_mtls_requirement_selects_the_provider()
    {
        var card = Card(
            schemes: new() { ["mtls"] = new MutualTlsScheme(new MutualTlsSecurityScheme()) },
            requirements: [Req(("mtls", []))]);
        var provider = new MutualTlsCredentialProvider();
        var b = A2ACredentialBinding.Create(Resolved(card), card.SupportedInterfaces[0], [provider]);
        Assert.Equal("mtls", Assert.Single(b.Schemes).SchemeName);
        Assert.Same(provider, b.Pairs.Single().Provider);
    }
}
