using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
/// The <see cref="IAgentCardProvider"/> of an ASP.NET Core host: the default copy with the extension entries, and, when the card lists
/// no interfaces, one interface per mapped binding at <c>(PublicBaseUrl ?? first listen address ?? "http://localhost") + pattern</c>.
/// The listen addresses are only known once the server has started, so without a <see cref="A2AHttpOptions.PublicBaseUrl"/> a card
/// built before <c>ApplicationStarted</c> is returned but not cached. After that the card is cached. The host's card is never
/// changed. Bindings mapped at a pattern with a <c>{</c> (a tenant template) are not listed.
/// </summary>
internal sealed class AgentCardCompleter(A2AServerOptions options, IEnumerable<A2AExtensionOperation> extensions,
    IOptions<A2AHttpOptions> http, A2AEndpointRegistry registry, IServer? server = null, IHostApplicationLifetime? lifetime = null)
    : AgentCardProvider(options, extensions)
{
    protected override bool CanCache =>
        http.Value.PublicBaseUrl is not null || lifetime is null || lifetime.ApplicationStarted.IsCancellationRequested;

    protected override void Complete(AgentCard copy)
    {
        if (copy.SupportedInterfaces.Count != 0) return;
        var baseUrl = BaseUrl(http.Value, server);
        foreach (var (binding, pattern) in registry.Bindings())
            copy.SupportedInterfaces.Add(new AgentInterface { Url = Combine(baseUrl, pattern), ProtocolBinding = binding });
    }

    private static string BaseUrl(A2AHttpOptions http, IServer? server)
    {
        if (http.PublicBaseUrl is { } configured) return configured.GetLeftPart(UriPartial.Authority) + configured.AbsolutePath.TrimEnd('/');
        var address = server?.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        return address is null ? "http://localhost" : Concrete(address).TrimEnd('/');
    }

    /// <summary>Kestrel reports wildcard binds (<c>http://+:5000</c>, <c>http://[::]:5000</c>); a client cannot connect to those, so they become <c>localhost</c>.</summary>
    private static string Concrete(string address)
    {
        var scheme = address.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return address;
        var hostStart = scheme + 3;
        var hostEnd = address.IndexOfAny(['/', '?'], hostStart);
        var authority = hostEnd < 0 ? address[hostStart..] : address[hostStart..hostEnd];
        var rest = hostEnd < 0 ? "" : address[hostEnd..];
        string host, port = "";
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            host = authority[..(close + 1)];
            port = authority[(close + 1)..];
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            host = colon < 0 ? authority : authority[..colon];
            port = colon < 0 ? "" : authority[colon..];
        }
        if (host is "+" or "*" or "0.0.0.0" or "[::]" || host.Length == 0) host = "localhost";
        return address[..hostStart] + host + port + rest;
    }

    private static string Combine(string baseUrl, string pattern)
    {
        var path = pattern.Trim('/');
        return path.Length == 0 ? baseUrl : baseUrl + "/" + path;
    }
}
