using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;

namespace A2A.Client.Grpc;

/// <summary>
///     The gRPC channels of the transport factories: one per address and channel options, shared by every client of that
///     agent. A call holds a lease on its channel while it runs, and a channel no call holds is disposed once it has been
///     idle for <see cref="IdleTimeout" />, or when more than <see cref="MaxIdleChannels" /> are idle (the least recently
///     used first). Calls go through <see cref="CallInvoker" />, which leases the channel of its address on each call, so a
///     client whose channel was disposed gets a new one on its next call. A host that calls many agents (for example from a
///     registry) keeps at most the channels its running calls use plus <see cref="MaxIdleChannels" />.
/// </summary>
internal sealed class GrpcChannelPool
{
    /// <summary>Idle channels kept at most; past it the least recently used idle one is disposed.</summary>
    public const int MaxIdleChannels = 64;

    /// <summary>How long a channel no call holds is kept.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(5);

    private static readonly GrpcChannelPool DefaultPool = new(null);

    // A pool per channel options, alive as long as the options (a caller that makes options per client keeps them collectable).
    private static readonly ConditionalWeakTable<GrpcChannelOptions, GrpcChannelPool> PoolsByOptions = new();

    private readonly Func<Uri, GrpcChannel> _create;
    private readonly Action<GrpcChannel> _dispose;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly int _maxIdle;
    private readonly TimeSpan _idleTimeout;
    private readonly TimeProvider _time;

    private GrpcChannelPool(GrpcChannelOptions? options)
        : this(address => GrpcChannel.ForAddress(address, options ?? new GrpcChannelOptions()), c => c.Dispose(),
            MaxIdleChannels, IdleTimeout, TimeProvider.System)
    {
    }

    /// <summary>For tests: the channel factory and disposal, the limits and the clock.</summary>
    internal GrpcChannelPool(Func<Uri, GrpcChannel> create, Action<GrpcChannel> dispose, int maxIdle,
        TimeSpan idleTimeout, TimeProvider time)
    {
        _create = create;
        _dispose = dispose;
        _maxIdle = maxIdle;
        _idleTimeout = idleTimeout;
        _time = time;
    }

    /// <summary>The pool of <paramref name="options" /> (null: the default options).</summary>
    public static GrpcChannelPool For(GrpcChannelOptions? options)
    {
        return options is null ? DefaultPool : PoolsByOptions.GetValue(options, static o => new GrpcChannelPool(o));
    }

    /// <summary>The channels held now, in use or idle.</summary>
    internal int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>A call invoker for <paramref name="address" />: each call leases the address's channel while it runs.</summary>
    public CallInvoker CallInvoker(Uri address)
    {
        return new PooledCallInvoker(this, address);
    }

    /// <summary>
    ///     A lease on the channel of <paramref name="address" />, created when there is none. Dispose the lease when the call
    ///     ends: the channel then counts as idle again.
    /// </summary>
    internal Lease Acquire(Uri address)
    {
        List<GrpcChannel>? evicted;
        Lease lease;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var key = address.AbsoluteUri;
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry(key, _create(address));
                _entries[key] = entry;
            }

            entry.Leases++;
            entry.LastUsed = now;
            lease = new Lease(this, entry);
            evicted = EvictLocked(now);
        }

        DisposeAll(evicted);
        return lease;
    }

    private void Release(Entry entry)
    {
        List<GrpcChannel>? evicted;
        lock (_gate)
        {
            entry.Leases--;
            var now = _time.GetUtcNow();
            entry.LastUsed = now;
            evicted = EvictLocked(now);
        }

        DisposeAll(evicted);
    }

    /// <summary>
    ///     Removes the channels no call holds that are idle past the timeout, then the least recently used idle ones past
    ///     the idle limit. A channel a call holds is never removed. Disposal happens outside the lock.
    /// </summary>
    private List<GrpcChannel>? EvictLocked(DateTimeOffset now)
    {
        List<GrpcChannel>? evicted = null;
        var idle = new List<Entry>();
        foreach (var entry in _entries.Values)
            if (entry.Leases == 0)
                idle.Add(entry);
        idle.Sort((a, b) => a.LastUsed.CompareTo(b.LastUsed));
        var excess = idle.Count - _maxIdle;
        foreach (var entry in idle)
        {
            if (excess <= 0 && now - entry.LastUsed < _idleTimeout) break;
            _entries.Remove(entry.Key);
            (evicted ??= []).Add(entry.Channel);
            excess--;
        }

        return evicted;
    }

    private void DisposeAll(List<GrpcChannel>? channels)
    {
        if (channels is null) return;
        foreach (var channel in channels) _dispose(channel);
    }

    internal sealed class Entry(string key, GrpcChannel channel)
    {
        public string Key { get; } = key;
        public GrpcChannel Channel { get; } = channel;
        public int Leases { get; set; }
        public DateTimeOffset LastUsed { get; set; }
    }

    /// <summary>A call's hold on a channel. Disposing it more than once releases it once.</summary>
    internal sealed class Lease : IDisposable
    {
        private readonly Entry _entry;
        private readonly GrpcChannelPool _pool;
        private int _released;

        internal Lease(GrpcChannelPool pool, Entry entry)
        {
            _pool = pool;
            _entry = entry;
        }

        public GrpcChannel Channel => _entry.Channel;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) _pool.Release(_entry);
        }
    }

    /// <summary>
    ///     Leases the channel for each call. A unary call releases it when the response arrives or the call is disposed; a
    ///     streaming call when the call is disposed (the transports dispose every call they start).
    /// </summary>
    private sealed class PooledCallInvoker(GrpcChannelPool pool, Uri address) : CallInvoker
    {
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host,
            CallOptions options, TRequest request)
        {
            using var lease = pool.Acquire(address);
            return lease.Channel.CreateCallInvoker().BlockingUnaryCall(method, host, options, request);
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request)
        {
            var lease = pool.Acquire(address);
            AsyncUnaryCall<TResponse> call;
            try
            {
                call = lease.Channel.CreateCallInvoker().AsyncUnaryCall(method, host, options, request);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            var response = ReleaseWhenDone(call.ResponseAsync, lease);
            return new AsyncUnaryCall<TResponse>(response, call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers,
                () =>
                {
                    call.Dispose();
                    lease.Dispose();
                });
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            var lease = pool.Acquire(address);
            AsyncServerStreamingCall<TResponse> call;
            try
            {
                call = lease.Channel.CreateCallInvoker().AsyncServerStreamingCall(method, host, options, request);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            return new AsyncServerStreamingCall<TResponse>(call.ResponseStream, call.ResponseHeadersAsync, call.GetStatus,
                call.GetTrailers, () =>
                {
                    call.Dispose();
                    lease.Dispose();
                });
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            var lease = pool.Acquire(address);
            AsyncClientStreamingCall<TRequest, TResponse> call;
            try
            {
                call = lease.Channel.CreateCallInvoker().AsyncClientStreamingCall(method, host, options);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            return new AsyncClientStreamingCall<TRequest, TResponse>(call.RequestStream,
                ReleaseWhenDone(call.ResponseAsync, lease), call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers,
                () =>
                {
                    call.Dispose();
                    lease.Dispose();
                });
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            var lease = pool.Acquire(address);
            AsyncDuplexStreamingCall<TRequest, TResponse> call;
            try
            {
                call = lease.Channel.CreateCallInvoker().AsyncDuplexStreamingCall(method, host, options);
            }
            catch
            {
                lease.Dispose();
                throw;
            }

            return new AsyncDuplexStreamingCall<TRequest, TResponse>(call.RequestStream, call.ResponseStream,
                call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, () =>
                {
                    call.Dispose();
                    lease.Dispose();
                });
        }

        private static async Task<T> ReleaseWhenDone<T>(Task<T> response, Lease lease)
        {
            try
            {
                return await response.ConfigureAwait(false);
            }
            finally
            {
                lease.Dispose();
            }
        }
    }
}
