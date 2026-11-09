using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Audit;

/// <summary>
/// Makes the audit events and writes them to the <see cref="A2AAuditSink"/>. Registered by <c>AddA2AServer</c>; the request
/// handler, the streamer and the admin classes use it. It has no public methods: you replace the sink, not the auditor.
/// </summary>
/// <remarks>
/// Every write cleans the strings of the event (<see cref="Clean"/>), then waits for the sink at most
/// <see cref="A2AAuditOptions.WriteTimeout"/>. The sink's token is cancelled when the wait ends, and the wait does not depend on
/// the sink honoring it, so a sink that hangs cannot hang a request. With <see cref="A2AAuditOptions.FailClosed"/>,
/// <see cref="BeginAsync"/> writes the <see cref="A2AAuditOutcome.Attempted"/> row before the change and refuses the request when
/// that write fails; every other write only logs a failure.
/// </remarks>
public sealed class A2AAuditor
{
    /// <summary>Longest string of an event. Longer ones are cut.</summary>
    internal const int MaxFieldLength = 256;

    /// <summary>Most detached writes (see <see cref="RecordDetached"/>) in flight at once; more are dropped.</summary>
    internal const int MaxDetached = 1024;

    private readonly A2AAuditSink? _sink;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly bool _failClosed;
    private readonly TimeSpan _timeout;
    private int _detached;

    /// <summary>Creates the auditor. In an app the DI container does this.</summary>
    /// <param name="sink">Where the events go.</param>
    /// <param name="options">The server options; <see cref="A2AServerOptions.Audit"/> is checked here.</param>
    /// <param name="logger">Optional logger for write failures.</param>
    /// <param name="timeProvider">Optional clock, for event times and the write timeout.</param>
    /// <exception cref="InvalidOperationException"><see cref="A2AAuditOptions.WriteTimeout"/> is not positive.</exception>
    public A2AAuditor(A2AAuditSink sink, A2AServerOptions options, ILogger<A2AAuditor>? logger = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        options.Audit.Validate();
        _sink = sink;
        _failClosed = options.Audit.FailClosed;
        _timeout = options.Audit.WriteTimeout;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    private A2AAuditor()
    {
        _logger = NullLogger.Instance;
        _time = TimeProvider.System;
    }

    /// <summary>An auditor that writes nothing: for a handler or admin class built by hand without one.</summary>
    internal static A2AAuditor None { get; } = new();

    /// <summary>Detached writes in flight. For tests.</summary>
    internal int DetachedInFlight => Volatile.Read(ref _detached);

    // =====================================================================
    // Making events
    // =====================================================================

    /// <summary>A new <see cref="A2AAuditOutcome.Attempted"/> event for a call.</summary>
    internal A2AAuditEvent For(A2AAuditAction action, ServerCallContext call, string? taskId = null, string? resource = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Time = _time.GetUtcNow(),
            Action = action,
            Outcome = A2AAuditOutcome.Attempted,
            Actor = call.Principal,
            Tenant = call.Tenant,
            TaskId = taskId,
            Resource = resource,
            Binding = call.Binding,
            RemoteAddress = call.RemoteAddress,
            TraceId = CurrentTraceId(),
        };

    /// <summary>A new <see cref="A2AAuditOutcome.Attempted"/> event for an admin call, which has no call context.</summary>
    internal A2AAuditEvent For(A2AAuditAction action, string actor, string tenant, string? taskId = null, string? resource = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Time = _time.GetUtcNow(),
            Action = action,
            Outcome = A2AAuditOutcome.Attempted,
            Actor = actor,
            Tenant = tenant,
            TaskId = taskId,
            Resource = resource,
            TraceId = CurrentTraceId(),
        };

    private static string? CurrentTraceId() =>
        Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity && activity.TraceId != default ? activity.TraceId.ToString() : null;

    /// <summary>The reason an exception is recorded with: the error code name, <c>Canceled</c>, or <c>Error</c>. Never the message.</summary>
    internal static string ReasonOf(Exception exception) => exception switch
    {
        A2AException a2a => a2a.Code.ToString(),
        OperationCanceledException => "Canceled",
        _ => "Error",
    };

    // =====================================================================
    // Writing
    // =====================================================================

    /// <summary>
    /// Step 1 of fail closed: writes <paramref name="e"/> as <see cref="A2AAuditOutcome.Attempted"/> before the change. A no-op
    /// when fail open. When the write fails or times out, logs and throws <see cref="A2AErrorCode.Internal"/> (with no sink text
    /// in it), so the caller must call this before it changes anything.
    /// </summary>
    internal async ValueTask BeginAsync(A2AAuditEvent e, CancellationToken cancellationToken)
    {
        if (_sink is null || !_failClosed) return;
        try
        {
            await WriteAsync(e with { Outcome = A2AAuditOutcome.Attempted }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // the caller went away; nothing changed
        }
        catch (Exception ex)
        {
            ServerLog.AuditBeginFailed(_logger, ex, e.Action, e.Id);
            throw new A2AException(A2AErrorCode.Internal, "The request could not be audited.");
        }
    }

    /// <summary>Step 2: writes <paramref name="e"/> as <see cref="A2AAuditOutcome.Succeeded"/>. Never throws.</summary>
    internal ValueTask SucceedAsync(A2AAuditEvent e) => RecordAsync(e with { Outcome = A2AAuditOutcome.Succeeded, Time = _time.GetUtcNow() });

    /// <summary>Step 2: writes <paramref name="e"/> as <see cref="A2AAuditOutcome.Failed"/>, with the reason of <paramref name="exception"/>. Never throws.</summary>
    internal ValueTask FailAsync(A2AAuditEvent e, Exception exception) => FailAsync(e, ReasonOf(exception));

    /// <summary>Step 2: writes <paramref name="e"/> as <see cref="A2AAuditOutcome.Failed"/> with a fixed <paramref name="reason"/>. Never throws.</summary>
    internal ValueTask FailAsync(A2AAuditEvent e, string reason) => RecordAsync(e with { Outcome = A2AAuditOutcome.Failed, Reason = reason, Time = _time.GetUtcNow() });

    /// <summary>
    /// Writes <paramref name="e"/> as it is. Never throws: a failure is logged. Uses no caller token, so a client that
    /// disconnects cannot lose the record; the write timeout still applies.
    /// </summary>
    internal async ValueTask RecordAsync(A2AAuditEvent e)
    {
        if (_sink is null) return;
        try
        {
            await WriteAsync(e, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ServerLog.AuditWriteFailed(_logger, ex, e.Action, e.Id);
        }
    }

    /// <summary>
    /// Writes <paramref name="e"/> without waiting, for the synchronous <see cref="A2ARequestHandler.ValidateCall"/>. At most
    /// <see cref="MaxDetached"/> writes are in flight; later events are dropped with a log, so a flood of anonymous calls cannot
    /// pile up tasks.
    /// </summary>
    internal void RecordDetached(A2AAuditEvent e)
    {
        if (_sink is null) return;
        if (Interlocked.Increment(ref _detached) > MaxDetached)
        {
            Interlocked.Decrement(ref _detached);
            ServerLog.AuditDetachedDropped(_logger, e.Action, e.Id, MaxDetached);
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await RecordAsync(e).ConfigureAwait(false); }
            finally { Interlocked.Decrement(ref _detached); }
        });
    }

    /// <summary>
    /// Runs <paramref name="run"/> as one audited action: <see cref="BeginAsync"/>, the action, then the final row. An exception
    /// of <paramref name="run"/> is written as <see cref="A2AAuditOutcome.Failed"/> and thrown again. <paramref name="describe"/>
    /// adds the resource and reason a result knows (for example the id of a created push config).
    /// </summary>
    internal async ValueTask<T> AuditedAsync<T>(A2AAuditEvent e, Func<ValueTask<T>> run, Func<T, (string? Resource, string? Reason)>? describe,
        CancellationToken cancellationToken)
    {
        await BeginAsync(e, cancellationToken).ConfigureAwait(false);
        T result;
        try
        {
            result = await run().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailAsync(e, ex).ConfigureAwait(false);
            throw;
        }
        if (describe is not null)
        {
            var (resource, reason) = describe(result);
            e = e with { Resource = resource ?? e.Resource, Reason = reason ?? e.Reason };
        }
        await SucceedAsync(e).ConfigureAwait(false);
        return result;
    }

    private async ValueTask WriteAsync(A2AAuditEvent e, CancellationToken cancellationToken)
    {
        var clean = Sanitize(e);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            // The wait ends at the timeout even when the sink ignores its token.
            await _sink!.WriteAsync(clean, cts.Token).AsTask().WaitAsync(_timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CancelQuietly(cts);
        }
    }

    private static void CancelQuietly(CancellationTokenSource cts)
    {
        try { cts.Cancel(); }
        catch (AggregateException) { /* a callback of the sink threw; the write is over anyway */ }
    }

    // =====================================================================
    // Cleaning
    // =====================================================================

    /// <summary>The event with every string cleaned (see <see cref="Clean"/>).</summary>
    internal static A2AAuditEvent Sanitize(A2AAuditEvent e) => e with
    {
        Id = Clean(e.Id)!,
        Actor = Clean(e.Actor)!,
        Tenant = Clean(e.Tenant)!,
        TaskId = Clean(e.TaskId),
        Resource = Clean(e.Resource),
        Operation = Clean(e.Operation),
        Reason = Clean(e.Reason),
        Binding = Clean(e.Binding),
        RemoteAddress = Clean(e.RemoteAddress),
        TraceId = Clean(e.TraceId),
    };

    /// <summary>
    /// At most <see cref="MaxFieldLength"/> characters (never cut inside a surrogate pair); control characters and lone
    /// surrogates become <c>_</c>. This stops log forging, and keeps a NUL or a broken surrogate from making a database refuse the row.
    /// </summary>
    internal static string? Clean(string? value)
    {
        if (value is null) return null;
        var length = Math.Min(value.Length, MaxFieldLength);
        if (length < value.Length && char.IsHighSurrogate(value[length - 1]) && char.IsLowSurrogate(value[length])) length--;
        if (length == value.Length && IsClean(value)) return value;
        return string.Create(length, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = source[i];
                if (char.IsControl(c)) span[i] = '_';
                else if (char.IsHighSurrogate(c) && i + 1 < span.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    span[i] = c;
                    span[i + 1] = source[i + 1];
                    i++;
                }
                else if (char.IsSurrogate(c)) span[i] = '_';
                else span[i] = c;
            }
        });
    }

    private static bool IsClean(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c)) return false;
            if (!char.IsSurrogate(c)) continue;
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) i++;
            else return false;
        }
        return true;
    }
}
