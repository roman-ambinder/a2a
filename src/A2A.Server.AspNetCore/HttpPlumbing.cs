using System.Text;
using System.Text.Json;
using A2A.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace A2A.Server.AspNetCore;

internal static class HttpPlumbing
{
    public static ServerCallContext CreateCallContext(HttpContext http, A2AHttpOptions options, string? tenant = null)
    {
        var extensions = http.Request.Headers[A2AProtocol.Headers.Extensions]
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? version = http.Request.Headers[A2AProtocol.Headers.Version];
        if (string.IsNullOrWhiteSpace(version)) version = http.Request.Query[A2AProtocol.Headers.Version];

        return new ServerCallContext
        {
            User = http.User,
            CallerId = options.CallerIdResolver(http),
            ProtocolVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            RequestedExtensions = extensions,
            Tenant = string.IsNullOrEmpty(tenant) ? null : tenant,
        };
    }

    public static void WriteExtensionsHeader(HttpContext http, ServerCallContext call)
    {
        if (call.ActivatedExtensions.Count > 0 && !http.Response.HasStarted)
            http.Response.Headers[A2AProtocol.Headers.Extensions] = string.Join(",", call.ActivatedExtensions);
    }

    public static A2AException Normalize(Exception ex, ILogger logger)
    {
        switch (ex)
        {
            case A2AException a2a:
                return a2a;
            case JsonException json:
                return A2AException.InvalidParams($"Invalid request body: {json.Message}");
            case BadHttpRequestException bad:
                return new A2AException(A2AErrorCode.InvalidRequest, bad.Message);
            default:
                logger.LogError(ex, "Unhandled error in A2A endpoint");
                return new A2AException(A2AErrorCode.Internal);
        }
    }

    public static async Task WriteJsonAsync<T>(HttpContext http, T value, string contentType, int status = 200)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = contentType;
        await http.Response.Body.WriteAsync(A2AJson.SerializeToUtf8Bytes(value), http.RequestAborted).ConfigureAwait(false);
    }

    public static void SetAuthChallenge(HttpContext http, A2AException ex, AgentCard card)
    {
        if (ex.Code != A2AErrorCode.Unauthenticated || http.Response.HasStarted) return;
        var scheme = card.SecuritySchemes?.Values.Select(s => s.HttpAuthSecurityScheme?.Scheme).FirstOrDefault(s => s is not null) ?? "Bearer";
        http.Response.Headers.WWWAuthenticate = new StringValues(scheme);
    }

    public static void StartEventStream(HttpContext http)
    {
        http.Response.StatusCode = 200;
        http.Response.ContentType = A2AProtocol.EventStreamMediaType;
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static readonly byte[] DataPrefix = "data: "u8.ToArray();
    private static readonly byte[] EventSuffix = "\n\n"u8.ToArray();
    private static readonly byte[] KeepAlive = ": keep-alive\n\n"u8.ToArray();

    public static async Task WriteEventAsync(HttpContext http, byte[] json)
    {
        var body = http.Response.Body;
        await body.WriteAsync(DataPrefix, http.RequestAborted).ConfigureAwait(false);
        await body.WriteAsync(json, http.RequestAborted).ConfigureAwait(false);
        await body.WriteAsync(EventSuffix, http.RequestAborted).ConfigureAwait(false);
        await body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }

    public static async Task WriteKeepAliveAsync(HttpContext http)
    {
        await http.Response.Body.WriteAsync(KeepAlive, http.RequestAborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls the first element before the response starts, so validation errors become normal error responses
    /// rather than broken streams. Returns null if the sequence is empty.
    /// </summary>
    public static async Task<IAsyncEnumerator<StreamResponse>?> PrimeAsync(IAsyncEnumerable<StreamResponse> source, CancellationToken ct)
    {
        var e = source.GetAsyncEnumerator(ct);
        try
        {
            if (await e.MoveNextAsync().ConfigureAwait(false)) return e;
            await e.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await e.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Writes the primed enumerator as SSE, emitting keep-alive comments when idle.</summary>
    public static async Task PumpAsync(HttpContext http, IAsyncEnumerator<StreamResponse> e, Func<StreamResponse, byte[]> encode,
        Func<Exception, byte[]> encodeError, TimeSpan keepAlive, ILogger logger)
    {
        try
        {
            await WriteEventAsync(http, encode(e.Current)).ConfigureAwait(false);
            while (true)
            {
                var next = e.MoveNextAsync().AsTask();
                while (keepAlive > TimeSpan.Zero && !next.IsCompleted)
                {
                    using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
                    var delay = Task.Delay(keepAlive, delayCts.Token);
                    if (await Task.WhenAny(next, delay).ConfigureAwait(false) == next)
                    {
                        delayCts.Cancel();
                        break;
                    }
                    http.RequestAborted.ThrowIfCancellationRequested();
                    await WriteKeepAliveAsync(http).ConfigureAwait(false);
                }
                if (!await next.ConfigureAwait(false)) break;
                await WriteEventAsync(http, encode(e.Current)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // client went away; the task keeps running
        }
        catch (Exception ex)
        {
            try { await WriteEventAsync(http, encodeError(ex)).ConfigureAwait(false); }
            catch (Exception writeEx) { logger.LogDebug(writeEx, "Could not write stream error event"); }
        }
        finally
        {
            await e.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static async Task<byte[]> ReadBodyAsync(HttpRequest request, long maxBytes)
    {
        if (request.ContentLength > maxBytes) throw new A2AException(A2AErrorCode.InvalidRequest, "Request body too large");
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes) throw new A2AException(A2AErrorCode.InvalidRequest, "Request body too large");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
