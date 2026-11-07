using System.Security.Cryptography;
using A2A.Json;
using A2A.Signing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace A2A.Server.AspNetCore;

/// <summary>Serves the public Agent Card (spec §8.2) with caching headers (§8.6) and optional JWS signing (§8.4).</summary>
internal sealed class AgentCardEndpoint(IOptions<A2AServerOptions> serverOptions, IOptions<A2AHttpOptions> httpOptions)
{
    private readonly Lazy<(byte[] Body, string ETag)> _card = new(() => Build(serverOptions.Value.AgentCard, httpOptions.Value.SigningKey));

    public Task GetCardAsync(HttpContext http)
    {
        var (body, etag) = _card.Value;
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = $"public, max-age={(int)httpOptions.Value.AgentCardMaxAge.TotalSeconds}";
        if (http.Request.Headers.IfNoneMatch.Any(v => v == etag || v == "*"))
        {
            http.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        return http.Response.Body.WriteAsync(body, http.RequestAborted).AsTask();
    }

    public Task GetJwksAsync(HttpContext http)
    {
        var key = httpOptions.Value.SigningKey;
        if (key is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }
        http.Response.Headers.CacheControl = $"public, max-age={(int)httpOptions.Value.AgentCardMaxAge.TotalSeconds}";
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        return http.Response.Body.WriteAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new JwkSet { Keys = [key.ToPublicJwk()] }), http.RequestAborted).AsTask();
    }

    private static (byte[], string) Build(AgentCard card, AgentCardSigningKey? key)
    {
        var served = key is null ? card : AgentCardSigner.Sign(card, key);
        var body = A2AJson.SerializeToUtf8Bytes(served);
        var etag = "\"" + Base64Url.Encode(SHA256.HashData(body))[..22] + "\"";
        return (body, etag);
    }
}
