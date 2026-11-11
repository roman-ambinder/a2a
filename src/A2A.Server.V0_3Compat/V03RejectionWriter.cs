using A2A.Server.AspNetCore;
using A2A.V0_3Compat;
using Microsoft.AspNetCore.Http;

namespace A2A.Server.V0_3Compat;

/// <summary>
///     Writes an error raised before a v0.3 HTTP+JSON endpoint runs (the rate limit rejection) as v0.3 writes every error:
///     <c>{ code, message }</c> with the HTTP status of the code, and <c>Retry-After</c>. It sits in the metadata of the
///     v0.3 HTTP+JSON endpoints. v0.3 JSON-RPC shares the v1.0 JSON-RPC endpoint, whose rejection is a JSON-RPC error already.
/// </summary>
internal sealed class V03RejectionWriter : IA2ARejectionWriter
{
    public static readonly V03RejectionWriter Instance = new();

    public Task? TryWriteAsync(HttpContext http, A2AException error)
    {
        if (http.Response.HasStarted) return Task.CompletedTask;
        HttpPlumbing.SetRetryAfter(http, error);
        var status = HttpPlumbing.StatusFor(error);
        http.Response.StatusCode = status;
        http.Response.ContentType = A2AProtocol.JsonMediaType;
        return http.Response.Body
            .WriteAsync(V03Json.ToUtf8(V03ProtoJsonCodec.WriteError(error, status)), http.RequestAborted).AsTask();
    }
}
