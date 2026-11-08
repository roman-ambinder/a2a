using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Server.Storage.S3;

/// <summary>Registers the S3 artifact object store with the A2A server.</summary>
public static class A2AS3ServiceCollectionExtensions
{
    /// <summary>
    /// Uses <see cref="S3ArtifactObjectStore"/> as the <see cref="ArtifactObjectStore"/>. Works before or after <c>AddA2AServer</c>.
    /// The container disposes the store, and the client the store made; a client passed in the options is yours to dispose.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the bucket and, for an S3-compatible service, its endpoint.</param>
    public static IServiceCollection AddA2AS3ArtifactStore(this IServiceCollection services, Action<S3ArtifactStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new S3ArtifactStoreOptions();
        configure(options);
        services.Replace(ServiceDescriptor.Singleton<ArtifactObjectStore>(_ => new S3ArtifactObjectStore(options)));
        return services;
    }
}
