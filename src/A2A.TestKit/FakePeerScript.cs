using A2A.Server;

namespace A2A.TestKit;

/// <summary>
/// The rules of a <see cref="FakePeerAgent"/>. A call is answered by the first match in this order: queued answers
/// (<see cref="Enqueue"/>, one use each, first in first out), then rules in the order you added them, then <see cref="Otherwise"/>.
/// With no match the peer fails the task and records an unmatched call. Thread-safe: you can add rules while the peer runs.
/// </summary>
public sealed class FakePeerScript
{
    private readonly object _gate = new();
    private readonly Queue<Action<PeerResponse>> _queue = new();
    private readonly List<(Func<AgentRequestContext, bool> Predicate, Action<PeerResponse> Respond)> _rules = [];
    private Action<PeerResponse>? _otherwise;

    internal FakePeerScript() { }

    /// <summary>Answers a call whose text contains <paramref name="contains"/> (ordinal, case-sensitive).</summary>
    public FakePeerScript WhenText(string contains, Action<PeerResponse> respond)
    {
        ArgumentNullException.ThrowIfNull(contains);
        return When(c => c.UserText.Contains(contains, StringComparison.Ordinal), respond);
    }

    /// <summary>Answers a call that <paramref name="predicate"/> accepts.</summary>
    public FakePeerScript When(Func<AgentRequestContext, bool> predicate, Action<PeerResponse> respond)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(respond);
        lock (_gate) _rules.Add((predicate, respond));
        return this;
    }

    /// <summary>Answers the next call, once. Queued answers win over rules.</summary>
    public FakePeerScript Enqueue(Action<PeerResponse> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        lock (_gate) _queue.Enqueue(respond);
        return this;
    }

    /// <summary>Answers a call that nothing else matched. A second call replaces the first.</summary>
    public FakePeerScript Otherwise(Action<PeerResponse> respond)
    {
        ArgumentNullException.ThrowIfNull(respond);
        lock (_gate) _otherwise = respond;
        return this;
    }

    internal PeerResponse? Match(AgentRequestContext context)
    {
        Action<PeerResponse>? chosen = null;
        (Func<AgentRequestContext, bool> Predicate, Action<PeerResponse> Respond)[] rules;
        Action<PeerResponse>? otherwise;
        lock (_gate)
        {
            if (_queue.Count > 0) chosen = _queue.Dequeue();
            rules = [.. _rules];
            otherwise = _otherwise;
        }
        if (chosen is null)
        {
            foreach (var (predicate, respond) in rules)
            {
                if (!predicate(context)) continue;
                chosen = respond;
                break;
            }
        }
        chosen ??= otherwise;
        if (chosen is null) return null;
        var response = new PeerResponse();
        chosen(response);
        return response;
    }
}
