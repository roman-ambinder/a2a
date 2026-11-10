using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Server.Storage.AzureBlob;

/// <summary>Registers the Azure Blob artifact object store with the A2A server.</summary>
public static class A2AAzureBlobServiceCollectionExtensions
{
    /// <summary>
    ///     Uses <see cref="AzureBlobArtifactObjectStore" /> as the <see cref="ArtifactObjectStore" />. Works before or after
    ///     <c>AddA2AServer</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the container and the connection string, or a container client.</param>
    public static IServiceCollection AddA2AAzureBlobArtifactStore(this IServiceCollection services,
        Action<AzureBlobArtifactStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AzureBlobArtifactStoreOptions();
        configure(options);
        services.Replace(
            ServiceDescriptor.Singleton<ArtifactObjectStore>(_ => new AzureBlobArtifactObjectStore(options)));
        return services;
    }
}