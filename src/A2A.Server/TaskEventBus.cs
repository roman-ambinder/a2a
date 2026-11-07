using System.Collections.Concurrent;
using System.Threading.Channels;

namespace A2A.Server;

/// <summary>
/// Fan-out of applied task events to live subscribers (SubscribeToTask). Every subscriber gets every event in order (spec §3.5.2).
/// Process-local; replace for multi-instance deployments.
/// </summary>
public class TaskEventBus
{
    private readonly ConcurrentDictionary<string, ImmutableSubscribers> _subs = new(StringComparer.Ordinal);

    public virtual Subscription Subscribe(string taskId)
    {
        var channel = Channel.CreateUnbounded<StreamResponse>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        var sub = new Subscription(this, taskId, channel);
        _subs.AddOrUpdate(taskId, _ => new ImmutableSubscribers([sub]), (_, s) => s.Add(sub));
        return sub;
    }

    public virtual void Publish(string taskId, StreamResponse evt)
    {
        if (!_subs.TryGetValue(taskId, out var subs)) return;
        foreach (var s in subs.Items) s.Channel.Writer.TryWrite(evt);
    }

    /// <summary>Ends all subscriptions for a task (after a terminal state).</summary>
    public virtual void Complete(string taskId)
    {
        if (_subs.TryRemove(taskId, out var subs))
            foreach (var s in subs.Items) s.Channel.Writer.TryComplete();
    }

    internal int SubscriberCount(string taskId) => _subs.TryGetValue(taskId, out var s) ? s.Items.Length : 0;

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

    public sealed class Subscription : IDisposable
    {
        private readonly TaskEventBus _bus;

        internal Subscription(TaskEventBus bus, string taskId, Channel<StreamResponse> channel)
        {
            _bus = bus;
            TaskId = taskId;
            Channel = channel;
        }

        public string TaskId { get; }
        internal Channel<StreamResponse> Channel { get; }
        public ChannelReader<StreamResponse> Reader => Channel.Reader;

        public void Dispose()
        {
            Channel.Writer.TryComplete();
            _bus.Remove(this);
        }
    }
}
