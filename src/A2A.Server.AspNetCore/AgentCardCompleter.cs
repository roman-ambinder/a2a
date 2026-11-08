using System.Text.Json;
using A2A.Json;
using A2A.Server.Pipeline;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>
/// The <see cref="IAgentCardProvider"/> of an ASP.NET Core host. On the first <see cref="Get"/> (after the server has started, when
/// its listen addresses are known) it copies <see cref="A2AServerOptions.AgentCard"/>, lists the registered extension operations
/// in its capabilities, and, when the card lists no interfaces, adds one interface per mapped binding at
/// <c>(PublicBaseUrl ?? first listen address ?? "http://localhost") + pattern</c> (no server in the container: <c>http://localhost</c>). The result is cached; the host's card is never
/// changed. Bindings mapped at a pattern with a <c>{</c> (a tenant template) are not listed.
/// </summary>
internal sealed class AgentCardCompleter : IAgentCardProvider
{
    private readonly Lazy<AgentCard> _card;

    public AgentCardCompleter(IOptions<A2AServerOptions> options, IOptions<A2AHttpOptions> http, A2AEndpointRegistry registry,
        IEnumerable<A2AExtensionOperation> extensions, IServer? server = null) =>
        _card = new(() => Build(options.Value.AgentCard, http.Value, registry, server, extensions), LazyThreadSafetyMode.ExecutionAndPublication);

    public AgentCard Get() => _card.Value;

    private static AgentCard Build(AgentCard hostCard, A2AHttpOptions http, A2AEndpointRegistry registry, IServer? server,
        IEnumerable<A2AExtensionOperation> extensions)
    {
        var card = JsonSerializer.Deserialize(A2AJson.SerializeToUtf8Bytes(hostCard), A2AJson.GetTypeInfo<AgentCard>())
            ?? throw new InvalidOperationException("The Agent Card could not be copied.");

        foreach (var extension in extensions)
        {
            var list = card.Capabilities.Extensions ??= [];
            if (list.Exists(e => string.Equals(e.Uri, extension.ExtensionUri, StringComparison.Ordinal))) continue;
            list.Add(new AgentExtension { Uri = extension.ExtensionUri });
        }

        if (card.SupportedInterfaces.Count == 0)
        {
            var baseUrl = BaseUrl(http, server);
            foreach (var (binding, pattern) in registry.Bindings())
                card.SupportedInterfaces.Add(new AgentInterface { Url = Combine(baseUrl, pattern), ProtocolBinding = binding });
        }
        return card;
    }

    private static string BaseUrl(A2AHttpOptions http, IServer? server)
    {
        if (http.PublicBaseUrl is { } configured) return configured.AbsoluteUri.TrimEnd('/');
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
