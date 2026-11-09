using System.Text.Json;
using A2A.Client.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Client.Outbox;

/// <summary>
/// Moves the entries of a <see cref="ClientOutboxStore"/> along: sends queued messages, polls the tasks of sent ones, and stores final
/// results. Create one per process and keep it for the life of the app; it runs its own timer. Several dispatchers (several instances
/// of your app) can share one durable store.
/// </summary>
/// <remarks>
/// <para>
/// Every send carries <c>Idempotency-Key</c> = the entry id, so a resend after a crash does not create a second task on an SDK server.
/// Delivery to the peer is at least once. A peer that ignores the key may see a duplicate only when an earlier attempt's outcome was
/// unknown (<see cref="ClientOutboxEntry.InDoubt"/>); <see cref="ClientOutboxOptions.RetryInDoubt"/> = false turns that into at most once.
/// </para>
/// <para>
/// Work that is cancelled (the token you gave <see cref="RunOnceAsync"/>, or <see cref="DisposeAsync"/>) writes nothing, the same as a crash:
/// the lease runs out and another sweep takes the entry. A store failure while writing is logged and treated the same way.
/// </para>
/// </remarks>
public sealed class ClientOutboxDispatcher : IAsyncDisposable
{
    private const int MaxErrorLength = 500;

    private readonly ClientOutboxStore _store;
    private readonly Func<string, CancellationToken, ValueTask<IA2AClient>> _clients;
    private readonly ClientOutboxOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stop = new();
    private readonly ITimer? _timer;
    private readonly object _gate = new();
    private int _inFlight;
    private TaskCompletionSource<bool>? _idle;
    private bool _disposed;
    private int _sweeping;
    private DateTimeOffset _lastPurge;

    /// <summary>Creates a dispatcher and, unless <see cref="ClientOutboxOptions.SweepInterval"/> is infinite, starts its timer.</summary>
    /// <param name="store">Where the entries are kept.</param>
    /// <param name="clients">
    /// Turns a peer name (<see cref="ClientOutboxEntry.Peer"/>) into a client. The client brings the credentials, so none are stored.
    /// An exception it throws is a retry that is not in doubt.
    /// </param>
    /// <param name="options">Settings, checked here. Default <see cref="ClientOutboxOptions"/>.</param>
    /// <param name="time">The clock for sweeps, backoff and call timeouts. Default <see cref="TimeProvider.System"/>.</param>
    /// <param name="logger">Where problems are logged. Default none.</param>
    /// <exception cref="ArgumentException">An option is out of range.</exception>
    public ClientOutboxDispatcher(ClientOutboxStore store, Func<string, CancellationToken, ValueTask<IA2AClient>> clients, ClientOutboxOptions? options = null, TimeProvider? time = null, ILogger? logger = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clients = clients ?? throw new ArgumentNullException(nameof(clients));
        _options = options ?? new ClientOutboxOptions();
        _options.Validate();
        _time = time ?? TimeProvider.System;
        _logger = logger ?? NullLogger.Instance;
        _lastPurge = _time.GetUtcNow();
        if (_options.SweepInterval != Timeout.InfiniteTimeSpan)
            _timer = _time.CreateTimer(_ => _ = SweepFromTimerAsync(), null, _options.SweepInterval, _options.SweepInterval);
    }

    /// <summary>
    /// One sweep: purges old collected entries when it is time, leases due entries and works on them (at most
    /// <see cref="ClientOutboxOptions.MaxConcurrency"/> at a time). The timer calls it; tests and apps with the timer off call it directly.
    /// Returns how many entries it leased. Throws <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> is cancelled.
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
    /// Tells the dispatcher what a webhook said (hand it the <see cref="PushDelivery"/> of <see cref="PushNotificationReceiver.HandleAsync"/>).
    /// A final state makes the entry of that peer's task due now, so it is polled soon. The webhook itself is never trusted:
    /// <c>GetTask</c> is the truth. Returns true when a tracked entry was hurried.
    /// </summary>
    public async Task<bool> NotifyAsync(string peer, PushDelivery delivery, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(peer)) throw new ArgumentException("The peer is required.", nameof(peer));
        ArgumentNullException.ThrowIfNull(delivery);
        var (taskId, state) = PushEvents.Inspect(delivery.Event);
        if (taskId is null || state is not { } s || !PushEvents.IsFinal(s)) return false;
        return await _store.HastenAsync(peer, taskId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the timer, cancels the work in flight and waits for it. The cancelled work writes nothing.</summary>
    public async ValueTask DisposeAsync()
    {
        Task? idle = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_inFlight > 0) idle = (_idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        _timer?.Dispose();
        _stop.Cancel();
        if (idle is not null) await idle.ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task SweepFromTimerAsync()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) == 1) return;
        try { await RunOnceAsync().ConfigureAwait(false); }
        catch (OperationCanceledException) { /* disposed */ }
        catch (ObjectDisposedException) { /* disposed */ }
        catch (Exception e) { ClientLog.OutboxSweepFailed(_logger, e); }
        finally { Volatile.Write(ref _sweeping, 0); }
    }

    private async Task<int> SweepAsync(CancellationToken token)
    {
        var sweptAt = _time.GetUtcNow();
        await PurgeIfDueAsync(sweptAt, token).ConfigureAwait(false);
        var leased = await _store.LeaseDueAsync(_options.BatchSize, _options.Lease, token).ConfigureAwait(false);
        if (leased.Count == 0) return 0;

        using var slots = new SemaphoreSlim(_options.MaxConcurrency);
        var work = new List<Task>(leased.Count);
        foreach (var entry in leased) work.Add(RunEntryAsync(entry, sweptAt, slots, token));
        await Task.WhenAll(work).ConfigureAwait(false);
        return leased.Count;
    }

    private async Task PurgeIfDueAsync(DateTimeOffset now, CancellationToken token)
    {
        if (_options.PurgeInterval == Timeout.InfiniteTimeSpan || now - _lastPurge < _options.PurgeInterval) return;
        _lastPurge = now;
        try { await _store.PurgeCollectedAsync(_options.CollectedRetention, _options.BatchSize, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception e) { ClientLog.OutboxSweepFailed(_logger, e); }
    }

    private async Task RunEntryAsync(ClientOutboxEntry entry, DateTimeOffset leasedAt, SemaphoreSlim slots, CancellationToken token)
    {
        try
        {
            await slots.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (entry.Status == ClientOutboxStatus.Queued) await SendAsync(entry, leasedAt, token).ConfigureAwait(false);
                else await PollAsync(entry, leasedAt, token).ConfigureAwait(false);
            }
            finally { slots.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* nothing is written, like a crash */ }
        catch (Exception e) { ClientLog.OutboxStoreFailed(_logger, entry.Id, entry.Peer, e); }
    }

    private async Task SendAsync(ClientOutboxEntry entry, DateTimeOffset leasedAt, CancellationToken token)
    {
        if (TooOld(entry, leasedAt, _options.MaxQueueAge))
        {
            await FailAsync(entry, $"The message was not sent within MaxQueueAge ({_options.MaxQueueAge}).", token).ConfigureAwait(false);
            return;
        }
        if (entry.InDoubt && !_options.RetryInDoubt)
        {
            await FailAsync(entry, "An earlier send may have reached the peer, and RetryInDoubt is off.", token).ConfigureAwait(false);
            return;
        }

        SendMessageRequest request;
        try { request = entry.GetRequest(); }
        catch (JsonException e)
        {
            await FailAsync(entry, "The stored request cannot be read: " + Shorten(e.Message), token).ConfigureAwait(false);
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
                try { client = await _clients(entry.Peer, call.Token).ConfigureAwait(false); }
                catch (Exception e)
                {
                    resolverFailed = true;
                    throw new InvalidOperationException($"No client for peer '{entry.Peer}': {e.Message}", e);
                }
                var options = new A2ARequestOptions { IdempotencyKey = entry.Id, Timeout = _options.CallTimeout, MaxRetries = 0 };
                response = await client.SendMessageAsync(request, options, call.Token).ConfigureAwait(false);
            }
            catch (Exception e) { error = e; }
        }
        if (token.IsCancellationRequested) return; // the process is stopping: write nothing

        if (response is not null)
        {
            await StoreAnswerAsync(entry, response, token).ConfigureAwait(false);
            return;
        }

        TimeSpan? retryAfter = null;
        var kind = resolverFailed ? ClientOutboxFailureKind.Retry : ClientOutboxFailures.Classify(error!, out retryAfter);
        var text = Describe(error!);
        if (kind == ClientOutboxFailureKind.Fail || TooOld(entry, leasedAt, _options.MaxQueueAge, afterWork: true))
        {
            await FailAsync(entry, text, token).ConfigureAwait(false);
            return;
        }
        // An earlier attempt may still be in doubt, so a clean failure of this attempt does not clear it.
        var inDoubt = entry.InDoubt || kind == ClientOutboxFailureKind.RetryInDoubt;
        await WriteAsync(() => _store.RescheduleAsync(entry, RetryDelay(entry.Attempts, retryAfter), text, inDoubt, token), token).ConfigureAwait(false);
    }

    private async Task StoreAnswerAsync(ClientOutboxEntry entry, SendMessageResponse response, CancellationToken token)
    {
        if (response is TaskResponse { Task: var task } && !PushEvents.IsFinal(task.Status.State))
        {
            await WriteAsync(() => _store.MarkTrackingAsync(entry, task.Id, task.ContextId, task.Status.State, _options.PollInterval, token), token).ConfigureAwait(false);
            return;
        }
        await WriteAsync(() => _store.FinishAsync(entry, ClientOutboxOutcome.Finished(response), token), token).ConfigureAwait(false);
    }

    private async Task PollAsync(ClientOutboxEntry entry, DateTimeOffset leasedAt, CancellationToken token)
    {
        if (entry.RemoteTaskId is not { Length: > 0 } taskId)
        {
            await FailAsync(entry, "The entry is tracked but has no task id.", token).ConfigureAwait(false);
            return;
        }
        if (TooOld(entry, leasedAt, _options.MaxTrackingAge))
        {
            await FailAsync(entry, $"The task did not finish within MaxTrackingAge ({_options.MaxTrackingAge}).", token).ConfigureAwait(false);
            return;
        }

        string? tenant = null;
        try { tenant = entry.GetRequest().Tenant; }
        catch (JsonException) { /* the id is enough for most peers */ }

        AgentTask? task = null;
        Exception? error = null;
        using (var call = new CallScope(_options.CallTimeout, _time, token))
        {
            try
            {
                var client = await _clients(entry.Peer, call.Token).ConfigureAwait(false);
                // HistoryLength stays unset: the stored result may need the history.
                var options = new A2ARequestOptions { Timeout = _options.CallTimeout, MaxRetries = 0 };
                task = await client.GetTaskAsync(new GetTaskRequest { Tenant = tenant, Id = taskId }, options, call.Token).ConfigureAwait(false);
            }
            catch (Exception e) { error = e; }
        }
        if (token.IsCancellationRequested) return;

        if (task is not null)
        {
            if (PushEvents.IsFinal(task.Status.State))
                await WriteAsync(() => _store.FinishAsync(entry, ClientOutboxOutcome.Finished(new TaskResponse(task)), token), token).ConfigureAwait(false);
            else
                await WriteAsync(() => _store.RescheduleAsync(entry, PollDelay(entry.Attempts), null, false, token), token).ConfigureAwait(false);
            return;
        }

        if (error is A2AException { Code: A2AErrorCode.TaskNotFound })
        {
            await FailAsync(entry, "The peer does not know the task.", token).ConfigureAwait(false);
            return;
        }
        if (TooOld(entry, leasedAt, _options.MaxTrackingAge, afterWork: true))
        {
            await FailAsync(entry, $"The task did not finish within MaxTrackingAge ({_options.MaxTrackingAge}). Last error: {Describe(error!)}", token).ConfigureAwait(false);
            return;
        }
        var retryAfter = (error as A2AException)?.RetryAfter;
        await WriteAsync(() => _store.RescheduleAsync(entry, RetryDelay(entry.Attempts, retryAfter), Describe(error!), false, token), token).ConfigureAwait(false);
    }

    /// <summary>Ends the entry as failed. The remote ids it already has are kept.</summary>
    private async Task FailAsync(ClientOutboxEntry entry, string error, CancellationToken token)
    {
        var outcome = ClientOutboxOutcome.Failed(error, entry.RemoteTaskId, entry.RemoteContextId, entry.RemoteState);
        if (await WriteAsync(() => _store.FinishAsync(entry, outcome, token), token).ConfigureAwait(false))
            ClientLog.OutboxEntryFailed(_logger, entry.Id, entry.Peer, error);
    }

    /// <summary>Runs a fenced store write unless the dispatcher was cancelled. Returns whether the write applied.</summary>
    private static async Task<bool> WriteAsync(Func<ValueTask<bool>> write, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        return await write().ConfigureAwait(false);
    }

    /// <summary>True when the entry is older than <paramref name="limit"/>. The age of a lease is as of the lease; <paramref name="afterWork"/> adds the time the work took.</summary>
    private bool TooOld(ClientOutboxEntry entry, DateTimeOffset leasedAt, TimeSpan limit, bool afterWork = false)
    {
        if (limit == Timeout.InfiniteTimeSpan) return false;
        var age = afterWork ? entry.Age + (_time.GetUtcNow() - leasedAt) : entry.Age;
        return age > limit;
    }

    /// <summary>The wait before the next send: the peer's <c>Retry-After</c> when it gave one, else exponential with jitter.</summary>
    private TimeSpan RetryDelay(int attempts, TimeSpan? retryAfter)
    {
        if (retryAfter is { } fromPeer) return fromPeer < TimeSpan.Zero ? TimeSpan.Zero : fromPeer;
        var backoff = Math.Min(_options.RetryBaseDelay.Ticks * Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 30)), _options.RetryMaxDelay.Ticks);
        var jitter = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromTicks((long)Math.Min(backoff * jitter, _options.RetryMaxDelay.Ticks));
    }

    /// <summary>The wait before the next check of a task: <see cref="ClientOutboxOptions.PollInterval"/> doubled with every check, up to <see cref="ClientOutboxOptions.MaxPollInterval"/>.</summary>
    private TimeSpan PollDelay(int attempts) =>
        TimeSpan.FromTicks((long)Math.Min(_options.PollInterval.Ticks * Math.Pow(2, Math.Min(Math.Max(attempts - 1, 0), 30)), _options.MaxPollInterval.Ticks));

    private static string Describe(Exception e) => Shorten($"{e.GetType().Name}: {e.Message}");

    private static string Shorten(string text) => text.Length <= MaxErrorLength ? text : text.Substring(0, MaxErrorLength);

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
                try { ((CancellationTokenSource)state!).Cancel(); }
                catch (ObjectDisposedException) { /* the call already ended */ }
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
