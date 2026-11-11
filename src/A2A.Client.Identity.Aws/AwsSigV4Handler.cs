using A2A.Client.Credentials;

namespace A2A.Client.Identity.Aws;

/// <summary>
///     Signs every request to <see cref="AwsSigV4Options.AgentOrigin" /> with AWS Signature Version 4, the card fetch
///     included. Use it for an agent behind AWS IAM (API Gateway with IAM authorization, a Lambda function URL with
///     <c>AWS_IAM</c> auth), whose card usually declares no security scheme. Requests to other origins pass unsigned.
///     Signing adds headers only; the request URI is not changed.
/// </summary>
public sealed class AwsSigV4Handler : DelegatingHandler
{
    private readonly AwsSigV4RequestSigner _signer;

    /// <summary>
    ///     Creates the handler with no inner handler, for <c>IHttpClientBuilder.AddHttpMessageHandler</c>. The primary
    ///     handler of the client must not follow redirects; the one <c>AddA2AClient</c> sets does not.
    /// </summary>
    /// <exception cref="ArgumentException">
    ///     An option is missing or invalid: <see cref="AwsSigV4Options.Region" />,
    ///     <see cref="AwsSigV4Options.Service" /> and <see cref="AwsSigV4Options.AgentOrigin" /> are required.
    /// </exception>
    public AwsSigV4Handler(AwsSigV4Options options)
    {
        _signer = new AwsSigV4RequestSigner(options, true);
    }

    /// <summary>Creates the handler over <paramref name="innerHandler" />, which must not follow redirects.</summary>
    /// <exception cref="ArgumentException">An option is missing or invalid, or <paramref name="innerHandler" /> follows redirects.</exception>
    public AwsSigV4Handler(AwsSigV4Options options, HttpMessageHandler innerHandler) : base(innerHandler)
    {
        _signer = new AwsSigV4RequestSigner(options, true);
        A2ACredentialHandler.EnsureNoAutoRedirect(innerHandler);
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is { } uri && AwsSigV4RequestSigner.IsAgentOrigin(_signer.AgentOrigin, uri))
            await _signer.SignAsync(request, cancellationToken).ConfigureAwait(false);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
