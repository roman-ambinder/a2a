namespace A2A.Client;

/// <summary>
/// A2A 1.1: one live exchange with an agent (spec 1.1 §3.1.12), from <c>A2AClient.OpenLiveAsync</c>.
/// <see cref="Events"/> is the downlink: the task's events, the client's own messages (message updates) and the refusals of
/// single requests (<see cref="StreamErrorEvent"/>), which do not end it. <c>SendAsync</c> is the uplink. The exchange
/// ends with the task, or when the connection is disposed.
/// </summary>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP005", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public abstract class A2ALiveConnection : IAsyncDisposable
{
    /// <summary>The downlink. Read it once; events of a kind this SDK does not know are skipped.</summary>
    public abstract IAsyncEnumerable<StreamResponse> Events { get; }

    /// <summary>
    /// Sends one more request on the exchange, for its task (a message without a task id gets it). A refusal comes back as a
    /// <see cref="StreamErrorEvent"/> on <see cref="Events"/>; over HTTP it is also thrown here.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public abstract Task SendAsync(StreamRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends a message on the exchange (<see cref="SendAsync(StreamRequest, CancellationToken)"/>).</summary>
    /// <param name="message">The message.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public Task SendAsync(Message message, CancellationToken cancellationToken = default) =>
        SendAsync(new StreamRequest { SendMessage = new SendMessageRequest { Message = message } }, cancellationToken);

    /// <summary>Ends the exchange: the uplink is closed and the downlink stops.</summary>
    public abstract ValueTask DisposeAsync();
}

/// <summary>A2A 1.1: a transport that can open a live exchange (<see cref="A2ALiveConnection"/>).</summary>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP005", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public interface ILiveTransport
{
    /// <summary>Opens a live exchange with <paramref name="first"/> as its first request. Sends <c>A2A-Version: 1.1</c>.</summary>
    /// <param name="first">The first request: a message to send, or a subscribe to an existing task.</param>
    /// <param name="options">Per-call options, used for every call of the exchange.</param>
    /// <param name="cancellationToken">Cancellation of the open; dispose the connection to end the exchange.</param>
    Task<A2ALiveConnection> OpenLiveAsync(StreamRequest first, A2ARequestOptions? options, CancellationToken cancellationToken);
}

#pragma warning disable A2AEXP005
/// <summary>
/// The HTTP form of a live exchange: the downlink is one streaming call (its <c>Accept</c> takes an event stream), and each
/// later request is a short call to the same operation, answered with the task.
/// </summary>
internal sealed class HttpLiveConnection(
    Func<CancellationToken, IAsyncEnumerable<StreamResponse>> openDownlink,
    Func<StreamRequest, CancellationToken, Task<AgentTask>> uplink,
    Func<string?> boundTask) : A2ALiveConnection
{
    private readonly CancellationTokenSource _stop = new();

    public override IAsyncEnumerable<StreamResponse> Events => openDownlink(_stop.Token);

    public override async Task SendAsync(StreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await uplink(LiveRequests.ForTask(request, boundTask()), cancellationToken).ConfigureAwait(false);
    }

    public override ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _stop.Dispose();
        return default;
    }
}

/// <summary>Helpers shared by the live connections.</summary>
internal static class LiveRequests
{
    /// <summary>Per-call options of a live exchange: the caller's, with <c>A2A-Version: 1.1</c>.</summary>
    public static A2ARequestOptions WithVersion11(A2ARequestOptions? options)
    {
        var copy = new A2ARequestOptions
        {
            IdempotencyKey = options?.IdempotencyKey,
            Timeout = options?.Timeout,
            Extensions = options?.Extensions,
            ConfigureRequest = options?.ConfigureRequest,
        };
        if (options is not null)
        {
            copy.MaxRetries = options.MaxRetries;
            foreach (var header in options.Headers) copy.Headers[header.Key] = header.Value;
        }
        copy.Headers[A2AProtocol.Headers.Version] = A2AProtocol.Version11;
        return copy;
    }

    /// <summary>The events of <paramref name="events"/>, telling <paramref name="taskId"/> the id of the first task it sees.</summary>
    public static async IAsyncEnumerable<StreamResponse> Track(IAsyncEnumerable<StreamResponse> events, Action<string> taskId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var e in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (e.TaskId is { Length: > 0 } id) taskId(id);
            yield return e;
        }
    }

    /// <summary>
    /// Over HTTP an uplink call must name its task: a message without a task id gets the exchange's task (the server checks
    /// that a named task is the exchange's).
    /// </summary>
    public static StreamRequest ForTask(StreamRequest request, string? taskId)
    {
        if (taskId is null || request.SendMessage is not { } send) return request;
        if (send.Message is { } m && string.IsNullOrEmpty(m.TaskId))
        {
            var message = m.DeepCopy();
            message.TaskId = taskId;
            return new StreamRequest
            {
                Tenant = request.Tenant,
                SendMessage = new SendMessageRequest
                {
                    Tenant = send.Tenant, Message = message, Configuration = send.Configuration, Metadata = send.Metadata, ArtifactUpdates = send.ArtifactUpdates,
                },
            };
        }
        return request;
    }
}
#pragma warning restore A2AEXP005
