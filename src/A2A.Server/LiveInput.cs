using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace A2A.Server;

/// <summary>
///     A2A 1.1: input the client sends to a task while an execution of it runs (spec 1.1 §3.1.12): a message, and the
///     artifacts
///     it wrote with it. Both are already stored on the task (timeline, artifacts) when the agent reads them.
/// </summary>
/// <param name="Message">The client's message, or null for artifact updates sent on their own.</param>
/// <param name="Artifacts">The artifacts the client wrote with it, in the order they were applied. Empty when none.</param>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed record LiveInputItem(Message? Message, IReadOnlyList<Artifact> Artifacts);

/// <summary>
///     A2A 1.1: the client input that arrives for the task while this execution runs (
///     <see cref="AgentRequestContext.LiveInput" />).
///     An agent opts in by reading it: from the first <see cref="ReadAllAsync" /> on, a message for the task (from a live
///     exchange
///     or any SendMessage) goes to this execution instead of starting a new one, also while the task is <c>WORKING</c>. An
///     agent
///     that never reads it keeps the usual follow-up rules. Input only reaches an execution on the same server instance.
/// </summary>
[Experimental("A2AEXP005",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public interface ILiveInput
{
    /// <summary>The input, in the order it was stored. Ends when the execution ends.</summary>
    /// <param name="cancellationToken">Stops the reading.</param>
    IAsyncEnumerable<LiveInputItem> ReadAllAsync(CancellationToken cancellationToken = default);
}

#pragma warning disable A2AEXP005
/// <summary>The bounded buffer behind <see cref="ILiveInput" />; one per execution.</summary>
internal sealed class LiveInputBuffer(int capacity) : ILiveInput
{
    private readonly int _capacity = Math.Max(1, capacity);

    private readonly Channel<LiveInputItem> _items = Channel.CreateBounded<LiveInputItem>(
        new BoundedChannelOptions(Math.Max(1, capacity))
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

    private int _listening;
    private int _queued;

    /// <summary>True once the agent started reading, until the execution ended.</summary>
    public bool Listening => Volatile.Read(ref _listening) == 1 && !_items.Reader.Completion.IsCompleted;

    /// <summary>True when the buffer can take another item (checked before the input is stored).</summary>
    public bool HasRoom => Volatile.Read(ref _queued) < _capacity;

    public async IAsyncEnumerable<LiveInputItem> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _listening, 1);
        await foreach (var item in _items.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref _queued);
            yield return item;
        }
    }

    /// <summary>Hands an item to the agent; false when the buffer is full or the execution ended.</summary>
    public bool TryWrite(LiveInputItem item)
    {
        if (!_items.Writer.TryWrite(item)) return false;
        Interlocked.Increment(ref _queued);
        return true;
    }

    /// <summary>The execution ended: no more input.</summary>
    public void Complete()
    {
        _items.Writer.TryComplete();
    }
}
#pragma warning restore A2AEXP005