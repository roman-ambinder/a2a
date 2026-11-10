using System.Collections.Concurrent;

namespace A2A.Client.Cards;

/// <summary>Process-local <see cref="ICardPinStore" />. Pins are lost on restart.</summary>
public sealed class InMemoryCardPinStore : ICardPinStore
{
    private readonly ConcurrentDictionary<string, CardPin> _pins = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public ValueTask<CardPin?> GetAsync(string origin, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromResult(_pins.TryGetValue(origin, out var pin) ? pin : null);
    }

    /// <inheritdoc />
    public ValueTask SetAsync(string origin, CardPin pin, CancellationToken cancellationToken = default)
    {
        _pins[origin] = pin ?? throw new ArgumentNullException(nameof(pin));
        return ValueTask.CompletedTask;
    }
}