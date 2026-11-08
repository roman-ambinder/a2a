namespace A2A.Client.Credentials;

/// <summary>
/// Applies the credentials of an <see cref="A2ACredentialBinding"/> to every request, and only to requests on the card's origin.
/// </summary>
public sealed class A2ACredentialHandler : DelegatingHandler
{
    private readonly A2ACredentialBinding _binding;

    /// <summary>Creates the handler. Throws <see cref="ArgumentException"/> when <paramref name="innerHandler"/> follows redirects.</summary>
    public A2ACredentialHandler(A2ACredentialBinding binding, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        ArgumentNullException.ThrowIfNull(binding);
        EnsureNoAutoRedirect(innerHandler);
        _binding = binding;
    }

    /// <summary>
    /// Throws <see cref="ArgumentException"/> when <paramref name="handler"/>, or a handler nested under it, is a
    /// <see cref="SocketsHttpHandler"/> or <see cref="HttpClientHandler"/> that follows redirects. A handler of any other type
    /// is accepted: the SDK cannot see whether it follows redirects.
    /// </summary>
    public static void EnsureNoAutoRedirect(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        for (var h = handler; h is not null;)
        {
            switch (h)
            {
                case SocketsHttpHandler { AllowAutoRedirect: true }:
                case HttpClientHandler { AllowAutoRedirect: true }:
                    throw new ArgumentException(
                        "The handler follows redirects, which could send credentials to another origin. Set AllowAutoRedirect = false.",
                        nameof(handler));
            }
            h = h is DelegatingHandler { InnerHandler: { } inner } ? inner : null;
        }
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_binding.Pairs.Count > 0)
        {
            var uri = request.RequestUri;
            if (uri is null || !uri.IsAbsoluteUri
                || Uri.Compare(uri, _binding.CardOrigin, UriComponents.Scheme | UriComponents.Host | UriComponents.StrongPort,
                    UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
                throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                    $"Refusing to send credentials to {uri?.GetLeftPart(UriPartial.Authority) ?? "(no URI)"}: not the card origin {_binding.CardOrigin.GetLeftPart(UriPartial.Authority)}.");

            foreach (var (context, provider) in _binding.Pairs)
            {
                await provider.ApplyAsync(request, context, cancellationToken).ConfigureAwait(false);
                if (request.RequestUri != uri)
                    throw new InvalidOperationException("A credential provider changed the request URI. Providers must not do that.");
            }
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
