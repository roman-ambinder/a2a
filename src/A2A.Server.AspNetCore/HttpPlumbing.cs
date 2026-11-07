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

        string? idempotencyKey = http.Request.Headers[A2AProtocol.Headers.IdempotencyKey];

        var tenant = tenantResolver.Resolve(http) ?? "";
        var user = http.User;
        var principal = ServerCallContext.AnonymousPrincipal;
        if (user.Identity?.IsAuthenticated == true)
        {
            principal = principalResolver.Resolve(user);
            // "anonymous" is reserved for unauthenticated callers, also when a custom resolver returns it.
            if (string.IsNullOrEmpty(principal) || string.Equals(principal, ServerCallContext.AnonymousPrincipal, StringComparison.Ordinal))
                throw new A2AException(A2AErrorCode.Unauthenticated);
        }

        return new ServerCallContext
        {
            User = user,
            Principal = principal,
            RemoteAddress = http.Connection.RemoteIpAddress?.ToString(),
            ProtocolVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            RequestedExtensions = extensions,
            IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
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

    private static readonly byte[] IdPrefix = "id: "u8.ToArray();
    private static readonly byte[] EventPrefix = "event: "u8.ToArray();
    private static readonly byte[] DataPrefix = "data: "u8.ToArray();
    private static readonly byte[] LineEnd = "\n"u8.ToArray();
    private static readonly byte[] EventSuffix = "\n\n"u8.ToArray();
    private static readonly byte[] KeepAlive = ": ka\n\n"u8.ToArray();

    /// <summary>The header a reconnecting SSE client sends with the id of the last event it saw.</summary>
    public const string LastEventIdHeader = "Last-Event-ID";

    /// <summary>Stand-in for a <c>Last-Event-ID</c> that is present but unusable: the handler answers with a snapshot.</summary>
    private const long UnusableCursor = -1;

    /// <summary>The raw <c>Last-Event-ID</c> header, or null when absent or empty.</summary>
    public static string? LastEventId(HttpContext http)
    {
        string? value = http.Request.Headers[LastEventIdHeader];
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// The resume point for a stream of task <paramref name="taskId"/>: null without a <c>Last-Event-ID</c> header (a fresh
    /// subscribe), its sequence when it is a cursor for this task, and a negative value (snapshot first) for any other value.
    /// </summary>
    public static long? ResumeAfter(HttpContext http, string taskId) =>
        LastEventId(http) is not { } value ? null
        : StreamCursor.TryParse(value, taskId, out var seq) ? seq
        : UnusableCursor;

    /// <summary>
    /// Writes one SSE frame. <paramref name="id"/> null writes no <c>id:</c> line; <paramref name="eventType"/> null writes
    /// no <c>event:</c> line.
    /// </summary>
    public static async Task WriteEventAsync(HttpContext http, string? id, byte[] json, string? eventType = null)
    {
        var body = http.Response.Body;
        if (eventType is not null)
        {
            await body.WriteAsync(EventPrefix, http.RequestAborted).ConfigureAwait(false);
            await body.WriteAsync(Encoding.UTF8.GetBytes(eventType), http.RequestAborted).ConfigureAwait(false);
            await body.WriteAsync(LineEnd, http.RequestAborted).ConfigureAwait(false);
        }
        if (id is not null)
        {
            await body.WriteAsync(IdPrefix, http.RequestAborted).ConfigureAwait(false);
            await body.WriteAsync(Encoding.UTF8.GetBytes(id), http.RequestAborted).ConfigureAwait(false);
            await body.WriteAsync(LineEnd, http.RequestAborted).ConfigureAwait(false);
        }
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

    /// <summary>The SSE id of a stored task event (<c>{taskId}:{sequence}</c>); null for events that are not stored (sequence 0).</summary>
    public static string? EventId(SequencedEvent evt)
    {
        if (evt.Sequence <= 0) return null;
        var taskId = evt.Event switch
        {
            TaskEvent t => t.Task.Id,
            StatusUpdateEvent s => s.StatusUpdate.TaskId,
            ArtifactUpdateEvent a => a.ArtifactUpdate.TaskId,
            _ => null,
        };
        return string.IsNullOrEmpty(taskId) ? null : StreamCursor.Format(taskId, evt.Sequence);
    }

    /// <summary>A signal the handler fires when a resume is caught up; <see cref="PrimeAsync"/> then starts the response.</summary>
    public static (Action Signal, Task Fired) CaughtUpSignal()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return (() => tcs.TrySetResult(), tcs.Task);
    }

    /// <summary>A stream whose first element may still be on its way.</summary>
    public sealed record PrimedStream(IAsyncEnumerator<SequencedEvent> Events, Task<bool> First);

    /// <summary>How long priming waits for the first element when keep-alives are off.</summary>
    private static readonly TimeSpan DefaultPrimePatience = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Asks for the first element before the response starts, so validation errors become normal error responses rather
    /// than broken streams. Stops waiting when <paramref name="startNow"/> completes (the handler accepted a resume that has
    /// nothing to send yet) or after <paramref name="keepAlive"/> (1 second when keep-alives are off), so a quiet stream
    /// still starts. Returns null if the sequence is empty.
    /// </summary>
    public static async Task<PrimedStream?> PrimeAsync(IAsyncEnumerable<SequencedEvent> source, TimeSpan keepAlive, Task? startNow, CancellationToken ct)
    {
        var e = source.GetAsyncEnumerator(ct);
        Task<bool>? first = null;
        try
        {
            first = e.MoveNextAsync().AsTask();
            if (!first.IsCompleted)
            {
                var patience = keepAlive > TimeSpan.Zero ? keepAlive : DefaultPrimePatience;
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = Task.Delay(patience, delayCts.Token);
                await (startNow is null ? Task.WhenAny(first, delay) : Task.WhenAny(first, delay, startNow)).ConfigureAwait(false);
                delayCts.Cancel();
            }
            if (!first.IsCompleted) return new PrimedStream(e, first);
            if (await first.ConfigureAwait(false)) return new PrimedStream(e, first);
            await e.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch
        {
            await DrainAndDisposeAsync(e, first).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Writes the primed stream as SSE, with an <c>id:</c> line on every stored task event and keep-alive comments when idle.
    /// Always disposes the enumerator, which releases the stream's resources (for example its stream slot).
    /// </summary>
    public static async Task PumpAsync(HttpContext http, PrimedStream stream, Func<StreamResponse, byte[]> encode,
        Func<Exception, byte[]> encodeError, TimeSpan keepAlive, ILogger logger, string? errorEventType = null)
    {
        var e = stream.Events;
        var next = stream.First;
        try
        {
            // Nothing to send yet (a caught-up resume, a quiet stream): send the headers now so the client sees the stream.
            if (!next.IsCompleted) await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
            while (true)
            {
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
                await WriteEventAsync(http, EventId(e.Current), encode(e.Current.Event)).ConfigureAwait(false);
                next = e.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // client went away; the task keeps running
        }
        catch (Exception ex)
        {
            try { await WriteEventAsync(http, null, encodeError(ex), errorEventType).ConfigureAwait(false); }
            catch (Exception writeEx) { logger.LogDebug(writeEx, "Could not write stream error event"); }
        }
        finally
        {
            await DrainAndDisposeAsync(e, next).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the enumerator. An async iterator cannot be disposed while a MoveNext is running, so a pending one is awaited
    /// first; the enumerator was created with the request's abort token, so it ends once the client is gone.
    /// </summary>
    private static async Task DrainAndDisposeAsync(IAsyncEnumerator<SequencedEvent> e, Task<bool>? pending)
    {
        if (pending is { IsCompleted: false })
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception) { /* the stream is ending anyway */ }
        }
        await e.DisposeAsync().ConfigureAwait(false);
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
