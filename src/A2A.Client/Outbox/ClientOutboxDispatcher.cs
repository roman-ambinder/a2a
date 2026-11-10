using System.Text.Json;
using A2A.Client.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Client.Outbox;

/// <summary>
///     Moves the entries of a <see cref="ClientOutboxStore" /> along: sends queued messages, polls the tasks of sent ones,
///     and stores final
///     results. Create one per process and keep it for the life of the app; it runs its own timer. Several dispatchers
///     (several instances
///     of your app) can share one durable store.
/// </summary>
/// <remarks>
///     <para>
///         Every send carries <c>Idempotency-Key</c> = the entry id, so a resend after a crash does not create a second
///         task on an SDK server.
///         Delivery to the peer is at least once. A peer that ignores the key may see a duplicate only when an earlier
///         attempt's outcome was
///         unknown (<see cref="ClientOutboxEntry.InDoubt" />); <see cref="ClientOutboxOptions.RetryInDoubt" /> = false
///         turns that into at most once.
///     </para>
///     <para>
///         Work that is cancelled (the token you gave <see cref="RunOnceAsync" />, or <see cref="DisposeAsync" />) writes
///         nothing, the same as a crash:
///         the lease runs out and another sweep takes the entry. A store failure while writing is logged and treated the
///         same way.
///     </para>
/// </remarks>
public sealed class ClientOutboxDispatcher : IAsyncDisposable
{
    private const int MaxErrorLength = 500;
    private const int PurgeBatch = 1000;
    private readonly Func<string, CancellationToken, ValueTask<IA2AClient>> _clients;
    private readonly object _gate = new();
    private readonly ILogger _logger;
    private readonly ClientOutboxOptions _options;
    private readonly CancellationTokenSource _stop = new();

    private readonly ClientOutboxStore _store;
    private readonly TimeProvider _time;
    private readonly ITimer? _timer;
    private bool _disposed;
    private TaskCompletionSource<bool>? _idle;
    private int _inFlight;
    private DateTimeOffset _lastPurge;
    private int _sweeping;

    /// <summary>
    ///     Creates a dispatcher and, unless <see cref="ClientOutboxOptions.SweepInterval" /> is infinite, starts its
    ///     timer.
    /// </summary>
    /// <param name="store">Where the entries are kept.</param>
    /// <param name="clients">
    ///     Turns a peer name (<see cref="ClientOutboxEntry.Peer" />) into a client. The client brings the credentials, so none
    ///     are stored.
    ///     An exception it throws is a retry that is not in doubt.
    /// </param>
    /// <param name="options">Settings, checked here. Default <see cref="ClientOutboxOptions" />.</param>
    /// <param name="time">The clock for sweeps, backoff and call timeouts. Default <see cref="TimeProvider.System" />.</param>
    /// <param name="logger">Where problems are logged. Default none.</param>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public ClientOutboxDispatcher(ClientOutboxStore store,
        Func<string, CancellationToken, ValueTask<IA2AClient>> clients, ClientOutboxOptions? options = null,
        TimeProvider? time = null, ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options ?? new ClientOutboxOptions();
        _options.Validate();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _lastPurge = _time.GetUtcNow();
        if (_options.SweepInterval != Timeout.InfiniteTimeSpan)
            _timer = _time.CreateTimer(_ => _ = SweepFromTimerAsync(), null, _options.SweepInterval,
                _options.SweepInterval);
    }

    /// <summary>Stops the timer, cancels the work in flight and waits for it. The cancelled work writes nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? idle = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_inFlight > 0)
                idle = (_idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
                    .Task;
        }

        _timer?.Dispose();
        _stop.Cancel();
        if (idle is not null) await idle.ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>
    ///     One sweep: purges old collected entries when it is time, leases due entries and works on them (at most
    ///     <see cref="ClientOutboxOptions.MaxConcurrency" /> at a time). The timer calls it; tests and apps with the timer off
    ///     call it directly.
    ///     Returns how many entries it leased. Throws <see cref="OperationCanceledException" /> when
    ///     <paramref name="cancellationToken" /> is cancelled.
    /// </summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _inFlight++;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, cancellationToken);
            return await SweepAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (--_inFlight == 0) _idle?.TrySetResult(true);
            }
        }
    }

    /// <summary>
    ///     Tells the dispatcher what a webhook said (hand it the <see cref="PushDelivery" /> of
    ///     <see cref="PushNotificationReceiver.HandleAsync" />).
    ///     A final state makes the entry of that peer's task due now, so it is polled soon. The webhook itself is never
    ///     trusted:
    ///     <c>GetTask</c> is the truth. Returns true when a tracked entry was hurried.
    /// </summary>
    public async Task<bool> NotifyAsync(string peer, PushDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(peer)) throw new ArgumentException("The peer is required.", nameof(peer));
        ArgumentNullException.ThrowIfNull(delivery);
        var (taskId, state) = PushEvents.Inspect(delivery.Event);
        if (taskId is null || state is not { } s || !PushEvents.IsFinal(s)) return false;
        return await _store.HastenAsync(peer, taskId, cancellationToken).ConfigureAwait(false);
    }

    private async Task SweepFromTimerAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try
        {
            await RunOnceAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            /* disposed */
        }
        catch (ObjectDisposedException)
        {
            /* disposed */
        }
        catch (Exception e)
        {
            ClientLog.OutboxSweepFailed(_logger, e);
        }
        finally
        {
            Volatile.Write(ref _sweeping, 0);
        }
    }

    private async Task<int> SweepAsync(CancellationToken token)
    {
        await PurgeIfDueAsync(_time.GetUtcNow(), token).ConfigureAwait(false);

        // Lease and run in groups of at most MaxConcurrency, so an entry never waits for a slot while its lease runs.
        var total = 0;
        while (total < _options.BatchSize && !token.IsCancellationRequested)
        {
            var want = Math.Min(_options.BatchSize - total, _options.MaxConcurrency);
            var leased = await _store.LeaseDueAsync(want, _options.Lease, token).ConfigureAwait(false);
            if (leased.Count == 0) break;
            var leasedAt = _time.GetUtcNow();
            await Task.WhenAll(leased.Select(entry => RunEntryAsync(entry, leasedAt, token))).ConfigureAwait(false);
            total += leased.Count;
            if (leased.Count < want) break;
        }

        return total;
    }

    private async Task PurgeIfDueAsync(DateTimeOffset now, CancellationToken token)
    {
        if (_options.PurgeInterval == Timeout.InfiniteTimeSpan || now - _lastPurge < _options.PurgeInterval) return;
        _lastPurge = now;
        try
        {
            int purged;
            do
            {
                purged = await _store.PurgeCollectedAsync(_options.CollectedRetention, PurgeBatch, token)
                    .ConfigureAwait(false);
            } while (purged >= PurgeBatch && !token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            ClientLog.OutboxSweepFailed(_logger, e);
        }
    }

    private async Task RunEntryAsync(ClientOutboxEntry entry, DateTimeOffset leasedAt, CancellationToken token)
    {
        try
        {
            if (entry.Status == ClientOutboxStatus.Queued)
                await SendAsync(entry, leasedAt, token).ConfigureAwait(false);
            else await PollAsync(entry, leasedAt, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            /* nothing is written, like a crash */
        }
        catch (Exception e)
        {
            ClientLog.OutboxStoreFailed(_logger, entry.Id, entry.Peer, e);
        }
    }

    private async Task SendAsync(ClientOutboxEntry entry, DateTimeOffset leasedAt, CancellationToken token)
    {
        if (TooOld(entry, leasedAt, _options.MaxQueueAge))
        {
            await FailAsync(entry, $"The message was not sent within MaxQueueAge ({_options.MaxQueueAge}).",
                entry.InDoubt, token).ConfigureAwait(false);
            return;
        }

        if (entry.InDoubt && !_options.RetryInDoubt)
        {
            await FailAsync(entry, "An earlier send may have reached the peer, and RetryInDoubt is off.", true, token)
                .ConfigureAwait(false);
            return;
        }

        SendMessageRequest request;
        try
        {
            request = entry.GetRequest();
        }
        catch (JsonException e)
        {
            await FailAsync(entry, "The stored request cannot be read: " + Shorten(e.Message), entry.InDoubt, token)
                .ConfigureAwait(false);
            return;
        }

        SendMessageResponse? response = null;
        Exception? error = null;
        var resolverFailed = false;
        using (var call = new CallScope(_options.CallTimeout, _time, token))
        {
            try
            {
                IA2AClient client;
                try
                {
                    client = await _clients(entry.Peer, call.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    resolverFailed = true;
                    throw new InvalidOperationException($"No client for peer '{entry.Peer}': {e.Message}", e);
                }

                var options = new A2ARequestOptions
                    { IdempotencyKey = entry.Id, Timeout = _options.CallTimeout, MaxRetries = 0 };
                response = await client.SendMessageAsync(request, options, call.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                error = e;
            }
        }

        if (token.IsCancellationRequested) return; // the process is stopping: write nothing

        if (response is not null)
        {
            await StoreAnswerAsync(entry, response, token).ConfigureAwait(false);
            return;
        }

        TimeSpan? retryAfter = null;
        var kind = resolverFailed
            ? ClientOutboxFailureKind.Retry
            : ClientOutboxFailures.Classify(error!, out retryAfter);
        var text = Describe(error!);
        // An earlier attempt may still be in doubt, so a clean failure of this attempt does not clear it.
        var inDoubt = entry.InDoubt || kind == ClientOutboxFailureKind.RetryInDoubt;
        if (kind == ClientOutboxFailureKind.Fail)
        {
            await FailAsync(entry, text, entry.InDoubt, token).ConfigureAwait(false);
            return;
        }

        if (TooOld(entry, leasedAt, _options.MaxQueueAge, true))
        {
            await FailAsync(entry, text, inDoubt, token).ConfigureAwait(false);
            return;
        }

        await WriteAsync(
                () => _store.RescheduleAsync(entry, RetryDelay(entry.Attempts, retryAfter), text, inDoubt, token),
                token)
            .ConfigureAwait(false);
    }

    private async Task StoreAnswerAsync(ClientOutboxEntry entry, SendMessageResponse response, CancellationToken token)
    {
        if (response is TaskResponse { Task: var task } && !PushEvents.IsFinal(task.Status.State))
        {
            await WriteAsync(
                () => _store.MarkTrackingAsync(entry, task.Id, task.ContextId, task.Status.State, _options.PollInterval,
                    token), token).ConfigureAwait(false);
            return;
        }

        await WriteAsync(() => _store.FinishAsync(entry, ClientOutboxOutcome.Finished(response), token), token)
            .ConfigureAwait(false);
    }

    private async Task PollAsync(ClientOutboxEntry entry, DateTimeOffset leasedAt, CancellationToken token)
    {
        if (entry.RemoteTaskId is not { Length: > 0 } taskId)
        {
            await FailAsync(entry, "The entry is tracked but has no task id.", false, token).ConfigureAwait(false);
            return;
        }

        if (TooOld(entry, leasedAt, _options.MaxTrackingAge))
        {
            await FailAsync(entry, $"The task did not finish within MaxTrackingAge ({_options.MaxTrackingAge}).", false,
                token).ConfigureAwait(false);
            return;
        }

        // Only the tenant is needed: read that one member, not the whole stored request, on every poll.
        var tenant = TenantOf(entry.Request.Span); // null when missing or unreadable: the id is enough for most peers

        AgentTask? task = null;
        Exception? error = null;
        using (var call = new CallScope(_options.CallTimeout, _time, token))
        {
            try
            {
                var client = await _clients(entry.Peer, call.Token).ConfigureAwait(false);
                // HistoryLength stays unset: the stored result may need the history.
                var options = new A2ARequestOptions { Timeout = _options.CallTimeout, MaxRetries = 0 };
                task = await client
                    .GetTaskAsync(new GetTaskRequest { Tenant = tenant, Id = taskId }, options, call.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                error = e;
            }
        }

        if (token.IsCancellationRequested) return;

        if (task is not null)
        {
            if (PushEvents.IsFinal(task.Status.State))
                await WriteAsync(
                        () => _store.FinishAsync(entry, ClientOutboxOutcome.Finished(new TaskResponse(task)), token),
                        token)
                    .ConfigureAwait(false);
            else
                await WriteAsync(() => _store.RescheduleAsync(entry, PollDelay(entry.Attempts), null, false, token),
                    token).ConfigureAwait(false);
            return;
        }

        if (error is A2AException { Code: A2AErrorCode.TaskNotFound })
        {
            await FailAsync(entry, "The peer does not know the task.", false, token).ConfigureAwait(false);
            return;
        }

        if (TooOld(entry, leasedAt, _options.MaxTrackingAge, true))
        {
            await FailAsync(entry,
                $"The task did not finish within MaxTrackingAge ({_options.MaxTrackingAge}). Last error: {Describe(error!)}",
                false, token).ConfigureAwait(false);
            return;
        }

        var retryAfter = (error as A2AException)?.RetryAfter;
        await WriteAsync(
            () => _store.RescheduleAsync(entry, RetryDelay(entry.Attempts, retryAfter), Describe(error!), false, token),
            token).ConfigureAwait(false);
    }

    /// <summary>Ends the entry as failed. The remote ids it already has are kept.</summary>
    private async Task FailAsync(ClientOutboxEntry entry, string error, bool inDoubt, CancellationToken token)
    {
        var outcome = ClientOutboxOutcome.Failed(error, entry.RemoteTaskId, entry.RemoteContextId, entry.RemoteState,
            inDoubt);
        if (await WriteAsync(() => _store.FinishAsync(entry, outcome, token), token).ConfigureAwait(false))
            ClientLog.OutboxEntryFailed(_logger, entry.Id, entry.Peer, error);
    }

    /// <summary>Runs a fenced store write unless the dispatcher was cancelled. Returns whether the write applied.</summary>
    private static async Task<bool> WriteAsync(Func<ValueTask<bool>> write, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        return await write().ConfigureAwait(false);
    }

    /// <summary>
    ///     True when the entry is older than <paramref name="limit" />. The age of a lease is as of the lease;
    ///     <paramref name="afterWork" /> adds the time the work took.
    /// </summary>
    private bool TooOld(ClientOutboxEntry entry, DateTimeOffset leasedAt, TimeSpan limit, bool afterWork = false)
    {
        if (limit == Timeout.InfiniteTimeSpan) return false;
        var age = afterWork ? entry.Age + (_time.GetUtcNow() - leasedAt) : entry.Age;
        return age > limit;
    }

    /// <summary>
    ///     The wait before the next send: the peer's <c>Retry-After</c> (at most
    ///     <see cref="ClientOutboxOptions.RetryMaxDelay" />) when it gave one, else exponential with jitter.
    /// </summary>
    private TimeSpan RetryDelay(int attempts, TimeSpan? retryAfter)
    {
        if (retryAfter is { } fromPeer)
            return fromPeer < TimeSpan.Zero ? TimeSpan.Zero :
                fromPeer > _options.RetryMaxDelay ? _options.RetryMaxDelay : fromPeer;
        var backoff = Math.Min(_options.RetryBaseDelay.Ticks * Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 30)),
            _options.RetryMaxDelay.Ticks);
        var jitter = 0.8 + Random.Shared.NextDouble() * 0.4;
        return TimeSpan.FromTicks((long)Math.Min(backoff * jitter, _options.RetryMaxDelay.Ticks));
    }

    /// <summary>
    ///     The wait before the next check of a task: <see cref="ClientOutboxOptions.PollInterval" /> doubled with every
    ///     check, up to <see cref="ClientOutboxOptions.MaxPollInterval" />.
    /// </summary>
    private TimeSpan PollDelay(int attempts)
    {
        return TimeSpan.FromTicks((long)Math.Min(
            _options.PollInterval.Ticks * Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 30)),
            _options.MaxPollInterval.Ticks));
    }

    private static string Describe(Exception e)
    {
        return Shorten($"{e.GetType().Name}: {e.Message}");
    }

    private static string Shorten(string text)
    {
        return text.Length <= MaxErrorLength ? text : text.Substring(0, MaxErrorLength);
    }

    /// <summary>The top-level <c>tenant</c> of a stored SendMessage request, or null when it is missing, null or unreadable.</summary>
    internal static string? TenantOf(ReadOnlySpan<byte> request)
    {
        try
        {
            var reader = new Utf8JsonReader(request);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return null;
            string? tenant = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isTenant = reader.ValueTextEquals("tenant"u8);
                reader.Read();
                if (isTenant) tenant = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                else reader.Skip();
            }

            return tenant;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A token that ends when the dispatcher is cancelled or the call timeout passes on the dispatcher's clock.</summary>
    private sealed class CallScope : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly ITimer _timer;

        public CallScope(TimeSpan timeout, TimeProvider time, CancellationToken outer)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
            _timer = time.CreateTimer(static state =>
            {
                try
                {
                    ((CancellationTokenSource)state!).Cancel();
                }
                catch (ObjectDisposedException)
                {
                    /* the call already ended */
                }
            }, _cts, timeout, Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _cts.Token;

        public void Dispose()
        {
            _timer.Dispose();
            _cts.Dispose();
        }
    }
}