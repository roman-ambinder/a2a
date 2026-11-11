using A2A.Client.Credentials;
using A2A.Text;

namespace A2A.Client.Identity.Aws;

/// <summary>
///     Signs requests with AWS Signature Version 4 when the card asks for the scheme named
///     <see cref="AwsSigV4Options.SchemeName" />. For an agent whose card declares no scheme, use
///     <see cref="AwsSigV4Handler" /> instead. Use one provider per agent: it binds to one card origin.
/// </summary>
public sealed class AwsSigV4CredentialProvider : IA2ACredentialProvider
{
    private readonly string _schemeName;
    private readonly AwsSigV4RequestSigner _signer;
    private Uri? _boundOrigin;

    /// <summary>Creates the provider.</summary>
    /// <exception cref="ArgumentException">
    ///     An option is missing or invalid: <see cref="AwsSigV4Options.Region" />,
    ///     <see cref="AwsSigV4Options.Service" /> and <see cref="AwsSigV4Options.SchemeName" /> are required.
    /// </exception>
    public AwsSigV4CredentialProvider(AwsSigV4Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.SchemeName))
            throw new ArgumentException(
                "SchemeName is required: no A2A scheme type means SigV4, so name the card scheme to handle.",
                nameof(options));
        _signer = new AwsSigV4RequestSigner(options, false);
        _schemeName = options.SchemeName;
        _boundOrigin = options.AgentOrigin;
    }

    /// <inheritdoc />
    public bool CanHandle(A2ACredentialContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return string.Equals(context.SchemeName, _schemeName, StringComparison.Ordinal) &&
               TryBindOrigin(context.CardOrigin);
    }

    /// <inheritdoc />
    public async ValueTask ApplyAsync(HttpRequestMessage request, A2ACredentialContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!TryBindOrigin(context.CardOrigin))
            throw new A2AException(A2AErrorCode.Unauthenticated,
                $"This provider is bound to another agent, so it will not sign requests to {TextSanitizer.ForMessage(context.CardOrigin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped))}. Use one provider per agent.");
        await _signer.SignAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private bool TryBindOrigin(Uri origin)
    {
        var bound = Interlocked.CompareExchange(ref _boundOrigin, origin, null) ?? origin;
        return A2ACredentialBinding.IsSameOrigin(bound, origin);
    }
}
