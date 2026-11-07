using A2A.Validation;

namespace A2A.Client.Cards;

/// <summary>
/// Text from a peer's Agent Card (spec §7.5, S6). <see cref="ToString"/> returns a sanitized form safe to show or put in a
/// prompt: C0/C1 control characters except <c>\n</c> and <c>\t</c> removed, Unicode bidi controls (U+202A–U+202E,
/// U+2066–U+2069), zero-width and invisible format characters (U+200B–U+200F, U+2060–U+2064, U+FEFF, U+061C, U+180E) and
/// Unicode tag characters (U+E0000–U+E007F) removed, and the result capped at <see cref="MaxLength"/> characters ending with <c>…</c>.
/// <see cref="Raw"/> gives the text as received; use it only deliberately.
/// </summary>
public readonly struct UntrustedText
{
    /// <summary>Default for <see cref="MaxLength"/> and <see cref="A2ACardResolverOptions.MaxCardTextLength"/>.</summary>
    public const int DefaultMaxLength = 2000;

    private readonly string? _raw;
    private readonly int _maxLength;

    /// <summary>Wraps <paramref name="raw"/>. <paramref name="maxLength"/> caps the sanitized text; it defaults to <see cref="DefaultMaxLength"/> and must be at least 1.</summary>
    public UntrustedText(string? raw, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        _raw = raw;
        _maxLength = maxLength;
    }

    /// <summary>The text exactly as received. Not sanitized.</summary>
    public string Raw => _raw ?? string.Empty;

    /// <summary>Maximum length of <see cref="ToString"/>.</summary>
    public int MaxLength => _maxLength == 0 ? DefaultMaxLength : _maxLength;

    /// <summary>The sanitized text.</summary>
    public override string ToString() => TextSanitizer.Sanitize(Raw, MaxLength);
}
