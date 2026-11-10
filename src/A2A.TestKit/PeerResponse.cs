using A2A.Server;

namespace A2A.TestKit;

/// <summary>
///     What a <see cref="FakePeerAgent" /> does when a call arrives. Each method adds a step; the steps run in the order
///     you added them.
///     The steps use the real <see cref="TaskUpdater" />, so the peer behaves like any agent of the SDK.
/// </summary>
public sealed class PeerResponse
{
    private readonly List<Func<AgentRequestContext, TaskUpdater, TimeProvider, CancellationToken, Task>> _steps = [];

    internal PeerResponse()
    {
    }

    /// <summary>Answers with a message and no task (<see cref="TaskUpdater.ReplyAsync(string, CancellationToken)" />).</summary>
    public PeerResponse Reply(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Add((_, u, _, ct) => u.ReplyAsync(text, ct).AsTask());
    }

    /// <summary>Moves the task to <c>WORKING</c>, with a status message when <paramref name="text" /> is given.</summary>
    public PeerResponse Working(string? text = null)
    {
        return Add((_, u, _, ct) => u.StartWorkAsync(text, ct).AsTask());
    }

    /// <summary>Adds a text artifact.</summary>
    public PeerResponse Artifact(string text, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Artifact([Part.FromText(text)], name);
    }

    /// <summary>Adds an artifact made of <paramref name="parts" />.</summary>
    public PeerResponse Artifact(IEnumerable<Part> parts, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var copy = parts.ToList();
        return Add(async (_, u, _, ct) =>
            await u.AddArtifactAsync(copy, name: name, cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>Moves the task to <c>INPUT_REQUIRED</c> with the question <paramref name="text" />.</summary>
    public PeerResponse RequireInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Add((_, u, _, ct) => u.RequireInputAsync(text, ct).AsTask());
    }

    /// <summary>Moves the task to <c>AUTH_REQUIRED</c> with the message <paramref name="text" />.</summary>
    public PeerResponse RequireAuth(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Add((_, u, _, ct) => u.RequireAuthAsync(text, ct).AsTask());
    }

    /// <summary>Moves the task to <c>COMPLETED</c>.</summary>
    public PeerResponse Complete(string? text = null)
    {
        return Add((_, u, _, ct) => u.CompleteAsync(text, ct).AsTask());
    }

    /// <summary>Moves the task to <c>FAILED</c>.</summary>
    public PeerResponse Fail(string? text = null)
    {
        return Add((_, u, _, ct) => u.FailAsync(text, ct).AsTask());
    }

    /// <summary>Moves the task to <c>REJECTED</c>.</summary>
    public PeerResponse Reject(string? text = null)
    {
        return Add((_, u, _, ct) => u.RejectAsync(text, ct).AsTask());
    }

    /// <summary>
    ///     Waits for <paramref name="delay" />. Uses the host's <see cref="TimeProvider" /> when one is set, so a
    ///     <see cref="TestClock" /> ends the wait when you advance it.
    /// </summary>
    public PeerResponse Delay(TimeSpan delay)
    {
        return Add((_, _, clock, ct) => Task.Delay(delay, clock, ct));
    }

    /// <summary>Waits until <paramref name="gate" /> completes. The wait ends early when the call is canceled.</summary>
    public PeerResponse WaitFor(Task gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return Add((_, _, _, ct) => gate.WaitAsync(ct));
    }

    /// <summary>Throws <paramref name="exception" /> from the agent. The caller sees the task fail or an error.</summary>
    public PeerResponse Throw(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Add((_, _, _, _) => Task.FromException(exception));
    }

    /// <summary>Runs your own step with the request context and the real updater.</summary>
    public PeerResponse Run(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return Add((c, u, _, ct) => step(c, u, ct));
    }

    private PeerResponse Add(Func<AgentRequestContext, TaskUpdater, TimeProvider, CancellationToken, Task> step)
    {
        _steps.Add(step);
        return this;
    }

    internal async Task RunAsync(AgentRequestContext context, TaskUpdater updater, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        foreach (var step in _steps)
            await step(context, updater, clock, cancellationToken).ConfigureAwait(false);
    }
}