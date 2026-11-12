using System.Runtime.CompilerServices;
using A2A.Server.Agents;
using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
///     Registration marker and endpoint registry. <c>MapA2A</c> throws when it is missing, so the startup check cannot be
///     skipped by mistake. Each <c>Map*</c> call records the data sources of the application's route builder here (the
///     root, also when it is called on a group). The validator reads them
///     directly: the <c>EndpointDataSource</c> in DI is only filled when the web host builds its pipeline, which happens
///     after hosted services start.
/// </summary>
internal sealed class A2AEndpointRegistry
{
    private readonly List<(string Binding, string Pattern)> _bindings = [];

    private readonly ConditionalWeakTable<ICollection<EndpointDataSource>, object> _cardMapped = new();
    private readonly List<ICollection<EndpointDataSource>> _sources = [];

    /// <summary>Rewrites the served Agent Card body (set by a protocol adapter). Null: the card is served as encoded.</summary>
    public Func<ReadOnlyMemory<byte>, byte[]>? CardBodyTransform { get; set; }

    /// <summary>
    ///     True when a protocol adapter serves the card at the legacy path <c>/.well-known/agent.json</c> of A2A 0.1 to 0.2.x
    ///     (v0.3 compat). False: that path answers 404.
    /// </summary>
    public bool ServeLegacyCardPath { get; set; }

    public bool HasSources
    {
        get
        {
            lock (_sources)
            {
                return _sources.Count > 0;
            }
        }
    }

    public void Add(ICollection<EndpointDataSource> source)
    {
        lock (_sources)
        {
            if (!_sources.Any(s => ReferenceEquals(s, source))) _sources.Add(source);
        }
    }

    /// <summary>
    ///     Records that <paramref name="binding" /> (a <see cref="ProtocolBindings" /> value) is mapped at
    ///     <paramref name="pattern" />.
    /// </summary>
    public void AddBinding(string binding, string pattern)
    {
        lock (_bindings)
        {
            if (!_bindings.Contains((binding, pattern))) _bindings.Add((binding, pattern));
        }
    }

    /// <summary>The recorded <c>(Binding, Pattern)</c> pairs, in mapping order.</summary>
    public IReadOnlyList<(string Binding, string Pattern)> Bindings()
    {
        lock (_bindings)
        {
            return _bindings.ToList();
        }
    }

    private readonly List<(string Binding, string Pattern)> _templateBindings = [];

    /// <summary>
    ///     Records a binding mapped at a pattern with a route parameter (a tenant template). It is never listed on a card
    ///     automatically; the startup check warns about it.
    /// </summary>
    public void AddTemplateBinding(string binding, string pattern)
    {
        lock (_templateBindings)
        {
            if (!_templateBindings.Contains((binding, pattern))) _templateBindings.Add((binding, pattern));
        }
    }

    /// <summary>The recorded template <c>(Binding, Pattern)</c> pairs.</summary>
    public IReadOnlyList<(string Binding, string Pattern)> TemplateBindings()
    {
        lock (_templateBindings)
        {
            return _templateBindings.ToList();
        }
    }

    /// <summary>
    ///     True the first time it is called for <paramref name="source" /> (the route builder's data sources); false
    ///     after.
    /// </summary>
    public bool TryMarkCardMapped(ICollection<EndpointDataSource> source)
    {
        lock (_cardMapped)
        {
            return _cardMapped.TryAdd(source, true);
        }
    }

    public IReadOnlyList<Endpoint> Endpoints()
    {
        lock (_sources)
        {
            return _sources.SelectMany(s => s.ToList()).SelectMany(d => d.Endpoints).ToList();
        }
    }
}

/// <summary>Endpoint metadata: an A2A <c>Map*</c> method (or <c>WithA2AStartupCheck</c>) made this endpoint.</summary>
internal sealed class A2AEndpointMetadata
{
    public static readonly A2AEndpointMetadata Instance = new();
}

/// <summary>
///     Fail-closed startup check. Every A2A endpoint (made by an A2A <c>Map*</c> method, display name starting
///     <c>"A2A "</c>, except the Agent Card endpoints), as the app serves it with the conventions of outer groups,
///     must carry authorization metadata and no <see cref="IAllowAnonymous" /> metadata, unless
///     <see cref="A2AServerOptions.AllowAnonymous" /> is true. When no endpoint is registered yet at start (Startup-style
///     hosting
///     maps endpoints while the web host starts), the check runs again on <c>ApplicationStarted</c> and stops the app on
///     failure.
///     <see cref="A2ARequestHandler" /> also rejects anonymous callers on its own as a backstop. The same check fails the
///     start when the Agent Card has no name (<c>AddAgentCard(...)</c> was not called).
/// </summary>
internal sealed class A2AStartupValidator(
    A2AEndpointRegistry registry,
    IOptions<A2AServerOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<A2AStartupValidator> logger,
    IServiceProvider services,
    IEnumerable<A2AExtensionOperation> extensions,
    IHostEnvironment? environment = null,
    IOptions<A2AHttpOptions>? http = null,
    IServer? server = null) : IHostedService
{
    internal const string NoPolicyMessage =
        "A2A endpoints have no authorization policy. On the A2A builder, call AddApiKeyAuthentication(), AddBearerAuthentication() or RequirePolicy(\"...\"), or call app.MapA2A(...).RequireAuthorization(). To allow anonymous callers, call Configure(o => o.AllowAnonymous = true), or ConfigureForDevelopment() while you develop.";

    /// <summary><see cref="NoPolicyMessage" />, and why <c>ConfigureForDevelopment()</c> did not apply when it was called.</summary>
    internal static string NoPolicy(IServiceProvider services, IHostEnvironment? environment)
    {
        if (services.GetService<A2ACallerPolicyExtensions.DevelopmentDefaultsMarker>() is null ||
            environment?.IsDevelopment() == true)
            return NoPolicyMessage;
        return NoPolicyMessage +
               $" ConfigureForDevelopment() has no effect here: the host runs as '{environment?.EnvironmentName ?? "(no host environment)"}', not Development.";
    }

    internal const string AnonymousWarning =
        "A2A endpoints allow anonymous callers. All anonymous callers share one owner and can read each other's tasks.";

    internal const string NoCardNameMessage =
        "The Agent Card has no name. Call AddAgentCard(...) on the A2A builder or set A2AServerOptions.AgentCard.";

    private const string Prefix = "A2A ";
    private const string AgentCard = "A2A Agent Card";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CheckCardName();
        await CheckTrustedBearerSchemesAsync().ConfigureAwait(false);
        await CheckExtensionPoliciesAsync().ConfigureAwait(false);
        // Listen addresses, and so the completed card, are known only once the server has started.
        lifetime.ApplicationStarted.Register(CheckCardInterfaces);
        if (options.Value.AllowAnonymous) AspNetCoreLog.AnonymousCallersAllowed(logger);
        // Spec 3.6.2: "Agents MUST interpret empty value as 0.3 version". The setting breaks that MUST on purpose, for curl
        // in Development; elsewhere a 0.3 client's call without the header would be served as 1.0.
        if (options.Value.TreatMissingVersionAsCurrent && environment?.IsDevelopment() != true)
            AspNetCoreLog.MissingVersionTreatedAsCurrent(logger);

        if (!registry.HasSources)
        {
            lifetime.ApplicationStarted.Register(() =>
            {
                try
                {
                    Check();
                }
                catch (InvalidOperationException ex)
                {
                    AspNetCoreLog.StoppingApplication(logger, ex, ex.Message);
                    lifetime.StopApplication();
                }
            });
            return;
        }

        Check();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Fails when the Agent Card has no name (a forgotten <c>AddAgentCard(...)</c>), unless a custom
    ///     <see cref="IAgentCardProvider" />
    ///     that does not build on <see cref="AgentCardProvider" /> supplies the card, or a
    ///     <see cref="Agents.TenantCardProvider" />
    ///     supplies each tenant's card.
    /// </summary>
    private void CheckCardName()
    {
        if (services.GetService<IAgentCardProvider>() is not (null or AgentCardProvider)) return;
        if (services.GetService<TenantCardProvider>() is not null) return;
        if (string.IsNullOrWhiteSpace(options.Value.AgentCard?.Name))
            throw new InvalidOperationException(NoCardNameMessage);
    }

    /// <summary>
    ///     Fails when <see cref="A2AHttpOptions.TrustedBearerSchemes" /> names a scheme that is not registered: a typo
    ///     would otherwise silently keep no token.
    /// </summary>
    private async Task CheckTrustedBearerSchemesAsync()
    {
        if (http?.Value.TrustedBearerSchemes is not { Count: > 0 } trusted) return;
        // Without AddAuthentication() there is no scheme provider at all: the same answer as a missing scheme.
        var provider = services.GetService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();
        foreach (var name in trusted.Order(StringComparer.Ordinal))
            if (provider is null || await provider.GetSchemeAsync(name).ConfigureAwait(false) is null)
                throw new InvalidOperationException(
                    $"A2AHttpOptions.TrustedBearerSchemes lists the authentication scheme '{name}', but no such scheme is registered. Register it with builder.Services.AddAuthentication().AddScheme(...), or remove it from the list.");
    }

    /// <summary>Fails when an extension operation names an authorization policy the app cannot evaluate (fail closed).</summary>
    private async Task CheckExtensionPoliciesAsync()
    {
        foreach (var extension in extensions)
        {
            if (extension.AuthorizationPolicy is not { } policy) continue;
            var provider = services.GetService<IAuthorizationPolicyProvider>();
            if (services.GetService<IAuthorizationService>() is null || provider is null)
                throw new InvalidOperationException(
                    $"Extension operation '{extension.Method}' names authorization policy '{policy}', but no IAuthorizationService is registered. Call services.AddAuthorization().");
            if (await provider.GetPolicyAsync(policy).ConfigureAwait(false) is null)
                throw new InvalidOperationException(
                    $"Extension operation '{extension.Method}' names authorization policy '{policy}', which is not registered.");
        }
    }

    /// <summary>
    ///     Warns (never fails) when the Agent Card would send other agents to the wrong place (spec 5.2, 8.3.1) or over plain
    ///     HTTP (13.4): a mapped binding the card does not list, a binding at a tenant pattern (never listed automatically),
    ///     and, outside Development, an interface on a local address, an interface on plain HTTP, and a server with no HTTPS
    ///     address and no https PublicBaseUrl. Also warns about an empty REQUIRED array on the card (5.7).
    /// </summary>
    internal void CheckCardInterfaces()
    {
        try
        {
            var production = environment?.IsDevelopment() != true;
            var bindings = registry.Bindings();
            foreach (var (binding, pattern) in registry.TemplateBindings())
                AspNetCoreLog.TenantBindingNotListed(logger, binding, pattern);

            AgentCard? card = null;
            if (services.GetService<IAgentCardProvider>() is { } cards)
                try
                {
                    card = cards.Get();
                }
                catch (InvalidOperationException)
                {
                    // One agent per tenant: no host card to check.
                }

            var plainInterface = false;
            if (card is not null)
            {
                CheckRequiredArrays(card);
                foreach (var (binding, pattern) in bindings)
                    if (!card.SupportedInterfaces.Any(i => string.Equals(i.ProtocolBinding, binding, StringComparison.OrdinalIgnoreCase)))
                        AspNetCoreLog.MappedBindingNotOnCard(logger, binding, pattern);

                if (production)
                    foreach (var itf in card.SupportedInterfaces)
                    {
                        // A gRPC "host:port" address has no scheme and means TLS.
                        if (!Uri.TryCreate(itf.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                            uri = Uri.TryCreate("https://" + itf.Url, UriKind.Absolute, out var grpc) ? grpc : null;
                        if (uri is null) continue;
                        if (IsLocal(uri.Host)) AspNetCoreLog.CardInterfaceIsLocal(logger, itf.ProtocolBinding, itf.Url);
                        else if (uri.Scheme == Uri.UriSchemeHttp)
                        {
                            plainInterface = true;
                            AspNetCoreLog.CardInterfaceNotEncrypted(logger, itf.ProtocolBinding, itf.Url);
                        }
                    }
            }

            if (production && !plainInterface && http?.Value.PublicBaseUrl?.Scheme != Uri.UriSchemeHttps
                && server?.Features.Get<IServerAddressesFeature>()?.Addresses is { Count: > 0 } addresses
                && !addresses.Any(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                AspNetCoreLog.NoEncryptedEndpoint(logger, string.Join(", ", addresses));
        }
        catch (Exception ex)
        {
            AspNetCoreLog.CardCheckFailed(logger, ex);
        }
    }

    /// <summary>
    ///     Spec 5.7: the card's REQUIRED arrays must have at least one element. An empty <c>supportedInterfaces</c> is
    ///     refused when the card is served; the others only warn, since the SDK reads cards leniently (spec-decisions.md).
    /// </summary>
    private void CheckRequiredArrays(AgentCard card)
    {
        if (card.DefaultInputModes is not { Count: > 0 }) AspNetCoreLog.CardRequiredArrayEmpty(logger, "defaultInputModes");
        if (card.DefaultOutputModes is not { Count: > 0 }) AspNetCoreLog.CardRequiredArrayEmpty(logger, "defaultOutputModes");
        if (card.Skills is not { Count: > 0 }) AspNetCoreLog.CardRequiredArrayEmpty(logger, "skills");
        else
            foreach (var skill in card.Skills)
                if (skill.Tags is not { Count: > 0 })
                    AspNetCoreLog.CardRequiredArrayEmpty(logger, $"skills[id={skill.Id}].tags");
    }

    private static bool IsLocal(string host)
    {
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var ip) && System.Net.IPAddress.IsLoopback(ip));
    }

    private void Check()
    {
        // The registry holds the application's data sources, so these are the endpoints as served, with the conventions
        // of every outer group. Only those an A2A Map* method made (marked, and named "A2A ...") are A2A endpoints.
        foreach (var endpoint in registry.Endpoints())
        {
            if (endpoint.Metadata.GetMetadata<A2AEndpointMetadata>() is null) continue;
            var name = endpoint.DisplayName;
            if (name is null || !name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            if (name.StartsWith(AgentCard, StringComparison.Ordinal)) continue; // card and JWKS stay anonymous
            if (!options.Value.AllowAnonymous
                && (endpoint.Metadata.GetMetadata<IAuthorizeData>() is null ||
                    endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null))
                throw new InvalidOperationException(NoPolicy(services, environment));
        }
    }
}