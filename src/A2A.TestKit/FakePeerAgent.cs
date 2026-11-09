using A2A.Client;
using A2A.Server;

namespace A2A.TestKit;

/// <summary>One call a <see cref="FakePeerAgent"/> received.</summary>
public sealed class PeerCall
{
    internal PeerCall(Message message, string text, string taskId, string contextId, bool isNewTask, bool matched)
    {
        Message = message;
        Text = text;
        TaskId = taskId;
        ContextId = contextId;
        IsNewTask = isNewTask;
        Matched = matched;
    }

    /// <summary>The message the caller sent.</summary>
    public Message Message { get; }

    /// <summary>The text of the message.</summary>
    public string Text { get; }

    /// <summary>The id of the task the call belongs to.</summary>
    public string TaskId { get; }

    /// <summary>The id of the context the call belongs to.</summary>
    public string ContextId { get; }

    /// <summary>True when the call started a new task, false for a follow-up to an existing one.</summary>
    public bool IsNewTask { get; }

    /// <summary>False when no queued answer, rule or fallback of the script matched the call.</summary>
    public bool Matched { get; }

    /// <summary>A one-line description for failure messages.</summary>
    public override string ToString() => $"\"{Text}\"{(Matched ? "" : " (unmatched)")}";
}

/// <summary>
/// A scripted peer agent for testing an agent that calls other agents. It runs in-process (an <see cref="A2ATestHost"/>), answers
/// the way its <see cref="FakePeerScript"/> says, and records every call.
/// </summary>
public sealed class FakePeerAgent : IAsyncDisposable
{
    private readonly PeerExecutor _executor;

    private FakePeerAgent(A2ATestHost host, PeerExecutor executor)
    {
        Host = host;
        _executor = executor;
    }

    /// <summary>The rules of the peer. You can add rules after the peer started.</summary>
    public FakePeerScript Script => _executor.Script;

    /// <summary>The host the peer runs on.</summary>
    public A2ATestHost Host { get; }

    /// <summary>The URL of the peer.</summary>
    public Uri Url => Host.Url;

    /// <summary>The Agent Card of the peer. Its name is "Fake Peer" unless <see cref="A2ATestHostOptions.ConfigureCard"/> changed it.</summary>
    public AgentCard Card => Host.Card;

    /// <summary>The calls received so far, oldest first. A copy: it does not change later.</summary>
    public IReadOnlyList<PeerCall> Calls => _executor.Calls.Snapshot();

    /// <summary>The calls that no queued answer, rule or fallback matched.</summary>
    public IReadOnlyList<PeerCall> UnmatchedCalls => _executor.Calls.Snapshot().Where(c => !c.Matched).ToArray();

    /// <summary>Starts a peer. <paramref name="script"/> sets its rules. <paramref name="configure"/> sets the host options.</summary>
    public static async Task<FakePeerAgent> StartAsync(Action<FakePeerScript> script, Action<A2ATestHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(script);
        var executor = new PeerExecutor();
        script(executor.Script);
        var host = await A2ATestHost.StartAsync(executor, options =>
        {
            configure?.Invoke(options);
            var card = options.ConfigureCard;
            options.ConfigureCard = c =>
            {
                c.Name = "Fake Peer";
                c.Description = "A scripted fake peer agent.";
                card?.Invoke(c);
            };
        }).ConfigureAwait(false);
        executor.Clock = host.Clock;
        return new FakePeerAgent(host, executor);
    }

    /// <summary>Creates the in-memory HTTP handler that reaches the peer.</summary>
    public HttpMessageHandler CreateHandler() => Host.CreateHandler();

    /// <summary>Creates an <see cref="HttpClient"/> for the peer, for example for <c>A2AClientFactory.CreateAsync(peer.Url, peer.CreateHttpClient())</c>.</summary>
    public HttpClient CreateHttpClient(string? user = null) => Host.CreateHttpClient(user);

    /// <summary>Creates an A2A client for the peer. See <see cref="A2ATestHost.CreateClient"/>.</summary>
    public A2AClient CreateClient(string binding = ProtocolBindings.JsonRpc, string? user = null, A2AClientOptions? options = null) =>
        Host.CreateClient(binding, user, options);

    /// <summary>
    /// Waits for the first call that matches <paramref name="predicate"/> (any call when null), looking at the calls received
    /// already and at future ones. Does not poll.
    /// </summary>
    /// <param name="predicate">Which call to wait for.</param>
    /// <param name="timeout">How long to wait, in real time. Default 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="A2AAssertionException">No call matched in time. The message lists the calls that did arrive.</exception>
    public Task<PeerCall> WaitForCallAsync(Func<PeerCall, bool>? predicate = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        return _executor.Calls.WaitAsync<PeerCall>(
            all =>
            {
                foreach (var c in all)
                    if (predicate is null || predicate(c)) return (true, c);
                return (false, null!);
            },
            limit,
            all => $"The peer received no matching call within {limit}. " + (all.Count == 0 ? "No call arrived." : "Calls: " + string.Join(", ", all.Select(c => c.ToString()))),
            cancellationToken);
    }

    /// <summary>Stops the peer's host.</summary>
    public ValueTask DisposeAsync() => Host.DisposeAsync();

    private sealed class PeerExecutor : IAgentExecutor
    {
        public FakePeerScript Script { get; } = new();
        public Recorder<PeerCall> Calls { get; } = new();
        public TimeProvider? Clock { get; set; }

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
        {
            var response = Script.Match(context);
            Calls.Add(new PeerCall(context.Message, context.UserText, context.TaskId, context.ContextId, context.IsNewTask, response is not null));
            if (response is null)
            {
                await updater.FailAsync($"The fake peer has no answer for the text \"{context.UserText}\".", cancellationToken).ConfigureAwait(false);
                return;
            }
            await response.RunAsync(context, updater, Clock ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        }
    }
}
