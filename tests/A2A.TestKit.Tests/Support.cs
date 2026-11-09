using A2A.Client;
using A2A.Server;
using A2A.TestKit;

namespace A2A.TestKit.Tests;

internal static class Ask
{
    public static SendMessageRequest Text(string text, string? taskId = null, string? contextId = null, bool returnImmediately = false) => new()
    {
        Message = Message.FromUser(text, contextId, taskId),
        Configuration = returnImmediately ? new SendMessageConfiguration { ReturnImmediately = true } : null,
    };

    public static IAsyncEnumerable<StreamResponse> Stream(IA2AClient client, string text) => client.SendStreamingMessageAsync(Text(text));
}

/// <summary>Replies with the text it got, and with the caller's principal for "who".</summary>
internal sealed class EchoExecutor : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
        context.UserText == "who"
            ? updater.ReplyAsync(context.Call.Principal, cancellationToken).AsTask()
            : updater.CompleteAsync("echo: " + context.UserText, cancellationToken).AsTask();
}

/// <summary>A clock that wraps a <see cref="TestClock"/> and tells you when somebody creates a timer with a given due time.</summary>
internal sealed class SpyClock(TestClock inner) : TimeProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<TimeSpan, TaskCompletionSource> _waiters = [];
    private readonly HashSet<TimeSpan> _seen = [];

    public TestClock Inner { get; } = inner;

    public Task WhenTimerAsync(TimeSpan dueTime)
    {
        lock (_gate)
        {
            if (_seen.Contains(dueTime)) return Task.CompletedTask;
            if (!_waiters.TryGetValue(dueTime, out var tcs)) _waiters[dueTime] = tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return tcs.Task;
        }
    }

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();
    public override long GetTimestamp() => Inner.GetTimestamp();
    public override long TimestampFrequency => Inner.TimestampFrequency;
    public override TimeZoneInfo LocalTimeZone => Inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = Inner.CreateTimer(callback, state, dueTime, period);
        lock (_gate)
        {
            _seen.Add(dueTime);
            if (_waiters.Remove(dueTime, out var tcs)) tcs.TrySetResult();
        }
        return timer;
    }
}
