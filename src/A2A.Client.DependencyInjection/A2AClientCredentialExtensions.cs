using System.Security.Cryptography.X509Certificates;
using A2A.Client.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Client;

/// <summary>
///     Credential extensions for the builder that <see cref="A2AClientServiceCollectionExtensions.AddA2AClient" />
///     returns. The
///     provider that fits the Agent Card's security requirements is chosen when the client is created. Credentials go only
///     to the
///     card's origin and never follow a redirect. The card fetch carries none.
/// </summary>
public static class A2AClientCredentialExtensions
{
    /// <summary>Adds a credential provider, created from the service provider when the client is created.</summary>
    /// <exception cref="InvalidOperationException">The builder is not the one <c>AddA2AClient</c> returned.</exception>
    public static IHttpClientBuilder AddCredentialProvider(this IHttpClientBuilder builder,
        Func<IServiceProvider, IA2ACredentialProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(factory);
        var name = ClientName(builder);
        builder.Services.Configure<A2ANamedClientOptions>(name, o => o.CredentialProviders.Add(factory));
        return builder;
    }

    /// <summary>Adds an OAuth2 client credentials provider.</summary>
    public static IHttpClientBuilder AddOAuth2ClientCredentials(this IHttpClientBuilder builder,
        Action<OAuth2ClientCredentialsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddOAuth2ClientCredentials((_, o) => configure(o));
    }

    /// <summary>Adds an OAuth2 client credentials provider, configured with access to the service provider.</summary>
    public static IHttpClientBuilder AddOAuth2ClientCredentials(this IHttpClientBuilder builder,
        Action<IServiceProvider, OAuth2ClientCredentialsOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddCredentialProvider(sp =>
        {
            var options = new OAuth2ClientCredentialsOptions();
            configure(sp, options);
            options.Logger ??= sp.GetService<ILoggerFactory>()
                ?.CreateLogger<OAuth2ClientCredentialsProvider>();
            return new OAuth2ClientCredentialsProvider(options);
        });
    }

    /// <summary>Adds an API key provider with a fixed key.</summary>
    public static IHttpClientBuilder AddApiKey(this IHttpClientBuilder builder, string apiKey,
        string? schemeName = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return builder.AddCredentialProvider(_ => new ApiKeyCredentialProvider(apiKey, schemeName));
    }

    /// <summary>Adds an API key provider that fetches the key on every request (cache inside it).</summary>
    public static IHttpClientBuilder AddApiKey(this IHttpClientBuilder builder,
        Func<IServiceProvider, CancellationToken, ValueTask<string>> apiKey, string? schemeName = null)
    {
        ArgumentNullException.ThrowIfNull(apiKey);
        return builder.AddCredentialProvider(sp => new ApiKeyCredentialProvider(ct => apiKey(sp, ct), schemeName));
    }

    /// <summary>
    ///     Adds mutual TLS: the certificate goes to the primary handler, scoped to the host of the client's
    ///     <see cref="A2ANamedClientOptions.AgentUrl" />, which replaces any primary handler set earlier. The caller owns the
    ///     certificate.
    /// </summary>
    /// <exception cref="InvalidOperationException">The client has no <c>AgentUrl</c> (thrown when the handler is created).</exception>
    public static IHttpClientBuilder AddMutualTls(this IHttpClientBuilder builder,
        Func<IServiceProvider, X509Certificate2> certificate, string? schemeName = null)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var name = ClientName(builder);
        builder.ConfigurePrimaryHttpMessageHandler(sp =>
        {
            var url = sp.GetRequiredService<IOptionsMonitor<A2ANamedClientOptions>>().Get(name).AgentUrl
                      ?? throw new InvalidOperationException($"A2A client '{name}' has no AgentUrl.");
            return A2AMutualTls.CreateHandler(certificate(sp), url);
        });
        return builder.AddCredentialProvider(_ => new MutualTlsCredentialProvider(schemeName));
    }

    private static string ClientName(IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return A2AClientServiceCollectionExtensions.TryGetA2AClientName(builder)
               ?? throw new InvalidOperationException(
                   "Credentials need an A2A client. Call this on the IHttpClientBuilder that AddA2AClient returned.");
    }
}