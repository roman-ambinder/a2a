using A2A.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Client.Identity.Aws;

/// <summary>Adds <see cref="AwsSigV4Handler" /> to a client from <see cref="A2AClientServiceCollectionExtensions.AddA2AClient" />.</summary>
public static class AwsSigV4ClientBuilderExtensions
{
    /// <summary>
    ///     Signs every request of the client to its agent with AWS Signature Version 4, the card fetch included.
    ///     <see cref="AwsSigV4Options.AgentOrigin" /> defaults to the client's <see cref="A2ANamedClientOptions.AgentUrl" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">The builder is not the one <c>AddA2AClient</c> returned.</exception>
    public static IHttpClientBuilder AddAwsSigV4(this IHttpClientBuilder builder, Action<AwsSigV4Options> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return builder.AddAwsSigV4((_, options) => configure(options));
    }

    /// <summary>
    ///     Signs every request of the client to its agent with AWS Signature Version 4, configured with access to the
    ///     service provider (for example to take <see cref="AwsSigV4Options.Credentials" /> from it).
    ///     <see cref="AwsSigV4Options.AgentOrigin" /> defaults to the client's <see cref="A2ANamedClientOptions.AgentUrl" />.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The builder is not the one <c>AddA2AClient</c> returned, or (when the
    ///     handler is created) neither <c>AgentOrigin</c> nor the client's <c>AgentUrl</c> is set.
    /// </exception>
    public static IHttpClientBuilder AddAwsSigV4(this IHttpClientBuilder builder,
        Action<IServiceProvider, AwsSigV4Options> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var name = builder.GetA2AClientName()
                   ?? throw new InvalidOperationException(
                       "AWS signing needs an A2A client. Call this on the IHttpClientBuilder that AddA2AClient returned.");
        // A message handler, not a credential provider: the card of an agent behind AWS IAM usually declares no scheme.
        return builder.AddHttpMessageHandler(sp =>
        {
            var options = new AwsSigV4Options();
            configure(sp, options);
            options.AgentOrigin ??= sp.GetRequiredService<IOptionsMonitor<A2ANamedClientOptions>>().Get(name).AgentUrl
                                    ?? throw new InvalidOperationException(
                                        $"A2A client '{name}' has no AgentUrl and AwsSigV4Options.AgentOrigin is not set.");
            return new AwsSigV4Handler(options);
        });
    }
}
