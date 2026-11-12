namespace A2A;

/// <summary>
///     ADR-004: one version of an append-only list whose versions share one backing array. A version is immutable: it
///     sees the slots <c>[start, start + Count)</c> of the array and never more, so a newer version that appends cannot
///     change what an older one sees. The model types keep their task lists in it until someone reads the public
///     <see cref="List{T}" /> property (<see cref="AgentTask.History" />, <see cref="AgentTask.Timeline" />,
///     <see cref="Artifact.Parts" />), so the engine can make a new version of a task per event without copying its lists.
/// </summary>
/// <remarks>
///     <para>
///         <b>Append.</b> Only the version that ends at the array's high-water mark (<see cref="Buffer.Used" />) may write
///         past its end. It claims the slots with a compare-and-swap on <see cref="Buffer.Used" />, writes them, and
///         returns a longer version. Any other version (a fork from an older version, or a second append from the same
///         version), or an append that does not fit, copies its own slots into a new array and appends there. So a slot
///         is written once, by the one appender that claimed it, before the version that sees it exists; no version
///         ever sees a slot it did not see when it was made.
///     </para>
///     <para>
///         <b>Visibility.</b> The slots of a version are written by the thread that made it, before it returns it. Other
///         threads get a version only through the task that holds it, and tasks are handed between threads by the
///         store, a channel or a lock, which publish the writes made before them.
///     </para>
///     <para>
///         <b>Trim.</b> <see cref="KeepLast" /> moves the start instead of copying: the dropped items stay in the array
///         (unreachable from the new version) until the next growth copies only the live items. With a cap of N kept
///         items the array grows to about 2N, so a trimmed append stays O(1) amortized.
///     </para>
/// </remarks>
internal sealed class AppendList<T>
{
    private const int MinCapacity = 4;

    /// <summary>An empty version with no array. Its first append allocates one.</summary>
    public static readonly AppendList<T> Empty = new(new Buffer(Array.Empty<T>(), 0), 0, 0);

    private readonly Buffer _buffer;
    private readonly int _start;

    private AppendList(Buffer buffer, int start, int count)
    {
        _buffer = buffer;
        _start = start;
        Count = count;
    }

    /// <summary>How many items this version has.</summary>
    public int Count { get; }

    /// <summary>The item at <paramref name="index" /> of this version.</summary>
    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _buffer.Items[_start + index];
        }
    }

    /// <summary>
    ///     A version holding a copy of <paramref name="items" />, with room for <paramref name="extra" /> more before the
    ///     first growth. The list is copied: a later change to it does not reach the version.
    /// </summary>
    public static AppendList<T> CopyOf(List<T> items, int extra = 0)
    {
        var count = items.Count;
        var array = new T[Math.Max(MinCapacity, count + extra)];
        items.CopyTo(array, 0);
        return new AppendList<T>(new Buffer(array, count), 0, count);
    }

    /// <summary>A version with <paramref name="item" /> after this version's items. This version is not changed.</summary>
    public AppendList<T> Add(T item)
    {
        var end = _start + Count;
        var buffer = _buffer;
        if (end < buffer.Items.Length && Interlocked.CompareExchange(ref buffer.Used, end + 1, end) == end)
        {
            buffer.Items[end] = item; // the claimed slot: no other version sees it, none will write it
            return new AppendList<T>(buffer, _start, Count + 1);
        }

        var array = new T[Capacity(Count + 1, Count)];
        Array.Copy(buffer.Items, _start, array, 0, Count);
        array[Count] = item;
        return new AppendList<T>(new Buffer(array, Count + 1), 0, Count + 1);
    }

    /// <summary>A version with <paramref name="items" /> after this version's items. This version is not changed.</summary>
    public AppendList<T> AddRange(IEnumerable<T> items)
    {
        // Read the items before claiming slots, so nothing can fail between the claim and the writes.
        var extra = items as ICollection<T> ?? items.ToArray();
        var added = extra.Count;
        if (added == 0) return this;
        var end = _start + Count;
        var buffer = _buffer;
        if (added <= buffer.Items.Length - end &&
            Interlocked.CompareExchange(ref buffer.Used, end + added, end) == end)
        {
            extra.CopyTo(buffer.Items, end);
            return new AppendList<T>(buffer, _start, Count + added);
        }

        var array = new T[Capacity(Count + added, Count)];
        Array.Copy(buffer.Items, _start, array, 0, Count);
        extra.CopyTo(array, Count);
        return new AppendList<T>(new Buffer(array, Count + added), 0, Count + added);
    }

    /// <summary>
    ///     This version when it has at most <paramref name="max" /> items, else a version of its newest
    ///     <paramref name="max" /> items (zero or negative keeps them all). Nothing is copied.
    /// </summary>
    public AppendList<T> KeepLast(int max)
    {
        if (max <= 0 || Count <= max) return this;
        return new AppendList<T>(_buffer, _start + (Count - max), max);
    }

    /// <summary>A new list with this version's items.</summary>
    public List<T> ToList()
    {
        var list = new List<T>(Count);
        if (Count > 0) list.AddRange(new ArraySegment<T>(_buffer.Items, _start, Count));
        return list;
    }

    /// <summary>A new list with the <paramref name="count" /> items of this version from <paramref name="index" /> on.</summary>
    public List<T> GetRange(int index, int count)
    {
        if (index < 0 || count < 0 || index > Count - count) throw new ArgumentOutOfRangeException(nameof(count));
        var list = new List<T>(count);
        if (count > 0) list.AddRange(new ArraySegment<T>(_buffer.Items, _start + index, count));
        return list;
    }

    /// <summary>A new list with <paramref name="convert" /> applied to each item, in order.</summary>
    public List<TOut> ConvertAll<TOut>(Func<T, TOut> convert)
    {
        var list = new List<TOut>(Count);
        for (var i = 0; i < Count; i++) list.Add(convert(_buffer.Items[_start + i]));
        return list;
    }

    // Growth doubles the live items, so a run of appends copies each item O(1) times. The live items only: a trimmed
    // prefix is left behind.
    private static int Capacity(int needed, int live)
    {
        return Math.Max(MinCapacity, Math.Max(needed, live * 2));
    }

    private sealed class Buffer(T[] items, int used)
    {
        public readonly T[] Items = items;

        /// <summary>The high-water mark: slots below it are claimed. It only grows (compare-and-swap).</summary>
        public int Used = used;
    }
}
