using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using A2A.Client;
using A2A.Client.Credentials;
using A2A.Client.Identity.Aws;
using A2A.Client.Identity.Azure;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Signing;
using Amazon.Runtime;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Snippets.IdentityProviders;

internal sealed class HelloAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext ctx, TaskUpdater updater, CancellationToken ct) =>
        await updater.CompleteAsync("hello", ct);
}

#region EntraResolver
// Entra: "sub" is different for each application, "oid" is the same user or service principal in every app of a
// tenant. "tid" plus "oid" is the stable owner id.
internal sealed class EntraPrincipalResolver : IPrincipalResolver
{
    public string? Resolve(ClaimsPrincipal user)
    {
        var tid = user.FindFirst("tid")?.Value;
        var oid = user.FindFirst("oid")?.Value;
        return string.IsNullOrEmpty(tid) || string.IsNullOrEmpty(oid) ? null : $"{tid}/{oid}";
    }
}
#endregion

internal static class Samples
{
    public static void EntraServer()
    {
        #region EntraServer
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                // Your tenant, and the application id URI of the agent's app registration.
                o.Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0";
                o.Audience = "api://my-agent";
                // Keep claim names as they are in the token ("oid", "tid", "roles"), not mapped to long URIs.
                o.MapInboundClaims = false;
            });
        builder.Services.AddAuthorizationBuilder()
            // An app role you define on the agent's app registration and assign to callers.
            .AddPolicy("A2ACaller", p => p.RequireClaim("roles", "Agent.Invoke"));
        builder.Services.AddA2A(a => a
            .AddAgentExecutor<HelloAgent>()
            .AddAgentCard(new AgentCard { Name = "My Agent", Description = "Says hello.", Version = "1.0.0" }));
        builder.Services.AddSingleton<IPrincipalResolver, EntraPrincipalResolver>();

        var app = builder.Build();
        app.UseAuthentication();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/a2a", o => o.AuthorizationPolicy = "A2ACaller");
        #endregion
    }

    public static async Task AzureManagedIdentityAsync()
    {
        #region AzureManagedIdentity
        // Install RomanAmbinder.A2A.Client.Identity.Azure and Azure.Identity.
        var options = new AzureTokenCredentialOptions();
        options.Scopes.Add("api://my-agent/.default");   // you choose the audience; card scopes are not used

        // Any TokenCredential works: ManagedIdentityCredential, WorkloadIdentityCredential, DefaultAzureCredential, ...
        var entra = new AzureTokenCredentialProvider(new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned), options);

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [entra]);
        #endregion
        _ = client;
    }

    public static void AzureDi(IServiceCollection services)
    {
        #region AzureDi
        // Uses the TokenCredential registered here. AgentOrigin defaults to AgentUrl.
        services.AddSingleton<Azure.Core.TokenCredential>(new DefaultAzureCredential());
        services.AddA2AClient("weather", o => o.AgentUrl = new Uri("https://agent.example.com/"))
            .AddAzureTokenCredential(o => o.Scopes.Add("api://weather-agent/.default"));
        #endregion
    }

    public static async Task FederatedTokenFileAsync()
    {
        #region FederatedTokenFile
        // A token file the platform writes and rotates: a Kubernetes projected service account token, or the file that
        // Azure workload identity names in AZURE_FEDERATED_TOKEN_FILE. No secret is stored anywhere.
        using var oauth = new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions
        {
            ClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")!,
            ClientAssertion = OAuth2ClientAssertions.FromFile(Environment.GetEnvironmentVariable("AZURE_FEDERATED_TOKEN_FILE")!),
            TokenEndpoint = new Uri($"https://login.microsoftonline.com/{Environment.GetEnvironmentVariable("AZURE_TENANT_ID")}/oauth2/v2.0/token"),
            Scopes = ["api://my-agent/.default"],
        });

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [oauth]);
        #endregion
        _ = client;
    }

    public static async Task PrivateKeyJwtAsync()
    {
        #region PrivateKeyJwt
        // private_key_jwt: the client proves itself with its own key. Register the public key (key.ToPublicJwk()) with
        // the identity provider. The JWT header names the key by "kid".
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new AgentCardSigningKey(ec, keyId: "client-key-1", algorithm: "ES256");

        using var oauth = new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions
        {
            ClientId = "my-client",
            ClientAssertion = OAuth2ClientAssertions.PrivateKeyJwt(key),
            TokenEndpoint = new Uri("https://idp.example.com/oauth/token"),
            Scopes = ["agent.invoke"],
        });

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [oauth]);
        #endregion
        _ = client;
    }

    public static async Task CertificateJwtAsync()
    {
        #region CertificateJwt
        // Entra ID certificate credentials: upload the certificate (no private key) to the app registration.
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile("client.pfx", password: null);

        using var oauth = new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions
        {
            ClientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID")!,
            // PS256, and the x5t#S256 header that names the certificate.
            ClientAssertion = OAuth2ClientAssertions.CertificateJwt(certificate),
            TokenEndpoint = new Uri($"https://login.microsoftonline.com/{Environment.GetEnvironmentVariable("AZURE_TENANT_ID")}/oauth2/v2.0/token"),
            Scopes = ["api://my-agent/.default"],
        });

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [oauth]);
        #endregion
        _ = client;
    }

    public static async Task AwsSigV4Async()
    {
        #region AwsSigV4
        // Install RomanAmbinder.A2A.Client.Identity.Aws. An agent behind API Gateway with IAM authorization.
        var agentUrl = new Uri("https://abc123.execute-api.us-east-1.amazonaws.com/prod/");
        var sigV4 = new AwsSigV4Handler(new AwsSigV4Options
        {
            Region = "us-east-1",
            Service = "execute-api",   // "lambda" for a Lambda function URL
            AgentOrigin = agentUrl,    // only requests to this origin are signed
            // Credentials = ...       // null: the AWS SDK's default credential search
        }, new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });

        // The card fetch and every call go through the handler, so both are signed.
        var client = await A2AClientFactory.CreateAsync(agentUrl, new HttpClient(sigV4));
        #endregion
        _ = client;
    }

    public static void AwsSigV4Di(IServiceCollection services)
    {
        #region AwsSigV4Di
        // Signs the card fetch and every call to AgentUrl's origin. AgentOrigin defaults to AgentUrl.
        services.AddA2AClient("orders", o => o.AgentUrl = new Uri("https://abc123.execute-api.us-east-1.amazonaws.com/prod/"))
            .AddAwsSigV4(o =>
            {
                o.Region = "us-east-1";
                o.Service = "execute-api";
            });
        #endregion
    }

    public static async Task AwsSigV4SchemeAsync()
    {
        #region AwsSigV4Scheme
        // A card that declares a scheme for SigV4 under a name you agreed on with the agent, for example
        // "aws": { "httpAuthSecurityScheme": { "scheme": "AWS4-HMAC-SHA256" } }.
        var aws = new AwsSigV4CredentialProvider(new AwsSigV4Options
        {
            Region = "eu-west-1",
            Service = "lambda",
            SchemeName = "aws",
            Credentials = new BasicAWSCredentials("AKIDEXAMPLE", Environment.GetEnvironmentVariable("AWS_SECRET_ACCESS_KEY")),
        });

        var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), [aws]);
        #endregion
        _ = client;
    }

    #region CustomIdp
    // Any other identity provider or token source: implement IA2ACredentialProvider. This one asks a token service of
    // your own for a token. Cache inside GetTokenAsync: ApplyAsync runs on every request.
    internal sealed class MyIdpProvider(Uri agentOrigin, Func<CancellationToken, ValueTask<string>> getTokenAsync)
        : IA2ACredentialProvider
    {
        public bool CanHandle(A2ACredentialContext context) =>
            context.Scheme is OAuth2Scheme or OpenIdConnectScheme or HttpAuthScheme { Value.Scheme: "Bearer" }
            // A token is for one agent. Do not hand it to another one.
            && A2ACredentialBinding.IsSameOrigin(context.CardOrigin, agentOrigin);

        public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context, CancellationToken cancellationToken) =>
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await getTokenAsync(cancellationToken));
    }
    #endregion
}
