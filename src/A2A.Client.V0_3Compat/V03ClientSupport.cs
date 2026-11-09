using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using A2A.Client.Transports;
using A2A.V0_3Compat;

namespace A2A.Client.V0_3Compat;

/// <summary>Helpers shared by the two v0.3 transports and the factory.</summary>
internal static class V03ClientSupport
{
    public const string JsonMediaType = "application/json";

    /// <summary>A copy of <paramref name="source"/> that speaks v0.3. The caller's object is never changed.</summary>
    public static A2AClientOptions CopyForV03(A2AClientOptions? source, string? tenant = null)
    {
        source ??= new A2AClientOptions();
        return new A2AClientOptions
        {
            ProtocolVersion = V03Compat.HeaderVersion,
            Extensions = new List<string>(source.Extensions),
            Tenant = string.IsNullOrEmpty(tenant) ? source.Tenant : tenant,
            SupportedBindings = new List<string>(source.SupportedBindings),
            RestSubscribeMethod = source.RestSubscribeMethod,
            ValidateCapabilities = source.ValidateCapabilities,
            MaxRetries = source.MaxRetries,
            TimeProvider = source.TimeProvider,
            OnMessageReceived = source.OnMessageReceived,
            CircuitBreaker = source.CircuitBreaker,
            TransportFactories = new List<IA2ATransportFactory>(source.TransportFactories),
            EndpointPolicy = source.EndpointPolicy,
        };
    }

    /// <summary>
    /// v0.3 names the extensions header <c>X-A2A-Extensions</c>. Moves the value the shared code wrote under the v1.0 name,
    /// unless the caller already set the v0.3 header.
    /// </summary>
    public static void UseV03ExtensionsHeader(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues(A2AProtocol.Headers.Extensions, out var values)) return;
        var joined = string.Join(",", values);
        request.Headers.Remove(A2AProtocol.Headers.Extensions);
        if (!request.Headers.Contains(V03Compat.ExtensionsHeader)) request.Headers.TryAddWithoutValidation(V03Compat.ExtensionsHeader, joined);
    }

    /// <summary>Unsupported-operation error for what v0.3 does not have.</summary>
    public static A2AException Unsupported(string what) =>
        new(A2AErrorCode.UnsupportedOperation, $"{what} does not exist in A2A protocol v0.3.");

    /// <summary>
    /// The A2A error code of a numeric error <paramref name="code"/>, or null when it is not an A2A, JSON-RPC or SDK code
    /// (an HTTP status, for example).
    /// </summary>
    public static A2AErrorCode? CodeOf(long code) =>
        code is <= -32000 and >= int.MinValue && Enum.IsDefined((A2AErrorCode)(int)code) ? (A2AErrorCode)(int)code : null;

    /// <summary>The integer value of a JSON number node, or null.</summary>
    public static long? Long(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<long>(out var l) ? l : null;

    /// <summary>The string value of a JSON string node, or null.</summary>
    public static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Wraps a codec failure as an invalid-response error.</summary>
    public static T Read<T>(Func<T> read, string what)
    {
        try { return read(); }
        catch (JsonException ex) { throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"Malformed {what} in agent response: {ex.Message}", ex); }
    }

    /// <summary>Parses UTF-8 JSON. Returns null when it is not JSON.</summary>
    public static JsonNode? TryParse(ReadOnlySpan<byte> utf8)
    {
        try { return V03Json.Parse(utf8); }
        catch (JsonException) { return null; }
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>The error a non-A2A HTTP status stands for, when the body has no usable code.</summary>
    public static A2AErrorCode FromStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => A2AErrorCode.Unauthenticated,
        HttpStatusCode.Forbidden => A2AErrorCode.PermissionDenied,
        HttpStatusCode.NotFound => A2AErrorCode.TaskNotFound,
        HttpStatusCode.BadRequest => A2AErrorCode.InvalidParams,
        _ => A2AErrorCode.Internal,
    };
}
