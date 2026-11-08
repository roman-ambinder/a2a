using System.Security.Cryptography;
using A2A.Client;
using A2A.Client.Cards;
using A2A.Json;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Agents;
using A2A.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.DocSnippets.AgentCardSigning;

internal static class Snippets
{
    public static void ServerKey(IServiceCollection services)
    {
        #region ServerKey
        var ec = ECDsa.Create();
        ec.ImportFromPem(File.ReadAllText("card-signing-key.pem"));
        var signingKey = new AgentCardSigningKey(ec, "echo-1", "ES256", "https://agent.example.com/.well-known/jwks.json");

        services.ConfigureA2AHttp(h => h.SigningKey = signingKey);
        #endregion
    }

    #region TenantKey
    public sealed class MyCards : TenantCardProvider
    {
        private readonly AgentCardSigningKey _tenantKey = LoadKey();

        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentCard?>(new AgentCard
            {
                Name = "Agent of " + scope.Tenant,
                Description = "One agent per tenant.",
                Version = "1.0.0",
                DefaultInputModes = ["text/plain"],
                DefaultOutputModes = ["text/plain"],
                Skills = [new AgentSkill { Id = "chat", Name = "Chat", Description = "Talks", Tags = ["chat"] }],
            });

        // Return null for a tenant to use the host's key (A2AHttpOptions.SigningKey), or no signature when there is none.
        public override ValueTask<AgentCardSigningKey?> GetSigningKeyAsync(string tenant, CancellationToken cancellationToken) =>
            ValueTask.FromResult<AgentCardSigningKey?>(_tenantKey);

        private static AgentCardSigningKey LoadKey()
        {
            var ec = ECDsa.Create();
            ec.ImportFromPem(File.ReadAllText("tenant-signing-key.pem"));
            return new AgentCardSigningKey(ec, "tenant-1", "ES256");
        }
    }
    #endregion

    public static void RegisterTenantProvider(IServiceCollection services)
    {
        services.AddA2A(a => a.AddTenantCardProvider<MyCards>());
    }

    public static void ClientPinnedKey(IServiceCollection services, Uri agentUrl)
    {
        #region PinnedKey
        // The public key set the agent serves at /.well-known/jwks.json, saved when you set the agent up.
        var jwks = A2AJson.Deserialize<JwkSet>(File.ReadAllText("echo-agent-jwks.json"));

        services.AddA2AClient("echo", o =>
        {
            o.AgentUrl = agentUrl;
            o.Resolver.Trust.TrustedKeys[CardTrustPolicy.OriginOf(agentUrl)] = jwks;
        });
        #endregion
    }

    public static void ClientJkuHost(A2ACardResolverOptions options)
    {
        #region JkuHost
        options.Trust.AllowJkuHost("https://agent.example.com", "keys.example.com");
        #endregion
    }

    public static async Task ResolveAsync(HttpClient http, Uri agentUrl, JwkSet jwks)
    {
        #region Resolve
        var resolver = new A2ACardResolver(http, new A2ACardResolverOptions
        {
            Trust = { TrustedKeys = { [CardTrustPolicy.OriginOf(agentUrl)] = jwks } },
            PinPolicy = PinPolicy.Strict,
        });
        resolver.CardChanged += (_, e) => Console.WriteLine($"Card of {e.Origin} changed: {e.OldHash} -> {e.NewHash}");

        try
        {
            var resolved = await resolver.ResolveAsync(agentUrl);

            Console.WriteLine(resolved.Verification.Status);                // Verified
            Console.WriteLine(resolved.Identity);                           // (origin, kid)
            Console.WriteLine(resolved.Description);                        // sanitized UntrustedText
            Console.WriteLine(resolved.Skills[0].Name.Raw);                 // as received: use with care
        }
        catch (AgentCardVerificationException ex)
        {
            Console.WriteLine($"{ex.Result.Status}: {ex.Result.Reason}");
        }
        catch (AgentCardPinException ex)
        {
            Console.WriteLine($"Card of {ex.Origin} changed since it was pinned.");
        }
        #endregion
    }

    public static void Rotation(A2ACardResolverOptions options, JwkSet current, JwkSet next, Uri agentUrl)
    {
        #region Rotation
        // During a rotation, trust the old and the new key. Keys are matched by kid.
        var both = new JwkSet { Keys = [.. current.Keys, .. next.Keys] };
        options.Trust.TrustedKeys[CardTrustPolicy.OriginOf(agentUrl)] = both;
        #endregion
    }
}
