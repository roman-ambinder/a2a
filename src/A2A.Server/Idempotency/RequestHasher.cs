using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Json;
using A2A.Signing;

namespace A2A.Server.Idempotency;

/// <summary>Fingerprint of a SendMessage request, to tell a retry from a different request that reuses the key.</summary>
public static class RequestHasher
{
    /// <summary>
    /// SHA-256 over the RFC 8785 canonical JSON of <c>{"message": ..., "configuration": ...}</c> in A2A wire form. Only the
    /// message and the configuration count: the tenant, the request metadata and the transport envelope (for example the
    /// JSON-RPC id, which a client changes on every attempt) are not part of the hash. A missing configuration is left out.
    /// </summary>
    public static byte[] Hash(SendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = new JsonObject
        {
            ["message"] = request.Message is null ? null : JsonSerializer.SerializeToNode(request.Message, A2AJson.GetTypeInfo<Message>()),
        };
        // A2A 1.1: the client's artifact updates are part of what the request does.
        if (request.ArtifactUpdates is { Count: > 0 } updates)
            root["artifactUpdates"] = JsonSerializer.SerializeToNode(updates, A2AJson.GetTypeInfo<List<TaskArtifactUpdateEvent>>());
        if (request.Configuration is { } configuration)
            root["configuration"] = JsonSerializer.SerializeToNode(configuration, A2AJson.GetTypeInfo<SendMessageConfiguration>());
        return SHA256.HashData(JsonCanonicalizer.Canonicalize(root));
    }
}
