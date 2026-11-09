using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using A2A.Diagnostics;
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
    /// <param name="http">The request.</param>
    /// <param name="binding">The binding for traces.</param>
    /// <param name="versionOverride">When set, used as the protocol version instead of the A2A-Version header (the v0.3 compat surface sends "1.0": the converted request is a v1.0 call).</param>
    /// <param name="skipRequiredExtensions">True for a route a plain HTTP client calls (the artifact download): required extensions are not enforced (<see cref="ServerCallContext.SkipRequiredExtensions"/>).</param>
    /// <param name="wireVersion">The version the caller used on the wire, when it differs from the one the call carries (<see cref="ServerCallContext.WireProtocolVersion"/>).</param>
    public static ServerCallContext CreateCallContext(HttpContext http, string? binding, string? versionOverride = null, string? wireVersion = null, bool skipRequiredExtensions = false)
    {
        var extensions = http.Request.Headers[A2AProtocol.Headers.Extensions]
            .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? version = http.Request.Headers[A2AProtocol.Headers.Version];
        if (string.IsNullOrWhiteSpace(version)) version = http.Request.Query[A2AProtocol.Headers.Version];
        if (versionOverride is not null) version = versionOverride;

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

        // Kept only when a token exchanger is registered and the caller is authenticated. Never logged or stored.
        var bearer = principal != ServerCallContext.AnonymousPrincipal && services.GetService<A2A.Delegation.ITokenExchanger>() is not null
            ? BearerToken(http.Request.Headers.Authorization)
            : null;

        return new ServerCallContext
        {
            InboundBearerToken = bearer,
            User = user,
            Principal = principal,
            RemoteAddress = http.Connection.RemoteIpAddress?.ToString(),
            ProtocolVersion = string.IsNullOrWhiteSpace(version) ? null : version,
            WireProtocolVersion = wireVersion,
            RequestedExtensions = extensions,
            SkipRequiredExtensions = skipRequiredExtensions,
            IdempotencyKey = string.IsNullOrEmpty(idempotencyKey) ? null : idempotencyKey,
            Tenant = tenant,
            TenantFromRoute = http.GetRouteValue("tenant") is string { Length: > 0 },
            RouteValues = RouteValues(http),
            Binding = binding,
            TraceParent = A2ATelemetry.TryExtractTraceContext(
                http.Request.Headers[A2ATelemetry.TraceParentHeader], http.Request.Headers[A2ATelemetry.TraceStateHeader], out var parent) ? parent : default,
        };
    }

    /// <summary>
    /// The token of an <c>Authorization: Bearer</c> header (scheme ignoring case). Null when there is not exactly one header
    /// value, the value is empty, or the token holds whitespace or a comma. Values are never joined.
    /// </summary>
    private static string? BearerToken(StringValues values)
    {
        if (values.Count != 1 || values[0] is not { } header) return null;
        const string scheme = "Bearer";
        if (header.Length <= scheme.Length || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) || !char.IsWhiteSpace(header[scheme.Length]))
            return null;
        var token = header[scheme.Length..].Trim();
        if (token.Length == 0) return null;
        foreach (var c in token)
            if (c == ',' || char.IsWhiteSpace(c)) return null;
        return token;
    }

    /// <summary>The request's route values as strings (invariant culture).</summary>
    public static IReadOnlyDictionary<string, string?> RouteValues(HttpContext http)
    {
        var values = http.Request.RouteValues;
        if (values.Count == 0) return Agents.AgentRequestScope.NoRouteValues;
        var copy = new Dictionary<string, string?>(values.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values) copy[name] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        return copy;
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

    /// <summary>True when the request's <c>Accept</c> header takes <c>text/event-stream</c> (with a weight above 0).</summary>
    public static bool AcceptsEventStream(HttpContext http)
    {
        foreach (var range in http.Request.GetTypedHeaders().Accept)
        {
            if (range.Quality is 0) continue;
            if (range.MediaType.Equals(A2AProtocol.EventStreamMediaType, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static async Task WriteJsonAsync<T>(HttpContext http, T value, string contentType, int status = 200)
    {
        await PooledResponse.WriteAsync(http, status, contentType, value, static (w, v) => JsonSerializer.Serialize(w, v, A2AJson.GetTypeInfo<T>())).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="WriteJsonAsync{T}(HttpContext, T, string, int)"/> in the shape of the caller's protocol version: a 1.0 caller
    /// never gets the members A2A 1.1 added (<see cref="A2AJson.OptionsFor"/>).
    /// </summary>
    public static async Task WriteJsonAsync<T>(HttpContext http, T value, string contentType, string? protocolVersion)
    {
        await PooledResponse.WriteAsync(http, 200, contentType, (value, info: A2AJson.GetTypeInfo<T>(protocolVersion)),
            static (w, s) => JsonSerializer.Serialize(w, s.value, s.info)).ConfigureAwait(false);
    }

    /// <summary>
    /// For an Unauthenticated error, sets <c>WWW-Authenticate</c> to the first HTTP auth scheme of the card of the call's tenant
    /// (else <c>Bearer</c>). The card comes from the <see cref="IAgentCardProvider"/>; a tenant without a card, or a provider that
    /// fails, falls back to the host's card (and to <c>Bearer</c> when that cannot be built either). Never throws.
    /// </summary>
    public static async ValueTask SetAuthChallengeAsync(HttpContext http, A2AException ex, ServerCallContext call)
    {
        if (ex.Code != A2AErrorCode.Unauthenticated || http.Response.HasStarted) return;
        var card = await ChallengeCardAsync(http, call).ConfigureAwait(false);
        if (http.Response.HasStarted) return;
        var scheme = card?.SecuritySchemes?.Values.Select(s => (s as HttpAuthScheme)?.Value.Scheme).FirstOrDefault(s => s is not null) ?? "Bearer";
        http.Response.Headers.WWWAuthenticate = new StringValues(scheme);
    }

    private static async ValueTask<AgentCard?> ChallengeCardAsync(HttpContext http, ServerCallContext call)
    {
        if (http.RequestServices.GetService<IAgentCardProvider>() is not { } cards) return null;
        try
        {
            return await cards.GetAsync(new Agents.AgentRequestScope(call.Tenant, call.RouteValues), http.RequestAborted).ConfigureAwait(false) ?? cards.Get();
        }
        catch (Exception)
        {
            try { return cards.Get(); }
            catch (InvalidOperationException) { return null; } // no host card either (one agent per tenant)
        }
    }

    public static void StartEventStream(HttpContext http)
    {
        http.Response.StatusCode = 200;
        http.Response.ContentType = A2AProtocol.EventStreamMediaType;
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static ReadOnlySpan<byte> EventPrefix => "event: "u8;
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
    /// Writes one SSE frame (<c>event: {eventType}\n</c>, <c>id: {id}\n</c>, <c>data: {json}\n\n</c>) with a single write, then
    /// flushes. <paramref name="id"/> null writes no <c>id:</c> line; <paramref name="eventType"/> null writes no <c>event:</c>
    /// line. The frame is built in a pooled buffer that is returned after the write.
    /// </summary>
    public static async Task WriteEventAsync(HttpContext http, string? id, ReadOnlyMemory<byte> json, string? eventType = null)
    {
        var length = (eventType is null ? 0 : EventPrefix.Length + Encoding.UTF8.GetByteCount(eventType) + 1)
            + (id is null ? 0 : IdPrefix.Length + Encoding.UTF8.GetByteCount(id) + 1) + DataPrefix.Length + json.Length + EventSuffix.Length;
        var frame = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            var written = BuildFrame(frame, eventType, id, json.Span);
            var body = http.Response.Body;
            await body.WriteAsync(frame.AsMemory(0, written), http.RequestAborted).ConfigureAwait(false);
            await body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    private static int BuildFrame(Span<byte> frame, string? eventType, string? id, ReadOnlySpan<byte> json)
    {
        var at = 0;
        if (eventType is not null)
        {
            EventPrefix.CopyTo(frame);
            at = EventPrefix.Length;
            at += Encoding.UTF8.GetBytes(eventType, frame[at..]);
            frame[at++] = (byte)'\n';
        }
        if (id is not null)
        {
            IdPrefix.CopyTo(frame[at..]);
            at += IdPrefix.Length;
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

    /// <summary>
    /// Writes one SSE frame for a stream event straight into the response pipe and flushes: <c>id: {taskId}:{sequence}</c> (when
    /// the event is stored), then <c>data: </c> + <paramref name="prefix"/> + <paramref name="json"/> + <paramref name="suffix"/>.
    /// No id string and no frame array are made: the frame is built in the pipe's own buffer.
    /// </summary>
    private static ValueTask<System.IO.Pipelines.FlushResult> WriteStreamEventAsync(HttpContext http, SequencedEvent evt,
        ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> json, ReadOnlySpan<byte> suffix)
    {
        var taskId = evt.Sequence > 0 ? TaskIdOf(evt) : null;
        var idLength = taskId is null ? 0 : IdPrefix.Length + Encoding.UTF8.GetByteCount(taskId) + 1 + 20 + 1; // ':' + long + '\n'
        var length = idLength + DataPrefix.Length + prefix.Length + json.Length + suffix.Length + EventSuffix.Length;
        var writer = http.Response.BodyWriter;
        var frame = writer.GetSpan(length);
        var at = 0;
        if (taskId is not null)
        {
            IdPrefix.CopyTo(frame);
            at = IdPrefix.Length;
            at += Encoding.UTF8.GetBytes(taskId, frame[at..]);
            frame[at++] = (byte)':';
            Utf8Formatter.TryFormat(evt.Sequence, frame[at..], out var digits);
            at += digits;
            frame[at++] = (byte)'\n';
        }
        DataPrefix.CopyTo(frame[at..]);
        at += DataPrefix.Length;
        prefix.CopyTo(frame[at..]);
        at += prefix.Length;
        json.CopyTo(frame[at..]);
        at += json.Length;
        suffix.CopyTo(frame[at..]);
        at += suffix.Length;
        EventSuffix.CopyTo(frame[at..]);
        writer.Advance(at + EventSuffix.Length);
        return writer.FlushAsync(http.RequestAborted);
    }

    public static async Task WriteKeepAliveAsync(HttpContext http)
    {
        await http.Response.Body.WriteAsync(KeepAlive, http.RequestAborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The SSE id of a stored task event (<c>{taskId}:{sequence}</c>); null for events that are not stored (sequence 0).</summary>
    public static string? EventId(SequencedEvent evt) =>
        evt.Sequence > 0 && TaskIdOf(evt) is { } taskId ? StreamCursor.Format(taskId, evt.Sequence) : null;

    /// <summary>The task id of a stream event, or null when it has none (a direct reply).</summary>
    private static string? TaskIdOf(SequencedEvent evt)
    {
        var taskId = evt.Event switch
        {
            TaskEvent t => t.Task.Id,
            StatusUpdateEvent s => s.StatusUpdate.TaskId,
            ArtifactUpdateEvent a => a.ArtifactUpdate.TaskId,
            MessageUpdateEvent u => u.MessageUpdate.TaskId,
            _ => null,
        };
        return string.IsNullOrEmpty(taskId) ? null : taskId;
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
    /// <remarks>
    /// <paramref name="encode"/> returns the <c>data:</c> payload of an event (for example its stored <see cref="SequencedEvent.Json"/>).
    /// A failure after the stream started is written as one last frame (<paramref name="encodeError"/>), named
    /// <paramref name="errorEventType"/> when set.
    /// </remarks>
    public static Task PumpAsync(HttpContext http, PrimedStream stream, Func<SequencedEvent, ReadOnlyMemory<byte>> encode,
        Func<Exception, byte[]> encodeError, TimeSpan keepAlive, ILogger logger, string? errorEventType = null) =>
        PumpAsync(http, stream, encode, default, default, encodeError, keepAlive, logger, errorEventType);

    /// <summary>
    /// <see cref="PumpAsync(HttpContext, PrimedStream, Func{SequencedEvent, ReadOnlyMemory{byte}}, Func{Exception, byte[]}, TimeSpan, ILogger, string?)"/>
    /// with an envelope: each <c>data:</c> payload is <paramref name="dataPrefix"/> + the encoded event + <paramref name="dataSuffix"/>
    /// (for example the JSON-RPC response around the event's stored bytes), written without building it per event.
    /// </summary>
    public static async Task PumpAsync(HttpContext http, PrimedStream stream, Func<SequencedEvent, ReadOnlyMemory<byte>> encode,
        ReadOnlyMemory<byte> dataPrefix, ReadOnlyMemory<byte> dataSuffix,
        Func<Exception, byte[]> encodeError, TimeSpan keepAlive, ILogger logger, string? errorEventType = null)
    {
        var e = stream.Events;
        Task<bool>? pending = stream.First; // the MoveNext still running, if any
        // One keep-alive timer per stream, not one per event: it is replaced only when it fires.
        using var beatStop = keepAlive > TimeSpan.Zero ? CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted) : null;
        Task? beat = null;
        var lastWrite = Stopwatch.GetTimestamp();
        try
        {
            // Nothing to send yet (a caught-up resume, a quiet stream): send the headers now so the client sees the stream.
            if (!pending.IsCompleted) await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
            var more = false;
            while (true)
            {
                if (pending is not null)
                {
                    while (beatStop is not null && !pending.IsCompleted)
                    {
                        var due = keepAlive - Stopwatch.GetElapsedTime(lastWrite);
                        if (due > TimeSpan.Zero)
                        {
                            beat ??= Task.Delay(due, beatStop.Token);
                            if (await Task.WhenAny(pending, beat).ConfigureAwait(false) == pending) break;
                            beat = null;
                            http.RequestAborted.ThrowIfCancellationRequested();
                            if (Stopwatch.GetElapsedTime(lastWrite) < keepAlive) continue; // an event was written since: wait again
                        }
                        await WriteKeepAliveAsync(http).ConfigureAwait(false);
                        lastWrite = Stopwatch.GetTimestamp();
                    }
                    more = await pending.ConfigureAwait(false);
                    pending = null;
                }
                if (!more) break;
                var current = e.Current;
                await WriteStreamEventAsync(http, current, dataPrefix.Span, encode(current).Span, dataSuffix.Span).ConfigureAwait(false);
                lastWrite = Stopwatch.GetTimestamp();
                var move = e.MoveNextAsync();
                if (move.IsCompletedSuccessfully) more = move.Result; // the next event is ready: no Task
                else pending = move.AsTask();
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // client went away; the task keeps running
        }
        catch (Exception ex)
        {
            try { await WriteEventAsync(http, null, encodeError(ex), errorEventType).ConfigureAwait(false); }
            catch (Exception writeEx) { AspNetCoreLog.StreamErrorWriteFailed(logger, writeEx); }
        }
        finally
        {
            beatStop?.Cancel(); // the keep-alive timer must not outlive the stream
            await DrainAndDisposeAsync(e, pending).ConfigureAwait(false);
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
