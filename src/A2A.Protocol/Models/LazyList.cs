namespace A2A;

/// <summary>
///     ADR-004: the storage behind a public <see cref="List{T}" /> property of a model type (<see cref="AgentTask.History" />,
///     <see cref="AgentTask.Timeline" />, <see cref="Artifact.Parts" />). It holds either a materialized list or a shared
///     version (<see cref="AppendList{T}" />), or neither (the property is null).
/// </summary>
/// <remarks>
///     <para>
///         The copy methods (<see cref="ModelCopies" />) and the engine use <see cref="Share" />, <see cref="Add" /> and
///         <see cref="AddKeepLast" />, which never build a list. The public getter (<see cref="Get" />) builds the list on
///         its first call and publishes it with a compare-and-swap, so threads that read one instance at the same time
///         get the same list. From then on that list is the instance's content: a change made to it (by the owner of a
///         copy) is what <see cref="Share" /> copies and what <see cref="Add" /> appends to. The shared version stays
///         behind, unused.
///     </para>
///     <para>
///         The setter (<see cref="Set" />) replaces both. Like any change to a model instance it is for the owner of the
///         instance only (see the ownership rule on the server's task store): it is not safe against a concurrent reader,
///         and it never was.
///     </para>
///     <para>This is a mutable struct: it must only be used as a non-readonly field, never copied.</para>
/// </remarks>
internal struct LazyList<T>
{
    private List<T>? _list;
    private AppendList<T>? _shared;

    public LazyList(List<T>? list)
    {
        _list = list;
        _shared = null;
    }

    /// <summary>Whether the public property's list exists (it has been read, or set).</summary>
    public bool IsMaterialized => Volatile.Read(ref _list) is not null;

    /// <summary>The public property's value: the list, built from the shared version on the first call.</summary>
    public List<T>? Get()
    {
        if (Volatile.Read(ref _list) is { } list) return list;
        if (Volatile.Read(ref _shared) is not { } shared) return null;
        var built = shared.ToList();
        // The first reader's list wins: every reader of this instance gets the same list.
        return Interlocked.CompareExchange(ref _list, built, null) ?? built;
    }

    /// <summary>
    ///     The content as a read-only list, without building the public list: the list when it exists, else the shared
    ///     version, else null (the property is null). For the JSON contract, which writes the property from it
    ///     (<c>A2AJson</c>): serializing an instance never builds its lists. The caller must not keep it past its read.
    /// </summary>
    public IReadOnlyList<T>? View => Volatile.Read(ref _list) ?? (IReadOnlyList<T>?)Volatile.Read(ref _shared);

    /// <summary>Sets the public property: the list, and no shared version.</summary>
    public void Set(List<T>? value)
    {
        _shared = null;
        _list = value;
    }

    /// <summary>
    ///     The content as a shared version, for a copy of the instance. Without a list, the shared version itself (O(1)).
    ///     With one, a new version holding a copy of it, with room for <paramref name="extra" /> more (O(n), once: the copy
    ///     keeps the new version). Null when the property is null.
    /// </summary>
    public AppendList<T>? Share(int extra = 1)
    {
        var list = Volatile.Read(ref _list);
        return list is not null ? AppendList<T>.CopyOf(list, extra) : _shared;
    }

    /// <summary>Sets the content to a shared version (from <see cref="Share" />), with no list.</summary>
    public void SetShared(AppendList<T>? shared)
    {
        _list = null;
        _shared = shared;
    }

    /// <summary>
    ///     Appends to the content, as <c>(list ??= []).Add(item)</c> does on the property, without building the list. For
    ///     the owner of the instance.
    /// </summary>
    public void Add(T item)
    {
        if (_list is { } list) list.Add(item);
        else _shared = (_shared ?? AppendList<T>.Empty).Add(item);
    }

    /// <summary>Appends <paramref name="items" /> to the content, without building the list. For the owner.</summary>
    public void AddRange(IEnumerable<T> items)
    {
        if (_list is { } list) list.AddRange(items);
        else _shared = (_shared ?? AppendList<T>.Empty).AddRange(items);
    }

    /// <summary>
    ///     <see cref="Add" />, then drops the oldest items beyond <paramref name="max" /> (zero or negative keeps them all).
    ///     A shared version only moves its start; a list drops them in place.
    /// </summary>
    public void AddKeepLast(T item, int max)
    {
        if (_list is { } list)
        {
            list.Add(item);
            if (max > 0 && list.Count > max) list.RemoveRange(0, list.Count - max);
        }
        else
        {
            _shared = (_shared ?? AppendList<T>.Empty).Add(item).KeepLast(max);
        }
    }

    /// <summary>How many items the content has (0 when the property is null), without building the list.</summary>
    public int Count => Volatile.Read(ref _list) is { } list ? list.Count : _shared?.Count ?? 0;

    /// <summary>A new list with the last <paramref name="count" /> items of the content, without building this one's list.</summary>
    public List<T> GetLast(int count)
    {
        if (Volatile.Read(ref _list) is { } list)
        {
            if (count < 0 || count > list.Count) throw new ArgumentOutOfRangeException(nameof(count));
            return list.GetRange(list.Count - count, count);
        }

        var shared = _shared ?? AppendList<T>.Empty;
        if (count < 0 || count > shared.Count) throw new ArgumentOutOfRangeException(nameof(count));
        return shared.GetRange(shared.Count - count, count);
    }

    /// <summary>A new list with <paramref name="convert" /> applied to each item, without building this one's list.</summary>
    public List<TOut>? ConvertAll<TOut>(Func<T, TOut> convert)
    {
        var list = Volatile.Read(ref _list);
        if (list is not null) return list.ConvertAll(new Converter<T, TOut>(convert));
        return _shared?.ConvertAll(convert);
    }
}
