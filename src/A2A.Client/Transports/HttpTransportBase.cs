using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using A2A.Json;

namespace A2A.Client.Transports;

/// <summary>Shared HTTP plumbing for the JSON-RPC and HTTP+JSON transports.</summary>
public abstract class HttpTransportBase
{
    protected HttpTransportBase(HttpClient httpClient, A2AClientOptions? options)
    {
        Http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Options = options ?? new A2AClientOptions();
    }

    protected HttpClient Http { get; }

    protected A2AClientOptions Options { get; }

    protected void ApplyHeaders(HttpRequestMessage request, bool streaming)
    {
        request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.Version, Options.ProtocolVersion);
        if (Options.Extensions.Count > 0)
            request.Headers.TryAddWithoutValidation(A2AProtocol.Headers.Extensions, string.Join(",", Options.Extensions));
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(streaming ? A2AProtocol.EventStreamMediaType : A2AProtocol.MediaType));
        if (!streaming) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.JsonMediaType));
    }

    protected static HttpContent JsonBody<T>(T body, string mediaType)
    {
        var content = new ByteArrayContent(A2AJson.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType) { CharSet = "utf-8" };
        return content;
    }

    protected static bool IsJson(HttpResponseMessage response)
    {
        var mt = response.Content.Headers.ContentType?.MediaType;
        return mt is not null && (mt.EndsWith("/json", StringComparison.OrdinalIgnoreCase) || mt.EndsWith("+json", StringComparison.OrdinalIgnoreCase));
    }

    protected static bool IsEventStream(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, A2AProtocol.EventStreamMediaType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Error for a non-success response with no parseable A2A error body.</summary>
    protected static A2AException HttpError(HttpResponseMessage response, string? body)
    {
        var status = (int)response.StatusCode;
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => A2AErrorCode.Unauthenticated,
            HttpStatusCode.Forbidden => A2AErrorCode.PermissionDenied,
            HttpStatusCode.NotFound => A2AErrorCode.MethodNotFound,
            _ => A2AErrorCode.Internal,
        };
        var msg = $"HTTP {status} {response.ReasonPhrase}";
        if (!string.IsNullOrWhiteSpace(body) && body.Length < 2048) msg += $": {body}";
        return new A2AException(code, msg) { HttpStatus = status };
    }

    protected static A2AException ProtocolError(string message, Exception? inner = null) =>
        new(A2AErrorCode.InvalidAgentResponse, message, inner);

    protected static T Parse<T>(JsonElement element, string what)
    {
        try { return A2AJson.Deserialize<T>(element); }
        catch (JsonException ex) { throw ProtocolError($"Malformed {what} in agent response: {ex.Message}", ex); }
    }
}
