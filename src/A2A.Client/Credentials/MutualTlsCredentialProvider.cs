using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace A2A.Client.Credentials;

/// <summary>
/// Satisfies the card's <c>mutualTLS</c> scheme. The certificate lives in the transport, not in a header, so this provider
/// adds nothing to the request. Build the transport with <see cref="A2AMutualTls.CreateHandler"/>.
/// </summary>
/// <param name="schemeName">When set, the provider only handles the card scheme with this name.</param>
public sealed class MutualTlsCredentialProvider(string? schemeName = null) : IA2ACredentialProvider
{
    /// <inheritdoc />
    public bool CanHandle(A2ACredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Scheme is MutualTlsScheme
            && (schemeName is null || string.Equals(schemeName, context.SchemeName, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Builds transports that present a client certificate.</summary>
public static class A2AMutualTls
{
    /// <summary>
    /// Creates a handler that presents <paramref name="certificate"/> only to the host of <paramref name="agentUrl"/>.
    /// The handler does not follow redirects and does not use cookies. The caller owns the certificate.
    /// </summary>
    /// <param name="certificate">The client certificate. It must have a private key.</param>
    /// <param name="agentUrl">The agent's absolute URL.</param>
    public static SocketsHttpHandler CreateHandler(X509Certificate2 certificate, Uri agentUrl)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(agentUrl);
        if (!agentUrl.IsAbsoluteUri) throw new ArgumentException("The agent URL must be absolute.", nameof(agentUrl));
        if (!certificate.HasPrivateKey) throw new ArgumentException("The certificate must have a private key.", nameof(certificate));

        var host = agentUrl.IdnHost;
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                LocalCertificateSelectionCallback = (_, targetHost, _, _, _) =>
                    string.Equals(targetHost, host, StringComparison.OrdinalIgnoreCase) ? certificate : null!,
            },
        };
    }
}
