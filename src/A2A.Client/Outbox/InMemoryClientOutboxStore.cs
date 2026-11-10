namespace A2A.Client.Outbox;

/// <summary>
///     A <see cref="ClientOutboxStore" /> in memory. Entries are lost when the process ends, so it is for tests and for
///     work you can
///     afford to lose. Thread-safe. Deadlines use the <see cref="TimeProvider" />.
/// </summary>
public sealed class InMemoryClientOutboxStore : ClientOutboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>Creates a store.</summary>
    /// <param name="time">The clock. Default <see cref="TimeProvider.System" />.</param>
    public InMemoryClientOutboxStore(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public override ValueTask<bool> EnqueueAsync(ClientOutboxMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_rows.ContainsKey(message.Id)) return new ValueTask<bool>(false);
            _rows[message.Id] = new Row
            {
                Id = message.Id,
                Peer = message.Peer,
                Correlation = message.Correlation,
                Request = message.Request.ToArray(),
                CreatedAt = now,
                DueAt = now
            };
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<ClientOutboxEntry?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            return new ValueTask<ClientOutboxEntry?>(
                _rows.TryGetValue(id, out var row) ? row.ToEntry(now, false) : null);
        }
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseDueAsync(int max, TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        if (max <= 0) return new ValueTask<IReadOnlyList<ClientOutboxEntry>>(Array.Empty<ClientOutboxEntry>());
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var picked = _rows.Values
                .Where(r => r.Status is ClientOutboxStatus.Queued or ClientOutboxStatus.Tracking && r.DueAt <= now &&
                            r.LeaseFree(now))
                .OrderBy(r => r.DueAt).ThenBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(max)
                .ToList();
            var result = new List<ClientOutboxEntry>(picked.Count);
            foreach (var row in picked)
            {
                var before = row.InDoubt;
                row.Attempts++;
                if (row.Status == ClientOutboxStatus.Queued) row.InDoubt = true;
                row.TakeLease(now, lease);
                result.Add(row.ToEntry(now, true, before));
            }

            return new ValueTask<IReadOnlyList<ClientOutboxEntry>>(result);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> RescheduleAsync(ClientOutboxEntry leased, TimeSpan delay, string? error,
        bool inDoubt, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!TryLeased(leased, out var row) ||
                row.Status is not (ClientOutboxStatus.Queued or ClientOutboxStatus.Tracking))
                return new ValueTask<bool>(false);
            row.DueAt = now + NonNegative(delay);
            row.LastError = error;
            row.InDoubt = inDoubt;
            row.ClearLease();
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> MarkTrackingAsync(ClientOutboxEntry leased, string taskId, string? contextId,
        TaskState state, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!TryLeased(leased, out var row) ||
                row.Status is not (ClientOutboxStatus.Queued or ClientOutboxStatus.Tracking))
                return new ValueTask<bool>(false);
            row.Status = ClientOutboxStatus.Tracking;
            row.RemoteTaskId = taskId;
            row.RemoteContextId = contextId;
            row.RemoteState = state;
            row.Attempts = 0;
            row.InDoubt = false;
            row.LastError = null;
            row.DueAt = now + NonNegative(delay);
            row.ClearLease();
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> FinishAsync(ClientOutboxEntry leased, ClientOutboxOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        lock (_gate)
        {
            if (!TryLeased(leased, out var row) ||
                row.Status is not (ClientOutboxStatus.Queued or ClientOutboxStatus.Tracking))
                return new ValueTask<bool>(false);
            row.Status = outcome.Status;
            row.RemoteTaskId = outcome.RemoteTaskId ?? row.RemoteTaskId;
            row.RemoteContextId = outcome.RemoteContextId ?? row.RemoteContextId;
            row.RemoteState = outcome.RemoteState ?? row.RemoteState;
            row.Result = outcome.Result.IsEmpty ? null : outcome.Result.ToArray();
            row.LastError = outcome.Error;
            row.InDoubt = outcome.InDoubt;
            row.ClearLease();
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<IReadOnlyList<ClientOutboxEntry>> LeaseResultsAsync(string? correlation, int max,
        TimeSpan lease, CancellationToken cancellationToken = default)
    {
        if (max <= 0) return new ValueTask<IReadOnlyList<ClientOutboxEntry>>(Array.Empty<ClientOutboxEntry>());
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var picked = _rows.Values
                .Where(r => r.Status is ClientOutboxStatus.Finished or ClientOutboxStatus.Failed &&
                            r.CollectedAt is null && r.LeaseFree(now)
                            && (correlation is null ||
                                string.Equals(r.Correlation, correlation, StringComparison.Ordinal)))
                .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)
                .Take(max)
                .ToList();
            var result = new List<ClientOutboxEntry>(picked.Count);
            foreach (var row in picked)
            {
                row.TakeLease(now, lease);
                result.Add(row.ToEntry(now, true));
            }

            return new ValueTask<IReadOnlyList<ClientOutboxEntry>>(result);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> AcknowledgeAsync(ClientOutboxEntry leased,
        CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!TryLeased(leased, out var row) ||
                row.Status is not (ClientOutboxStatus.Finished or ClientOutboxStatus.Failed) ||
                row.CollectedAt is not null) return new ValueTask<bool>(false);
            row.CollectedAt = now;
            row.ClearLease();
            return new ValueTask<bool>(true);
        }
    }

    /// <inheritdoc />
    public override ValueTask<bool> HastenAsync(string peer, string taskId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(taskId);
        var now = _time.GetUtcNow();
        var found = false;
        lock (_gate)
        {
            foreach (var row in _rows.Values)
            {
                if (row.Status != ClientOutboxStatus.Tracking || row.Peer != peer ||
                    row.RemoteTaskId != taskId) continue;
                row.DueAt = now;
                found = true;
            }
        }

        return new ValueTask<bool>(found);
    }

    /// <inheritdoc />
    public override ValueTask<int> PurgeCollectedAsync(TimeSpan olderThan, int max,
        CancellationToken cancellationToken = default)
    {
        if (max <= 0) return new ValueTask<int>(0);
        var cutoff = _time.GetUtcNow() - NonNegative(olderThan);
        lock (_gate)
        {
            var old = _rows.Values.Where(r => r.CollectedAt is { } at && at <= cutoff)
                .OrderBy(r => r.CollectedAt).ThenBy(r => r.Id, StringComparer.Ordinal).Take(max).ToList();
            foreach (var row in old) _rows.Remove(row.Id);
            return new ValueTask<int>(old.Count);
        }
    }

    private static TimeSpan NonNegative(TimeSpan span)
    {
        return span < TimeSpan.Zero ? TimeSpan.Zero : span;
    }

    /// <summary>The row that <paramref name="leased" /> still holds the lease of.</summary>
    private bool TryLeased(ClientOutboxEntry leased, out Row row)
    {
        ArgumentNullException.ThrowIfNull(leased);
        row = null!;
        return leased.LeaseToken is { } token && _rows.TryGetValue(leased.Id, out var found) &&
               found.LeaseToken == token && (row = found) is not null;
    }

    private sealed class Row
    {
        public required string Id { get; init; }
        public required string Peer { get; init; }
        public string? Correlation { get; init; }
        public required byte[] Request { get; init; }
        public ClientOutboxStatus Status { get; set; } = ClientOutboxStatus.Queued;
        public int Attempts { get; set; }
        public bool InDoubt { get; set; }
        public string? RemoteTaskId { get; set; }
        public string? RemoteContextId { get; set; }
        public TaskState? RemoteState { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset DueAt { get; set; }
        public DateTimeOffset? CollectedAt { get; set; }
        public byte[]? Result { get; set; }
        public DateTimeOffset? LeaseUntil { get; private set; }
        public Guid? LeaseToken { get; private set; }

        public bool LeaseFree(DateTimeOffset now)
        {
            return LeaseUntil is not { } until || until <= now;
        }

        public void TakeLease(DateTimeOffset now, TimeSpan lease)
        {
            LeaseUntil = now + (lease < TimeSpan.Zero ? TimeSpan.Zero : lease);
            LeaseToken = Guid.NewGuid();
        }

        public void ClearLease()
        {
            LeaseUntil = null;
            LeaseToken = null;
        }

        public ClientOutboxEntry ToEntry(DateTimeOffset now, bool withToken, bool? inDoubt = null)
        {
            return new ClientOutboxEntry(Id, Peer, Request.ToArray(), Status)
            {
                Correlation = Correlation,
                Attempts = Attempts,
                InDoubt = inDoubt ?? InDoubt,
                RemoteTaskId = RemoteTaskId,
                RemoteContextId = RemoteContextId,
                RemoteState = RemoteState,
                LastError = LastError,
                CreatedAt = CreatedAt,
                DueAt = DueAt,
                CollectedAt = CollectedAt,
                Age = now - CreatedAt < TimeSpan.Zero ? TimeSpan.Zero : now - CreatedAt,
                LeaseToken = withToken ? LeaseToken : null,
                ResultPayload = Result is null ? null : new ReadOnlyMemory<byte>(Result.ToArray())
            };
        }
    }
}