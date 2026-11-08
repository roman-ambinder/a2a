using System.Collections.Concurrent;
using System.Threading.Channels;
using A2A.Server.Streaming;

namespace A2A.Server;

/// <summary>
/// Fan-out of applied task events to live subscribers (SubscribeToTask). Every subscriber gets the events in sequence order
/// (spec §3.5.2). Each subscription holds at most <see cref="StreamingOptions.ChannelCapacity"/> events: when a subscriber
/// falls behind, its oldest queued events are dropped, never the newest, and the subscriber fills the gap in sequences from
/// the <see cref="Storage.EventStore"/> (every event is stored before it is published). Publishing never waits.
/// Process-local; replace for multi-instance deployments.
/// </summary>
public class TaskEventBus
{
    private readonly ConcurrentDictionary<string, ImmutableSubscribers> _subs = new(StringComparer.Ordinal);

    /// <summary>Creates a bus whose subscriptions hold up to 256 events each.</summary>
    public TaskEventBus() : this(new StreamingOptions()) { }

    /// <summary>Creates a bus whose subscriptions hold up to <see cref="StreamingOptions.ChannelCapacity"/> events each.</summary>
    /// <param name="options">Streaming settings; <see cref="StreamingOptions.ChannelCapacity"/> is used.</param>
    public TaskEventBus(StreamingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Capacity = Math.Max(1, options.ChannelCapacity);
    }

    /// <summary>Events each subscription holds.</summary>
    internal int Capacity { get; }

    /// <summary>Registers a subscriber for the events of <paramref name="taskId"/>. Dispose the result to unsubscribe.</summary>
    public virtual Subscription Subscribe(string taskId)
    {
        // DropOldest, not DropWrite: the newest event always reaches the subscriber, so it sees the gap before it and fills it
        // from the store. With DropWrite a subscriber that fell behind would miss the latest events (for example INPUT_REQUIRED)
        // until some later event arrived, which may never happen.
        var channel = Channel.CreateBounded<SequencedEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false,
        });
        var sub = new Subscription(this, taskId, channel);
        _subs.AddOrUpdate(taskId, _ => new ImmutableSubscribers([sub]), (_, s) => s.Add(sub));
        return sub;
    }

    /// <summary>Sends a stored event to every current subscriber of the task. A full subscription drops its oldest event.</summary>
    /// <remarks>
    /// Called while the execution engine holds the task's lock, in sequence order. Overrides must not block and must not call back
    /// into <see cref="A2ARequestHandler"/> for the same task (that would deadlock).
    /// </remarks>
    public virtual void Publish(string taskId, SequencedEvent evt)
    {
        if (!_subs.TryGetValue(taskId, out var subs)) return;
        foreach (var s in subs.Items) s.Channel.Writer.TryWrite(evt);
    }

    /// <summary>Ends all subscriptions for a task (after a terminal state).</summary>
    /// <remarks>
    /// Called while the execution engine holds the task's lock. Overrides must not block and must not call back into
    /// <see cref="A2ARequestHandler"/> for the same task (that would deadlock).
    /// </remarks>
    public virtual void Complete(string taskId)
    {
        if (_subs.TryRemove(taskId, out var subs))
            foreach (var s in subs.Items) s.Channel.Writer.TryComplete();
    }

    internal int SubscriberCount(string taskId) => _subs.TryGetValue(taskId, out var s) ? s.Items.Length : 0;

    /// <summary>True when this process has a live subscriber for the topic. Lets a distributed subclass skip events nobody here reads.</summary>
    protected bool HasSubscribers(string taskId) => SubscriberCount(taskId) > 0;

    private void Remove(Subscription sub)
    {
        while (_subs.TryGetValue(sub.TaskId, out var current))
        {
            var updated = current.Remove(sub);
            if (updated.Items.Length == 0)
            {
                if (((ICollection<KeyValuePair<string, ImmutableSubscribers>>)_subs).Remove(new(sub.TaskId, current))) return;
            }
            else if (_subs.TryUpdate(sub.TaskId, updated, current)) return;
        }
    }

    private sealed record ImmutableSubscribers(Subscription[] Items)
    {
        public ImmutableSubscribers Add(Subscription s) => new([.. Items, s]);
        public ImmutableSubscribers Remove(Subscription s) => new(Items.Where(x => x != s).ToArray());
    }

    /// <summary>A live subscription to the events of one task.</summary>
    public sealed class Subscription : IDisposable
    {
        private readonly TaskEventBus _bus;

        internal Subscription(TaskEventBus bus, string taskId, Channel<SequencedEvent> channel)
        {
            _bus = bus;
            TaskId = taskId;
            Channel = channel;
        }

        /// <summary>Id of the task the subscription follows.</summary>
        public string TaskId { get; }
        internal Channel<SequencedEvent> Channel { get; }
        /// <summary>Reads events in sequence order. A gap in sequences means events were dropped: read them from the event store.</summary>
        public ChannelReader<SequencedEvent> Reader => Channel.Reader;

        /// <summary>Ends the subscription and completes the reader.</summary>
        public void Dispose()
        {
            Channel.Writer.TryComplete();
            _bus.Remove(this);
        }
    }
}
