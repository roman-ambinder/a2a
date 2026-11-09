using System.Collections.Concurrent;
using System.Net.Http.Headers;
using A2A.Client.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Client;

/// <summary>Named client registration for DI.</summary>
public sealed class A2ANamedClientOptions
{
    /// <summary>Agent base URL or full Agent Card URL.</summary>
    public Uri? AgentUrl { get; set; }

    /// <summary>Options of the A2A client, such as bindings and protocol version.</summary>
    public A2AClientOptions Client { get; } = new();

    /// <summary>Options of the card resolver, such as trust policy and pinning.</summary>
    public A2ACardResolverOptions Resolver { get; } = new();

    /// <summary>Default of <see cref="MaxResponseContentBufferSize"/>: 16 MiB.</summary>
    public const long DefaultMaxResponseContentBufferSize = 16 * 1024 * 1024;

    /// <summary>
    /// Largest response body, in bytes, the client's <see cref="HttpClient"/> buffers
    /// (<see cref="HttpClient.MaxResponseContentBufferSize"/>): Agent Cards, unary responses and error bodies. A larger body
    /// fails the call with an <see cref="HttpRequestException"/> instead of filling memory. Streams are read as they arrive and
    /// are not limited by it. Default <see cref="DefaultMaxResponseContentBufferSize"/> (16 MiB); from 1 to
    /// <see cref="int.MaxValue"/>.
    /// </summary>
    public long MaxResponseContentBufferSize { get; set; } = DefaultMaxResponseContentBufferSize;

    internal List<Func<IServiceProvider, IA2ACredentialProvider>> CredentialProviders { get; } = [];
}

/// <summary>Creates and caches A2A clients registered with <see cref="A2AClientServiceCollectionExtensions.AddA2AClient"/>.</summary>
public interface IA2AClientFactory
{
    /// <summary>Resolves the agent's card on first use and returns a cached client.</summary>
    ValueTask<A2AClient> GetClientAsync(string name, CancellationToken cancellationToken = default);
}

internal sealed class DefaultA2AClientFactory(IHttpClientFactory httpClientFactory, IOptionsMonitor<A2ANamedClientOptions> options,
    IServiceProvider services, IHttpMessageHandlerFactory handlerFactory, ILoggerFactory? loggerFactory = null) : IA2AClientFactory
{
    private readonly ConcurrentDictionary<string, Lazy<Task<A2AClient>>> _clients = new();

    public async ValueTask<A2AClient> GetClientAsync(string name, CancellationToken cancellationToken = default)
    {
        var lazy = _clients.GetOrAdd(name, n => new Lazy<Task<A2AClient>>(() => CreateAsync(n)));
        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (lazy.Value.IsFaulted)
        {
            _clients.TryRemove(new KeyValuePair<string, Lazy<Task<A2AClient>>>(name, lazy));
            throw;
        }
    }

    private async Task<A2AClient> CreateAsync(string name)
    {
        var o = options.Get(name);
        if (o.AgentUrl is null) throw new InvalidOperationException($"A2A client '{name}' has no AgentUrl.");
        var http = httpClientFactory.CreateClient(HttpClientName(name));
        var resolver = new A2ACardResolver(http, o.Resolver, loggerFactory?.CreateLogger<A2ACardResolver>());
        if (o.CredentialProviders.Count == 0)
        {
            var card = await resolver.GetAgentCardAsync(o.AgentUrl).ConfigureAwait(false);
            await A2AClientFactory.EnsureEndpointsAllowedAsync(card, o.AgentUrl, o.Client).ConfigureAwait(false);
            return A2AClientFactory.Create(card, http, o.Client);
        }

        // Check the chain before any request: a redirecting chain must not even fetch the card when credentials are configured.
        var chain = handlerFactory.CreateHandler(HttpClientName(name));
        A2ACredentialHandler.EnsureNoAutoRedirect(chain);

        // The card fetch carries no credentials (R3). Credentials go on a second client built over the same handler chain.
        var resolved = await resolver.ResolveAsync(o.AgentUrl).ConfigureAwait(false);
        await A2AClientFactory.EnsureEndpointsAllowedAsync(resolved.Card, resolved.Source, o.Client).ConfigureAwait(false);
        var selected = A2AClientFactory.SelectInterface(resolved.Card, o.Client);
        if (selected is null) return A2AClientFactory.Create(resolved.Card, http, o.Client); // throws the usual error

        var providers = o.CredentialProviders.ConvertAll(f => f(services));
        var binding = A2ACredentialBinding.Create(resolved, selected, providers, o.Resolver.RequireHttps);
        // disposeHandler: false. The handler comes from IHttpMessageHandlerFactory, which owns its lifetime. The client is cached
        // per name, so it is built once and not disposed.
        var authed = new HttpClient(new A2ACredentialHandler(binding, chain), disposeHandler: false)
        {
            Timeout = http.Timeout,
            MaxResponseContentBufferSize = http.MaxResponseContentBufferSize,
            BaseAddress = http.BaseAddress,
            DefaultRequestVersion = http.DefaultRequestVersion,
            DefaultVersionPolicy = http.DefaultVersionPolicy,
        };
        foreach (var header in http.DefaultRequestHeaders) authed.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        return A2AClientFactory.Create(resolved.Card, authed, o.Client);
    }

    internal const string HttpClientPrefix = "A2A:";

    internal static string HttpClientName(string name) => HttpClientPrefix + name;
}

/// <summary>Dependency injection extensions for A2A clients.</summary>
public static class A2AClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named A2A client with safe defaults (no cookies, no redirects, infinite HttpClient timeout, response bodies
    /// buffered up to <see cref="A2ANamedClientOptions.MaxResponseContentBufferSize"/>). Returns the
    /// <see cref="IHttpClientBuilder"/> so you can add auth handlers, resilience, or a primary handler.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the client is created: <see cref="A2ANamedClientOptions.MaxResponseContentBufferSize"/> is below 1 or above
    /// <see cref="int.MaxValue"/>.
    /// </exception>
    public static IHttpClientBuilder AddA2AClient(this IServiceCollection services, string name, Action<A2ANamedClientOptions> configure)
    {
        services.Configure(name, configure);
        services.AddHttpClient();
        services.AddSingleton<IA2AClientFactory, DefaultA2AClientFactory>();
        return services.AddHttpClient(DefaultA2AClientFactory.HttpClientName(name))
            // No cookies and no redirects: a redirect would carry credentials to another origin. Replace it with
            // ConfigurePrimaryHttpMessageHandler on the returned builder if you need something else.
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false })
            // The credential handler sits above the factory's logging handlers, and the card chooses the header name, so redact
            // every header value: an API key or token must never reach a log line.
            .RedactLoggedHeaders(_ => true)
            // The limit is per call (A2ARequestOptions.Timeout, default 100 s for unary calls, none for streams).
            .ConfigureHttpClient((sp, c) =>
            {
                c.Timeout = Timeout.InfiniteTimeSpan;
                var max = sp.GetRequiredService<IOptionsMonitor<A2ANamedClientOptions>>().Get(name).MaxResponseContentBufferSize;
                ArgumentOutOfRangeException.ThrowIfLessThan(max, 1, nameof(A2ANamedClientOptions.MaxResponseContentBufferSize));
                ArgumentOutOfRangeException.ThrowIfGreaterThan(max, int.MaxValue, nameof(A2ANamedClientOptions.MaxResponseContentBufferSize));
                c.MaxResponseContentBufferSize = max;
            });
    }

    /// <summary>Adds a bearer token to every request, fetched from <paramref name="tokenProvider"/> (cache inside it).</summary>
    /// <remarks>
    /// On a builder returned by <see cref="AddA2AClient"/>, the handler refuses to send the token to any origin other than the
    /// <see cref="A2ANamedClientOptions.AgentUrl"/> origin (it throws <see cref="A2AException"/> before it calls the token provider).
    /// On another builder it adds the token to every request.
    /// </remarks>
    public static IHttpClientBuilder AddBearerToken(this IHttpClientBuilder builder, Func<IServiceProvider, CancellationToken, ValueTask<string>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        var clientName = TryGetA2AClientName(builder);
        return builder.AddHttpMessageHandler(sp =>
        {
            Uri? origin = null;
            if (clientName is not null)
                origin = sp.GetRequiredService<IOptionsMonitor<A2ANamedClientOptions>>().Get(clientName).AgentUrl;
            return new BearerTokenHandler(ct => tokenProvider(sp, ct), origin);
        });
    }

    internal static string? TryGetA2AClientName(IHttpClientBuilder builder) =>
        builder.Name is { } n && n.StartsWith(DefaultA2AClientFactory.HttpClientPrefix, StringComparison.Ordinal)
            ? n[DefaultA2AClientFactory.HttpClientPrefix.Length..]
            : null;
}

/// <summary>Sets <c>Authorization: Bearer</c> on each request.</summary>
public sealed class BearerTokenHandler : DelegatingHandler
{
    private readonly Func<CancellationToken, ValueTask<string>> _tokenProvider;
    private readonly Uri? _allowedOrigin;

    /// <summary>
    /// Creates a handler that adds the token to every request, whatever the request's origin. It is unguarded: to restrict the
    /// token to the agent's origin, use <see cref="A2AClientServiceCollectionExtensions.AddBearerToken"/> on an
    /// <see cref="A2AClientServiceCollectionExtensions.AddA2AClient"/> builder.
    /// </summary>
    public BearerTokenHandler(Func<CancellationToken, ValueTask<string>> tokenProvider) : this(tokenProvider, null) { }

    internal BearerTokenHandler(Func<CancellationToken, ValueTask<string>> tokenProvider, Uri? allowedOrigin)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        _tokenProvider = tokenProvider;
        _allowedOrigin = allowedOrigin;
    }

    private static string Origin(Uri uri) => uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped);

    /// <summary>Sets the bearer token on the request and sends it.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (_allowedOrigin is not null)
        {
            var uri = request.RequestUri;
            if (uri is null || !A2ACredentialBinding.IsSameOrigin(uri, _allowedOrigin))
                throw new A2AException(A2AErrorCode.InvalidAgentResponse,
                    $"Refusing to send the bearer token to {(uri is { IsAbsoluteUri: true } ? Origin(uri) : "(no absolute URI)")}: not the agent origin {Origin(_allowedOrigin)}.");
        }
        var token = await _tokenProvider(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
