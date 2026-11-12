using System.Text.Json;
using A2A.Client;
using A2A.Client.Cards;
using A2A.Client.Credentials;
using A2A.Json;
using A2A.Signing;

namespace A2A.Cli;

/// <summary>
///     The resolved Agent Card and, for most commands, a client that sends the credentials the card asks for.
///     The card fetch carries no credentials. Credentials go only to the card's origin and never over plain HTTP to a
///     non-loopback host.
/// </summary>
internal sealed class AgentConnection : IDisposable
{
    private const long MaxResponseBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(100);

    private readonly List<IDisposable> _owned = [];

    private AgentConnection(ResolvedAgentCard card)
    {
        Card = card;
    }

    public ResolvedAgentCard Card { get; }

    /// <summary>Null when only the card was asked for.</summary>
    public A2AClient? Client { get; private set; }

    public A2ACredentialBinding? Binding { get; private set; }

    /// <summary>The handler stack that adds credentials. Use it for artifact downloads.</summary>
    public HttpMessageHandler? AgentHandler { get; private set; }

    public void Dispose()
    {
        foreach (var d in _owned) d.Dispose();
        _owned.Clear();
    }

    public static async Task<AgentConnection> OpenAsync(ConnectionSettings s, CliEnvironment env, Output output,
        bool cardOnly,
        CancellationToken cancellationToken)
    {
        var owned = new List<IDisposable>();
        try
        {
            var primary = env.Handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false };
            if (env.Handler is null) owned.Add(primary);

            var resolverOptions = await BuildResolverOptionsAsync(s, cancellationToken).ConfigureAwait(false);
            ResolvedAgentCard resolved;
            using (var cardHttp = new HttpClient(primary, false)
                   {
                       Timeout = Timeout.InfiniteTimeSpan,
                       MaxResponseContentBufferSize = MaxResponseBytes
                   })
            {
                resolved = await ResolveCardAsync(new A2ACardResolver(cardHttp, resolverOptions), s.Agent,
                    cancellationToken).ConfigureAwait(false);
            }

            var connection = new AgentConnection(resolved);
            connection._owned.AddRange(owned);
            owned.Clear();
            try
            {
                if (!cardOnly)
                    await connection.BuildClientAsync(s, env, output, primary, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                connection.Dispose();
                throw;
            }

            return connection;
        }
        finally
        {
            foreach (var d in owned) d.Dispose();
        }
    }

    /// <summary>
    ///     Fetches the card. An HTTP error status on the card means no agent answers at that URL: a connection error (exit 3),
    ///     not "task not found" (exit 5). It is not an A2A error, so no A2A error code is reported. 401 and 403 stay auth errors.
    /// </summary>
    private static async Task<ResolvedAgentCard> ResolveCardAsync(A2ACardResolver resolver, Uri agent,
        CancellationToken cancellationToken)
    {
        try
        {
            return await resolver.ResolveAsync(agent, cancellationToken).ConfigureAwait(false);
        }
        catch (A2AException ex) when (ex is not AgentCardVerificationException and not AgentCardPinException
                                      && ex.HttpStatus is not (null or 401 or 403))
        {
            throw new CliException(ExitCodes.Network, ex.Message);
        }
    }

    private static async Task<A2ACardResolverOptions> BuildResolverOptionsAsync(ConnectionSettings s,
        CancellationToken cancellationToken)
    {
        var trust = new CardTrustPolicy();
        var origin = CardTrustPolicy.OriginOf(s.Agent);
        if (s.TrustJwksPath is not null)
        {
            JwkSet keys;
            try
            {
                var text = await File.ReadAllTextAsync(s.TrustJwksPath, cancellationToken).ConfigureAwait(false);
                keys = A2AJson.Deserialize<JwkSet>(text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                throw CliException.Usage("The --trust-jwks file cannot be read as a JSON Web Key Set.");
            }

            trust.TrustedKeys[origin] = keys;
        }

        foreach (var host in s.JkuHosts) trust.AllowJkuHost(origin, host);
        if (s.RequireSignature) trust.RequireSignature = true;

        return new A2ACardResolverOptions
        {
            Trust = trust,
            PinPolicy = PinPolicy.Off,
            RequireHttps = !s.AllowHttp,
            Timeout = s.Timeout ?? DefaultTimeout
        };
    }

    private async Task BuildClientAsync(ConnectionSettings s, CliEnvironment env, Output output,
        HttpMessageHandler primary, CancellationToken cancellationToken)
    {
        var options = new A2AClientOptions { Tenant = s.Tenant };
        if (s.Retries is { } retries) options.MaxRetries = retries;
        if (s.Transport is not null)
            options.SupportedBindings = new List<string>
                { s.Transport == "rest" ? ProtocolBindings.HttpJson : ProtocolBindings.JsonRpc };

        // The check A2AClient.ConnectAsync makes: the card chooses the URL the tool calls, so a card fetched from a public
        // address must not point the tool at a private one (SSRF). The default policy (MatchCardNetwork) needs the card's
        // source; A2AClientFactory.Create alone treats the card as caller-supplied and allows every endpoint.
        await A2AClientFactory.EnsureEndpointsAllowedAsync(Card.Card, Card.Source, options, cancellationToken)
            .ConfigureAwait(false);

        var selected = A2AClientFactory.SelectInterface(Card.Card, options)
                       ?? throw new A2AException(A2AErrorCode.UnsupportedOperation,
                           $"The agent has no interface this tool can use. It supports: {string.Join(", ", options.SupportedBindings)}.");

        var providers = new List<IA2ACredentialProvider>();
        if (s.ClientId is not null && s.ClientSecret is not null)
        {
            var oauth = new OAuth2ClientCredentialsProvider(new OAuth2ClientCredentialsOptions
            {
                ClientId = s.ClientId,
                ClientSecret = s.ClientSecret,
                TokenEndpoint = s.TokenEndpoint,
                Scopes = s.Scopes.ToList(),
                TokenHandler = env.OAuthTokenHandler
            });
            _owned.Add(oauth);
            providers.Add(oauth);
        }

        if (s.Token is not null) providers.Add(new StaticBearerCredentialProvider(s.Token));
        if (s.ApiKey is not null) providers.Add(new ApiKeyCredentialProvider(s.ApiKey));

        // Always create the binding: a card that demands credentials fails here, before any call, with Unauthenticated.
        // RequireHttps stays true, so --allow-http never lets a credential cross plain HTTP to a non-loopback host.
        var binding = A2ACredentialBinding.Create(Card, selected, providers);
        if (s.HasCredential && binding.Schemes.Count == 0)
            output.ErrorLine("The agent card asks for no credentials, so none was sent.");

        var handler = new A2ACredentialHandler(binding, primary);
        var http = new HttpClient(handler, false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        _owned.Add(http);
        Binding = binding;
        AgentHandler = handler;
        Client = A2AClientFactory.Create(Card.Card, http, options);
    }
}