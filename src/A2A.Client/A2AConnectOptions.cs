using A2A.Client.Credentials;

namespace A2A.Client;

/// <summary>
///     Everything <see cref="A2AClient.ConnectAsync(Uri, Action{A2AConnectOptions}?, CancellationToken)" /> needs to reach
///     an agent: credentials, card trust, client behaviour and, rarely, your own HTTP handler.
/// </summary>
public sealed class A2AConnectOptions
{
    /// <summary>
    ///     Credential providers, in order of preference: the client sends the credentials the Agent Card asks for, only to
    ///     the card's origin, and never on a redirect or on the card fetch. Empty (the default): no credentials.
    /// </summary>
    public IList<IA2ACredentialProvider> Credentials { get; } = new List<IA2ACredentialProvider>();

    /// <summary>
    ///     Client behaviour: retries, timeouts, extensions, the tenant, interface fallback, and transport factories (gRPC
    ///     with <c>UseGrpc()</c>).
    /// </summary>
    public A2AClientOptions Client { get; set; } = new();

    /// <summary>How the Agent Card is fetched and trusted: HTTPS, signatures, pinned keys and the pin store.</summary>
    public A2ACardResolverOptions Card { get; set; } = new();

    /// <summary>
    ///     The primary HTTP handler, for a proxy or a client certificate. Null (the default): a shared handler that keeps
    ///     no cookies, follows no redirects and recycles connections so DNS changes are seen. A handler of your own must
    ///     not follow redirects. It is not disposed.
    /// </summary>
    public HttpMessageHandler? Handler { get; set; }
}
