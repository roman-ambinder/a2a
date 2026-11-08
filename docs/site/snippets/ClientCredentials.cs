using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using A2A.Client;
using A2A.Client.Credentials;
using A2A.Client.Grpc;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.ClientCredentials;

internal static class Snippets
{
    public static async Task OAuth2Async()
    {
        #region OAuth2
        using var oauth = new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions
        {
            ClientId = "my-client",
            ClientSecret = Environment.GetEnvironmentVariable("A2A_CLIENT_SECRET")!,
            // The identity provider is usually on another origin, so name its token endpoint here.
            TokenEndpoint = new Uri("https://idp.example.com/oauth/token"),
            // You choose the scopes. Without this, the card chooses them.
            Scopes = ["agent.invoke"],
        });

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [oauth]);
        #endregion
        _ = client;
    }

    public static async Task ApiKeyAsync()
    {
        #region ApiKey
        // The header or cookie name comes from the card. You supply only the key.
        var fixedKey = new ApiKeyCredentialProvider("my-api-key");

        // Or read the key on every request. Cache inside the delegate.
        var rotating = new ApiKeyCredentialProvider(ct => ValueTask.FromResult(Environment.GetEnvironmentVariable("A2A_API_KEY")!));

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [fixedKey]);
        #endregion
        _ = rotating;
        _ = client;
    }

    public static async Task MutualTlsAsync()
    {
        #region MutualTls
        var agentUrl = new Uri("https://agent.example.com/");
        var certificate = X509CertificateLoader.LoadPkcs12FromFile("client.pfx", password: null);

        // The certificate belongs to the TLS connection. The handler offers it only to the host of agentUrl.
        var handler = A2AMutualTls.CreateHandler(certificate, agentUrl);

        var client = await A2AClientFactory.CreateAsync(agentUrl, [new MutualTlsCredentialProvider()], handler);
        #endregion
        _ = client;
    }

    #region Custom
    // Handles a card that asks for an HTTP Bearer scheme and gets the token from your own source.
    internal sealed class VaultTokenProvider(Func<CancellationToken, ValueTask<string>> getToken) : IA2ACredentialProvider
    {
        public bool CanHandle(A2ACredentialContext context) =>
            context.Scheme is HttpAuthScheme { Value.Scheme: "Bearer" };

        // Runs on every request, retries included. Do not change request.RequestUri.
        public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken)
        {
            var token = await getToken(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
    #endregion

    public static async Task OrderAsync(IA2ACredentialProvider oauth)
    {
        #region Order
        // The first requirement of the card that these providers can meet wins. Within one requirement, the first provider that
        // can handle a scheme wins.
        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"),
        [
            oauth,
            new ApiKeyCredentialProvider("my-api-key"),
        ]);
        #endregion
        _ = client;
    }

    public static async Task GrpcAsync(HttpClient plainHttp, IReadOnlyList<IA2ACredentialProvider> providers)
    {
        #region Grpc
        // Resolve the card without credentials.
        var resolver = new A2ACardResolver(plainHttp);
        var resolved = await resolver.ResolveAsync(new Uri("https://agent.example.com/"));
        var grpcInterface = resolved.Card.SupportedInterfaces.First(i => i.ProtocolBinding == ProtocolBindings.Grpc);

        // Pick the credentials once. forGrpcChannel: true allows a gRPC interface. The origin and HTTPS checks still apply.
        var binding = A2ACredentialBinding.Create(resolved, grpcInterface, providers, forGrpcChannel: true);

        // The handler under the channel must not follow redirects.
        var handler = new A2ACredentialHandler(binding, new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false });

        var client = A2AGrpcClientFactory.Create(resolved.Card, channelOptions: new GrpcChannelOptions { HttpHandler = handler });
        #endregion
        _ = client;
    }

    public static void Di(IServiceCollection services, X509Certificate2 certificate)
    {
        #region DiOAuth
        services.AddA2AClient("weather", o => o.AgentUrl = new Uri("https://agent.example.com/"))
            .AddOAuth2ClientCredentials(o =>
            {
                o.ClientId = "my-client";
                o.ClientSecret = Environment.GetEnvironmentVariable("A2A_CLIENT_SECRET")!;
                o.TokenEndpoint = new Uri("https://idp.example.com/oauth/token");
                o.Scopes = ["agent.invoke"];
            });
        #endregion

        #region DiOthers
        // An API key.
        services.AddA2AClient("search", o => o.AgentUrl = new Uri("https://search.example.com/"))
            .AddApiKey("my-api-key");

        // Mutual TLS. This sets the primary handler of the named client, scoped to the host of AgentUrl.
        services.AddA2AClient("payments", o => o.AgentUrl = new Uri("https://pay.example.com/"))
            .AddMutualTls(sp => certificate);

        // Your own provider.
        services.AddA2AClient("vault", o => o.AgentUrl = new Uri("https://vault.example.com/"))
            .AddCredentialProvider(sp => new VaultTokenProvider(ct => ValueTask.FromResult("token")));
        #endregion
    }
}
