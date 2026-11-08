using A2A.Client;
using A2A.Client.Peers;
using Microsoft.Extensions.Logging;

namespace A2A.DocSnippets.PeerIdentityGuide;

internal static class Snippets
{
    public static async Task ReadIdentityAsync(A2ACardResolver resolver)
    {
        #region Read
        var resolved = await resolver.ResolveAsync(new Uri("https://agent.example.com/"));
        var identity = resolved.Identity;

        Console.WriteLine(identity.Origin);     // https://agent.example.com
        Console.WriteLine(identity.KeyId);      // the kid of the trusted key that verified the card, or null
        Console.WriteLine(identity.Assurance);  // OriginOnly (no key) or Verified
        Console.WriteLine(identity);            // https://agent.example.com#echo-1, or "... (origin only)"
        #endregion
    }

    #region Resolver
    // Adds a DID after your own check. It may add an ExternalId and nothing else.
    internal sealed class DidResolver : IPeerIdentityResolver
    {
        public async ValueTask<PeerIdentity> ResolveAsync(PeerIdentityContext context, CancellationToken cancellationToken = default)
        {
            var did = await FindVerifiedDidAsync(context.Source, cancellationToken);
            return did is null ? context.Baseline : context.Baseline with { ExternalId = did };
        }

        // Your proof check goes here. Return null when the peer has no DID or the check fails.
        private static ValueTask<string?> FindVerifiedDidAsync(Uri cardUrl, CancellationToken cancellationToken) =>
            ValueTask.FromResult<string?>(null);
    }
    #endregion

    public static A2ACardResolver CreateResolver(HttpClient http)
    {
        #region UseResolver
        return new A2ACardResolver(http, new A2ACardResolverOptions
        {
            IdentityResolver = new DidResolver(),
        });
        #endregion
    }

    public static void Register(AgentRegistry registry, ResolvedAgentCard resolved, ILogger logger)
    {
        #region Rejections
        try
        {
            var agent = registry.Register(resolved);
            if (agent.PreviousIdentity is not null)
                logger.LogInformation("Key changed: {Old} -> {New}", agent.PreviousIdentity, agent.Identity);
            if (agent.LookalikeOf.Count > 0 || agent.MixedScriptName)
                registry.Remove(agent.Identity); // fail closed
        }
        catch (AgentRegistrationException ex)
        {
            // The registry is unchanged. ConflictingIdentity is the registered peer behind the refusal, if any.
            var advice = ex.Reason switch
            {
                AgentRegistrationRejection.DuplicateName => "Another peer already has this name.",
                AgentRegistrationRejection.IdentityInUse => "This identity is registered at another card URL.",
                AgentRegistrationRejection.IdentityDowngrade => "A signed agent is now served unsigned.",
                AgentRegistrationRejection.InvalidName => "The card name is empty.",
                _ => "The registry is full.",
            };
            logger.LogWarning("{Advice} {Identity} against {Conflicting}", advice, ex.Identity, ex.ConflictingIdentity);
        }
        #endregion
    }

    public static bool CallByName(AgentRegistry registry, PeerIdentity expected)
    {
        #region ByName
        // A name is a lookup key only. Check the identity before you send credentials.
        return registry.TryGetByName("echo", out var agent) && agent.Identity == expected;
        #endregion
    }
}
