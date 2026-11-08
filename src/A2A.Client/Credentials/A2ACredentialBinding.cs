namespace A2A.Client.Credentials;

/// <summary>
/// The credentials selected for one card and interface. Built once at client creation; applied by
/// <see cref="A2ACredentialHandler"/> on every request.
/// </summary>
public sealed class A2ACredentialBinding
{
    private A2ACredentialBinding(Uri cardOrigin, List<(A2ACredentialContext Context, IA2ACredentialProvider Provider)> pairs)
    {
        CardOrigin = cardOrigin;
        Pairs = pairs;
        Schemes = pairs.ConvertAll(p => p.Context);
    }

    /// <summary>The origin of the card: scheme, host and port, no path.</summary>
    public Uri CardOrigin { get; }

    /// <summary>The schemes that will be applied. Empty when the client is anonymous.</summary>
    public IReadOnlyList<A2ACredentialContext> Schemes { get; }

    internal List<(A2ACredentialContext Context, IA2ACredentialProvider Provider)> Pairs { get; }

    /// <summary>
    /// Selects the first requirement of the card that the providers can satisfy, and checks that the interface is safe to
    /// send credentials to.
    /// </summary>
    /// <exception cref="A2AException">No requirement can be satisfied, the interface is on another origin, or the card origin is not HTTPS.</exception>
    /// <exception cref="NotSupportedException">The interface uses gRPC.</exception>
    public static A2ACredentialBinding Create(ResolvedAgentCard card, AgentInterface selectedInterface,
        IEnumerable<IA2ACredentialProvider> providers, bool requireHttps = true)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(selectedInterface);
        ArgumentNullException.ThrowIfNull(providers);
        var list = providers.ToList();
        var origin = new Uri(card.Source.GetLeftPart(UriPartial.Authority), UriKind.Absolute);

        var requirements = card.Card.SecurityRequirements ?? [];
        var schemes = card.Card.SecuritySchemes;
        List<(A2ACredentialContext, IA2ACredentialProvider)>? chosen = null;
        var anonymousAllowed = requirements.Count == 0;

        foreach (var requirement in requirements)
        {
            if (requirement.Schemes.Count == 0) { anonymousAllowed = true; continue; }
            var pairs = new List<(A2ACredentialContext, IA2ACredentialProvider)>();
            var ok = true;
            foreach (var (name, scopes) in requirement.Schemes)
            {
                if (schemes is null || !schemes.TryGetValue(name, out var scheme)) { ok = false; break; }
                var context = new A2ACredentialContext(name, scheme, (scopes?.List ?? []).AsReadOnly(), origin);
                var provider = list.FirstOrDefault(p => p.CanHandle(context));
                if (provider is null) { ok = false; break; }
                pairs.Add((context, provider));
            }
            if (ok) { chosen = pairs; break; }
        }

        if (chosen is null)
        {
            if (!anonymousAllowed)
            {
                var declared = schemes is { Count: > 0 } ? string.Join(", ", schemes.Keys) : "none";
                throw new A2AException(A2AErrorCode.Unauthenticated,
                    $"No credential provider satisfies the Agent Card's security requirements. Declared schemes: {declared}. " +
                    "API keys in query strings are refused and OAuth2 token URLs on another origin need TokenEndpoint.");
            }
            return new A2ACredentialBinding(origin, []);
        }

        if (!Uri.TryCreate(selectedInterface.Url, UriKind.Absolute, out var itfUrl)
            || Uri.Compare(itfUrl, origin, UriComponents.Scheme | UriComponents.Host | UriComponents.StrongPort,
                UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
        {
            var itfOrigin = itfUrl is null ? "(invalid URL)" : itfUrl.GetLeftPart(UriPartial.Authority);
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"The selected interface is on origin {itfOrigin}, not the card origin {origin.GetLeftPart(UriPartial.Authority)}. Credentials are only sent to the card's origin.");
        }

        if (requireHttps && origin.Scheme != Uri.UriSchemeHttps && !origin.IsLoopback)
            throw new A2AException(A2AErrorCode.InvalidRequest,
                $"Credentials are only sent over HTTPS. Card origin: {origin.GetLeftPart(UriPartial.Authority)}.");

        if (selectedInterface.ProtocolBinding == ProtocolBindings.Grpc)
            throw new NotSupportedException(
                "Credentials cannot be applied to a gRPC interface through the HttpClient. Build the gRPC channel with " +
                "GrpcChannelOptions.HttpHandler set to an A2ACredentialHandler over a non-redirecting handler.");

        return new A2ACredentialBinding(origin, chosen);
    }
}
