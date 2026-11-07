using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace A2A.Client;

/// <summary>Named client registration for DI.</summary>
public sealed class A2ANamedClientOptions
{
    /// <summary>Agent base URL or full Agent Card URL.</summary>
    public Uri? AgentUrl { get; set; }

    public A2AClientOptions Client { get; } = new();

    public A2ACardResolverOptions Resolver { get; } = new();
}

/// <summary>Creates and caches A2A clients registered with <see cref="A2AClientServiceCollectionExtensions.AddA2AClient"/>.</summary>
public interface IA2AClientFactory
{
    /// <summary>Resolves the agent's card on first use and returns a cached client.</summary>
    ValueTask<A2AClient> GetClientAsync(string name, CancellationToken cancellationToken = default);
}

internal sealed class DefaultA2AClientFactory(IHttpClientFactory httpClientFactory, IOptionsMonitor<A2ANamedClientOptions> options) : IA2AClientFactory
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

    private Task<A2AClient> CreateAsync(string name)
    {
        var o = options.Get(name);
        if (o.AgentUrl is null) throw new InvalidOperationException($"A2A client '{name}' has no AgentUrl.");
        var http = httpClientFactory.CreateClient(HttpClientName(name));
        return A2AClientFactory.CreateAsync(o.AgentUrl, http, o.Client, o.Resolver);
    }

    internal static string HttpClientName(string name) => "A2A:" + name;
}

public static class A2AClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named A2A client. Returns the <see cref="IHttpClientBuilder"/> so you can add auth handlers,
    /// resilience, or a primary handler.
    /// </summary>
    public static IHttpClientBuilder AddA2AClient(this IServiceCollection services, string name, Action<A2ANamedClientOptions> configure)
    {
        services.Configure(name, configure);
        services.AddHttpClient();
        services.AddSingleton<IA2AClientFactory, DefaultA2AClientFactory>();
        return services.AddHttpClient(DefaultA2AClientFactory.HttpClientName(name));
    }

    /// <summary>Adds a bearer token to every request, fetched from <paramref name="tokenProvider"/> (cache inside it).</summary>
    public static IHttpClientBuilder AddBearerToken(this IHttpClientBuilder builder, Func<IServiceProvider, CancellationToken, ValueTask<string>> tokenProvider) =>
        builder.AddHttpMessageHandler(sp => new BearerTokenHandler(ct => tokenProvider(sp, ct)));
}

/// <summary>Sets <c>Authorization: Bearer</c> on each request.</summary>
public sealed class BearerTokenHandler(Func<CancellationToken, ValueTask<string>> tokenProvider) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await tokenProvider(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
