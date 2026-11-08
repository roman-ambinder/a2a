using A2A.Client;
using A2A.Client.Credentials;
using A2A.Security.Http;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.ArtifactStorage;

internal static class Samples
{
    public static void Register(IServiceCollection services)
    {
        #region Register
        services.AddA2AServer(o =>
        {
            // The base of every artifact link. It must be the origin that clients send credentials to.
            o.ArtifactStorage.PublicBaseUrl = new Uri("https://agent.example.com/a2a");
            o.ArtifactStorage.InlineThresholdBytes = 256 * 1024;
            o.ArtifactStorage.Delivery = ArtifactDelivery.Redirect;
        });

        // For tests and development. A durable provider replaces it in production.
        services.AddSingleton<ArtifactObjectStore>(new InMemoryArtifactObjectStore());

        // The sweep also removes orphan objects. Needed with any durable store.
        services.AddA2ARetention();
        #endregion
    }

    public static void MapRoutes(WebApplication app)
    {
        #region Map
        app.MapA2A("/a2a").RequireAuthorization();
        app.MapA2AArtifacts("/a2a").RequireAuthorization();
        #endregion
    }

    public static void ResolverPerTenant(IServiceCollection services)
    {
        #region Resolver
        services.AddA2AServer(o =>
            o.ArtifactStorage.PublicBaseUrlResolver = tenant => new Uri($"https://{tenant}.agents.example.com/a2a"));
        #endregion
    }

    public static async Task Produce(TaskUpdater updater, string reportPath, byte[] smallImage, CancellationToken ct)
    {
        #region Produce
        // A large file: the bytes go straight to the store, never through an event.
        await using var file = File.OpenRead(reportPath);
        await updater.AddArtifactFromStreamAsync(file, "application/pdf", name: "report.pdf", cancellationToken: ct);

        // A small raw part stays inline. A raw part over InlineThresholdBytes is moved to the store for you.
        await updater.AddArtifactAsync([Part.FromBytes(smallImage, "image/png", "chart.png")], name: "chart", cancellationToken: ct);
        #endregion
    }

    public static async Task Download(Artifact artifact, A2ACredentialBinding binding, CancellationToken ct)
    {
        #region Download
        // The handler adds the same credentials as your A2A calls. It must not follow redirects.
        var handler = new A2ACredentialHandler(binding, new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });
        var downloader = new A2AArtifactDownloader(handler, new Uri("https://agent.example.com/"));

        var part = artifact.Parts.OfType<UrlPart>().First();
        await using var destination = File.Create("report.pdf");
        await downloader.DownloadAsync(part, destination, maxBytes: 1L << 30, ct);
        #endregion
    }

    public static A2AArtifactDownloader DownloadFromPrivateStore(HttpMessageHandler handler)
    {
        #region PrivateStore
        // The agent redirects to a presigned URL. By default the downloader only follows it to a public HTTPS host.
        return new A2AArtifactDownloader(handler, new Uri("https://agent.example.com/"),
            new SafeHttpOptions { AllowPrivateNetworks = true, AllowHttp = true });
        #endregion
    }
}
