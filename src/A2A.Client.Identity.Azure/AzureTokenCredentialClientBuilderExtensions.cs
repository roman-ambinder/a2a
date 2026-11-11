using A2A.Client;
using Azure.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Client.Identity.Azure;

/// <summary>
///     Adds <see cref="AzureTokenCredentialProvider" /> to a client from
///     <see cref="A2AClientServiceCollectionExtensions.AddA2AClient" />.
/// </summary>
public static class AzureTokenCredentialClientBuilderExtensions
{
    /// <summary>
    ///     Adds an Azure credential provider that uses the <see cref="TokenCredential" /> registered in the service
    ///     collection. <see cref="AzureTokenCredentialOptions.AgentOrigin" /> defaults to the client's
    ///     <see cref="A2ANamedClientOptions.AgentUrl" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder is not the one <c>AddA2AClient</c> returned.</exception>
    public static IHttpClientBuilder AddAzureTokenCredential(this IHttpClientBuilder builder,
        Action<AzureTokenCredentialOptions> configure)
    {
        return builder.AddAzureTokenCredential(sp => sp.GetRequiredService<TokenCredential>(), configure);
    }

    /// <summary>
    ///     Adds an Azure credential provider with the credential <paramref name="credential" /> returns.
    ///     <see cref="AzureTokenCredentialOptions.AgentOrigin" /> defaults to the client's
    ///     <see cref="A2ANamedClientOptions.AgentUrl" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder is not the one <c>AddA2AClient</c> returned.</exception>
    public static IHttpClientBuilder AddAzureTokenCredential(this IHttpClientBuilder builder,
        Func<IServiceProvider, TokenCredential> credential, Action<AzureTokenCredentialOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(configure);
        var name = builder.GetA2AClientName()
                   ?? throw new InvalidOperationException(
                       "Credentials need an A2A client. Call this on the IHttpClientBuilder that AddA2AClient returned.");
        return builder.AddCredentialProvider(sp =>
        {
            var options = new AzureTokenCredentialOptions();
            configure(options);
            options.AgentOrigin ??= sp.GetRequiredService<IOptionsMonitor<A2ANamedClientOptions>>().Get(name).AgentUrl;
            return new AzureTokenCredentialProvider(credential(sp), options);
        });
    }
}
