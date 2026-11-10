using A2A.Text;

namespace A2A.Client.Credentials;

/// <summary>
///     The credentials selected for one card and interface. Built once at client creation; applied by
///     <see cref="A2ACredentialHandler" /> on every request.
/// </summary>
public sealed class A2ACredentialBinding
{
    private A2ACredentialBinding(Uri cardOrigin,
        List<(A2ACredentialContext Context, IA2ACredentialProvider Provider)> pairs)
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
    ///     Selects the first requirement of the card that the providers can satisfy, and checks that the interface is safe to
    ///     send credentials to.
    /// </summary>
    /// <exception cref="A2AException">
    ///     No requirement can be satisfied, the interface is on another origin, or the card origin
    ///     is not HTTPS.
    /// </exception>
    /// <param name="card">The resolved card.</param>
    /// <param name="selectedInterface">The interface the client will use.</param>
    /// <param name="providers">The providers, in order of preference.</param>
    /// <param name="requireHttps">When true (the default), the card origin must be HTTPS or loopback.</param>
    /// <param name="forGrpcChannel">
    ///     Set to true only when the binding will be used in the handler of a gRPC channel
    ///     (<c>GrpcChannelOptions.HttpHandler = new A2ACredentialHandler(binding, handler)</c>). Otherwise a gRPC interface is
    ///     refused,
    ///     because a plain <see cref="HttpClient" /> cannot carry it. The origin and HTTPS checks apply either way.
    /// </param>
    /// <exception cref="NotSupportedException">The interface uses gRPC and <paramref name="forGrpcChannel" /> is false.</exception>
    public static A2ACredentialBinding Create(ResolvedAgentCard card, AgentInterface selectedInterface,
        IEnumerable<IA2ACredentialProvider> providers, bool requireHttps = true, bool forGrpcChannel = false)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(selectedInterface);
        ArgumentNullException.ThrowIfNull(providers);
        var list = providers.ToList();
        var origin = new Uri(OriginText(card.Source), UriKind.Absolute);

        var requirements = card.Card.SecurityRequirements ?? [];
        var schemes = card.Card.SecuritySchemes;
        List<(A2ACredentialContext, IA2ACredentialProvider)>? chosen = null;
        var anonymousAllowed = requirements.Count == 0;

        foreach (var requirement in requirements)
        {
            // A null entry in the card is unsatisfiable, not anonymous.
            if (requirement?.Schemes is not { } requirementSchemes) continue;
            if (requirementSchemes.Count == 0)
            {
                anonymousAllowed = true;
                continue;
            }

            var pairs = new List<(A2ACredentialContext, IA2ACredentialProvider)>();
            var ok = true;
            foreach (var (name, scopes) in requirementSchemes)
            {
                if (schemes is null || !schemes.TryGetValue(name, out var scheme) || scheme is null)
                {
                    ok = false;
                    break;
                }

                var context = new A2ACredentialContext(name, scheme, (scopes?.List ?? []).AsReadOnly(), origin);
                var provider = list.FirstOrDefault(p => p.CanHandle(context));
                if (provider is null)
                {
                    ok = false;
                    break;
                }

                pairs.Add((context, provider));
            }

            if (ok)
            {
                chosen = pairs;
                break;
            }
        }

        if (chosen is null)
        {
            if (!anonymousAllowed)
            {
                var declared = schemes is { Count: > 0 }
                    ? string.Join(", ", schemes.Keys.Select(TextSanitizer.ForMessage))
                    : "none";
                throw new A2AException(A2AErrorCode.Unauthenticated,
                    $"No credential provider satisfies the Agent Card's security requirements. Declared schemes: {declared}. " +
                    "API keys in query strings are refused and OAuth2 token URLs on another origin need TokenEndpoint.");
            }

            return new A2ACredentialBinding(origin, []);
        }

        if (!Uri.TryCreate(selectedInterface.Url, UriKind.Absolute, out var itfUrl)
            || !IsSameOrigin(itfUrl, origin))
        {
            var itfOrigin = itfUrl is null ? "(invalid URL)" : OriginText(itfUrl);
            throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                $"The selected interface is on origin {itfOrigin}, not the card origin {OriginText(origin)}. Credentials are only sent to the card's origin.");
        }

        if (requireHttps && origin.Scheme != Uri.UriSchemeHttps && !origin.IsLoopback)
            throw new A2AException(A2AErrorCode.InvalidRequest,
                $"Credentials are only sent over HTTPS. Card origin: {OriginText(origin)}.");

        if (selectedInterface.ProtocolBinding == ProtocolBindings.Grpc && !forGrpcChannel)
            throw new NotSupportedException(
                "Credentials cannot be applied to a gRPC interface through the HttpClient. Call A2ACredentialBinding.Create(..., " +
                "forGrpcChannel: true), then build the gRPC channel with GrpcChannelOptions.HttpHandler set to an " +
                "A2ACredentialHandler over a non-redirecting handler.");

        return new A2ACredentialBinding(origin, chosen);
    }

    /// <summary>
    ///     True when both URIs have the same scheme, port and host, as the connection sees them. Hosts are compared by
    ///     <see cref="Uri.IdnHost" /> (the name that is dialed), not by <see cref="Uri.Host" />, because case folding of
    ///     Unicode host
    ///     names can equate two names that resolve differently. Relative URIs are never the same origin.
    /// </summary>
    public static bool IsSameOrigin(Uri a, Uri b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.IsAbsoluteUri && b.IsAbsoluteUri
                               && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
                               && a.Port == b.Port
                               && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Scheme, host and port of an absolute URI, without user info, path, query or fragment.</summary>
    internal static string OriginText(Uri uri)
    {
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);
    }
}