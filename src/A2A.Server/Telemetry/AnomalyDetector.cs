using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace A2A.Server.Telemetry;

/// <summary>
///     Flags unusual patterns of one caller (spec §13.4): more task creations or CancelTask calls in one minute than
///     <see cref="A2AAnomalyOptions" /> allows. It only reports, through <see cref="SecurityEventLog.RecordAnomaly" />, once
///     per caller, kind and window; it never refuses or delays a call. Built only when the detector is enabled, so a server
///     without it pays nothing per request.
/// </summary>
/// <remarks>
///     A caller is a (tenant, principal) pair. Each has a fixed one-minute window per kind that starts at its first counted
///     call. At most <see cref="A2AAnomalyOptions.MaxTrackedCallers" /> callers are tracked. When the table is full, callers
///     whose windows have ended are dropped (a scan at most once every <see cref="PruneInterval" />); if none has, a new
///     caller is not tracked, so a flood of new identities cannot push out a caller that is already being counted.
/// </remarks>
internal sealed class AnomalyDetector
{
    /// <summary>The kind of an anomaly: too many new tasks.</summary>
    public const string TaskCreation = "task_creation";

    /// <summary>The kind of an anomaly: too many CancelTask calls.</summary>
    public const string Cancellation = "cancellation";

    /// <summary>The length of one window.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    /// <summary>Shortest time between two scans for ended windows while the table is full.</summary>
    internal static readonly TimeSpan PruneInterval = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<(string Tenant, string Principal), Caller> _callers = new();
    private readonly int _cancellations;
    private readonly int _capacity;
    private readonly TimeProvider _clock;
    private readonly int _creations;
    private readonly ILogger? _logger;
    private long _nextPrune;
    private int _tracked;

    private AnomalyDetector(A2AAnomalyOptions options, ILogger? logger, TimeProvider clock)
    {
        _creations = options.TaskCreationsPerMinute;
        _cancellations = options.CancellationsPerMinute;
        _capacity = options.MaxTrackedCallers;
        _logger = logger;
        _clock = clock;
    }

    /// <summary>Callers tracked now. For tests.</summary>
    internal int TrackedCallers => Volatile.Read(ref _tracked);

    /// <summary>
    ///     The detector of <paramref name="options" />, or null when <see cref="A2AAnomalyOptions.Enabled" /> is false.
    ///     Checks the settings when enabled.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is out of range.</exception>
    public static AnomalyDetector? Create(A2AServerOptions options, TimeProvider clock)
    {
        var anomalies = options.Anomalies;
        if (!anomalies.Enabled) return null;
        anomalies.Validate();
        return new AnomalyDetector(anomalies, options.SecurityLogger, clock);
    }

    /// <summary>Counts a new task of the caller.</summary>
    public void TaskCreated(ServerCallContext call, string operation)
    {
        Count(call, operation, true);
    }

    /// <summary>Counts a CancelTask call of the caller.</summary>
    public void CancelRequested(ServerCallContext call, string operation)
    {
        Count(call, operation, false);
    }

    private void Count(ServerCallContext call, string operation, bool creation)
    {
        var now = _clock.GetUtcNow().UtcTicks;
        var key = (call.Tenant, call.Principal);
        var threshold = creation ? _creations : _cancellations;
        while (true)
        {
            if (!_callers.TryGetValue(key, out var caller) && (caller = Add(key, now)) is null)
                return; // the table is full of active callers
            int count;
            lock (caller.Gate)
            {
                if (caller.Removed) continue; // dropped by a scan just now: count it in a fresh entry
                ref var window = ref creation ? ref caller.Creations : ref caller.Cancellations;
                if (window.Count == 0 || now - window.Start >= Window.Ticks || now < window.Start)
                    window = new Counter { Start = now };
                window.Count++;
                if (now > caller.Last) caller.Last = now;
                if (window.Reported || window.Count <= threshold) return;
                window.Reported = true;
                count = window.Count;
            }

            SecurityEventLog.RecordAnomaly(_logger, creation ? TaskCreation : Cancellation, operation, call.Tenant,
                call.Principal, count, threshold);
            return;
        }
    }

    /// <summary>Tracks a new caller, or returns the one another thread just added; null when the table is full.</summary>
    private Caller? Add((string Tenant, string Principal) key, long now)
    {
        while (true)
        {
            if (Interlocked.Increment(ref _tracked) > _capacity)
            {
                Interlocked.Decrement(ref _tracked);
                if (!Prune(now)) return null;
            }
            else
            {
                var fresh = new Caller { Last = now };
                if (_callers.TryAdd(key, fresh)) return fresh;
                Interlocked.Decrement(ref _tracked);
            }

            if (_callers.TryGetValue(key, out var existing)) return existing;
        }
    }

    /// <summary>
    ///     Drops the callers whose windows have all ended. At most one scan per <see cref="PruneInterval" />, by one thread;
    ///     false when no scan ran or it freed nothing.
    /// </summary>
    private bool Prune(long now)
    {
        var due = Volatile.Read(ref _nextPrune);
        if (now < due || Interlocked.CompareExchange(ref _nextPrune, now + PruneInterval.Ticks, due) != due)
            return false;
        var freed = false;
        foreach (var (key, caller) in _callers)
        {
            lock (caller.Gate)
            {
                if (now - caller.Last < Window.Ticks) continue;
                caller.Removed = true;
            }

            if (_callers.TryRemove(new KeyValuePair<(string, string), Caller>(key, caller)))
            {
                Interlocked.Decrement(ref _tracked);
                freed = true;
            }
        }

        return freed;
    }

    private struct Counter
    {
        public long Start;
        public int Count;
        public bool Reported;
    }

    private sealed class Caller
    {
        public readonly Lock Gate = new();
        public Counter Cancellations;
        public Counter Creations;
        public long Last;
        public bool Removed;
    }
}
