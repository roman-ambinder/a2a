using System.Text;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace A2A.Server.AspNetCore;

internal static class HttpPlumbing
{
    public static ServerCallContext CreateCallContext(HttpContext http)
    {
        var extensions = http.Request.Headers[A2AProtocol.Headers.Extensions]
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? version = http.Request.Headers[A2AProtocol.Headers.Version];
        if (string.IsNullOrWhiteSpace(version)) version = http.Request.Query[A2AProtocol.Headers.Version];

        var services = http.RequestServices;
        var tenantResolver = services.GetRequiredService<ITenantResolver>();
        var principalResolver = services.GetRequiredService<IPrincipalResolver>();

        var tenant = tenantResolver.Resolve(http) ?? "";
        var user = http.User;
        var principal = ServerCallContext.AnonymousPrincipal;
        if (user.Identity?.IsAuthenticated == true)
        {
            principal = principalResolver.Resolve(user);
            if (string.IsNullOrEmpty(principal)) throw new A2AException(A2AErrorCode.Unauthenticated);
        }

        return new ServerCallContext
        {
            User = user,
            Principal = principal,
            ProtocolVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            RequestedExtensions = extensions,
            Tenant = tenant,
            TenantFromRoute = http.GetRouteValue("tenant") is string { Length: > 0 },
        };
    }

    public static void WriteExtensionsHeader(HttpContext http, ServerCallContext call)
    {
        if (call.ActivatedExtensions.Count > 0 && !http.Response.HasStarted)
            http.Response.Headers[A2AProtocol.Headers.Extensions] = string.Join(",", call.ActivatedExtensions);
    }

    /// <summary>Resolves <see cref="A2AServerOptions.IncludeExceptionDetails"/>: null means Development only.</summary>
    public static bool ShouldIncludeDetails(A2AServerOptions options, IHostEnvironment? environment) =>
        options.IncludeExceptionDetails ?? environment?.IsDevelopment() ?? false;

    /// <summary>The HTTP status for an error: an explicit <see cref="A2AException.HttpStatus"/> wins over the code's default.</summary>
    public static int StatusFor(A2AException ex) => ex.HttpStatus ?? A2AErrors.HttpStatus(ex.Code);

    public static void SetRetryAfter(HttpContext http, A2AException ex)
    {
        if (ex.RetryAfter is not { } after || http.Response.HasStarted) return;
        http.Response.Headers.RetryAfter = ((long)Math.Ceiling(after.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static A2AException Normalize(Exception ex, ILogger logger, bool includeDetails)
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
                return includeDetails
                    ? new A2AException(A2AErrorCode.Internal, $"{A2AErrors.DefaultMessage(A2AErrorCode.Internal)}: {ex.GetType().Name}: {ex.Message}")
                    : new A2AException(A2AErrorCode.Internal);
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
        var scheme = card.SecuritySchemes?.Values.Select(s => (s as HttpAuthScheme)?.Value.Scheme).FirstOrDefault(s => s is not null) ?? "Bearer";
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
    public static async Task<IAsyncEnumerator<SequencedEvent>?> PrimeAsync(IAsyncEnumerable<SequencedEvent> source, CancellationToken ct)
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
    public static async Task PumpAsync(HttpContext http, IAsyncEnumerator<SequencedEvent> e, Func<StreamResponse, byte[]> encode,
        Func<Exception, byte[]> encodeError, TimeSpan keepAlive, ILogger logger)
    {
        try
        {
            await WriteEventAsync(http, encode(e.Current.Event)).ConfigureAwait(false);
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
                await WriteEventAsync(http, encode(e.Current.Event)).ConfigureAwait(false);
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

    /// <summary>Reads the request body. More than <paramref name="maxBytes"/> bytes is <see cref="A2AException.PayloadTooLarge"/> (HTTP 413).</summary>
    public static async Task<byte[]> ReadBodyAsync(HttpRequest request, long maxBytes)
    {
        if (request.ContentLength > maxBytes) throw A2AException.PayloadTooLarge(maxBytes);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, request.HttpContext.RequestAborted).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > maxBytes) throw A2AException.PayloadTooLarge(maxBytes);
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Parses a request body once, enforcing <paramref name="maxDepth"/> and rejecting duplicate property names. Valid JSON that
    /// breaks a limit is InvalidParams (<see cref="A2AException"/>). Malformed JSON throws <see cref="JsonException"/>.
    /// On .NET 10 the parser rejects duplicates itself. On .NET 8 it cannot, so <see cref="ContainsDuplicateProperties"/> checks after the parse.
    /// </summary>
    public static JsonDocument ParseBody(byte[] body, int maxDepth)
    {
        try
        {
#if NET10_0_OR_GREATER
            return JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = maxDepth, AllowDuplicateProperties = false });
#else
            var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = maxDepth });
            if (!ContainsDuplicateProperties(body, maxDepth)) return doc;
            doc.Dispose();
            throw A2AException.InvalidParams("Invalid request body: duplicate property names are not allowed");
#endif
        }
        catch (JsonException ex) when (IsWellFormed(body))
        {
            throw A2AException.InvalidParams($"Invalid request body: {ex.Message}");
        }
    }

    /// <summary>Failure path only: tells a limit violation (valid JSON) from a syntax error. Nesting beyond 1024 counts as malformed.</summary>
    private static bool IsWellFormed(byte[] body)
    {
        try
        {
            using var _ = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 1024 });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when any JSON object in <paramref name="json"/> repeats a property name (compared after unescaping, case sensitive).
    /// Used on .NET 8, whose parser accepts duplicates and silently keeps the last value. Throws <see cref="JsonException"/>
    /// for malformed JSON or nesting deeper than <paramref name="maxDepth"/>.
    /// </summary>
    internal static bool ContainsDuplicateProperties(ReadOnlySpan<byte> json, int maxDepth)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = maxDepth });
        var scopes = new Stack<HashSet<string>?>(); // one entry per open container; null for arrays
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!scopes.Peek()!.Add(reader.GetString()!)) return true;
                    break;
            }
        }
        return false;
    }

    public static string Utf8(byte[] b) => Encoding.UTF8.GetString(b);
}
