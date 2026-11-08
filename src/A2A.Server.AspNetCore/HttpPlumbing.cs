using System.Buffers;
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

internal static partial class HttpPlumbing
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
                AspNetCoreLog.UnhandledError(logger, ex);
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

    private static ReadOnlySpan<byte> IdPrefix => "id: "u8;
    private static ReadOnlySpan<byte> DataPrefix => "data: "u8;
    private static ReadOnlySpan<byte> EventSuffix => "\n\n"u8;
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
    /// Writes one SSE frame (<c>id: {id}\n</c>, <c>data: {json}\n\n</c>) with a single write, then flushes. <paramref name="id"/>
    /// null writes no <c>id:</c> line. The frame is built in a pooled buffer that is returned after the write.
    /// </summary>
    public static async Task WriteEventAsync(HttpContext http, string? id, ReadOnlyMemory<byte> json)
    {
        var length = (id is null ? 0 : IdPrefix.Length + Encoding.UTF8.GetByteCount(id) + 1) + DataPrefix.Length + json.Length + EventSuffix.Length;
        var frame = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var written = BuildFrame(frame, id, json.Span);
            var body = http.Response.Body;
            await body.WriteAsync(frame.AsMemory(0, written), http.RequestAborted).ConfigureAwait(false);
            await body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static int BuildFrame(Span<byte> frame, string? id, ReadOnlySpan<byte> json)
    {
        var at = 0;
        if (id is not null)
        {
            IdPrefix.CopyTo(frame);
            at = IdPrefix.Length;
            at += Encoding.UTF8.GetBytes(id, frame[at..]);
            frame[at++] = (byte)'\n';
        }
        DataPrefix.CopyTo(frame[at..]);
        at += DataPrefix.Length;
        json.CopyTo(frame[at..]);
        at += json.Length;
        EventSuffix.CopyTo(frame[at..]);
        return at + EventSuffix.Length;
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
    public static async Task<PrimedStream?> PrimeAsync(IAsyncEnumerable<SequencedEvent> source, TimeSpan keepAlive, Task? startNow, CancellationToken cancellationToken)
    {
        var e = source.GetAsyncEnumerator(cancellationToken);
        Task<bool>? first = null;
        try
        {
            first = e.MoveNextAsync().AsTask();
            if (!first.IsCompleted)
            {
                var patience = keepAlive > TimeSpan.Zero ? keepAlive : DefaultPrimePatience;
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
    /// <remarks><paramref name="encode"/> returns the <c>data:</c> payload of an event (for example its stored <see cref="SequencedEvent.Json"/>).</remarks>
    public static async Task PumpAsync(HttpContext http, PrimedStream stream, Func<SequencedEvent, ReadOnlyMemory<byte>> encode,
        Func<Exception, byte[]> encodeError, TimeSpan keepAlive, ILogger logger)
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
                var current = e.Current;
                await WriteEventAsync(http, EventId(current), encode(current)).ConfigureAwait(false);
                next = e.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // client went away; the task keeps running
        }
        catch (Exception ex)
        {
            try { await WriteEventAsync(http, null, encodeError(ex)).ConfigureAwait(false); }
            catch (Exception writeEx) { AspNetCoreLog.StreamErrorWriteFailed(logger, writeEx); }
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
}
