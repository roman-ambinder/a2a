using System.Text;

namespace A2A.Client.Cards;

/// <summary>
/// Text from a peer's Agent Card (spec §7.5, S6). <see cref="ToString"/> returns a sanitized form safe to show or put in a
/// prompt: C0/C1 control characters except <c>\n</c> and <c>\t</c> removed, Unicode bidi controls (U+202A–U+202E,
/// U+2066–U+2069) removed, and the result capped at <see cref="MaxLength"/> characters ending with <c>…</c>.
/// <see cref="Raw"/> gives the text as received; use it only deliberately.
/// </summary>
public readonly struct UntrustedText
{
    /// <summary>Default for <see cref="MaxLength"/> and <see cref="A2ACardResolverOptions.MaxCardTextLength"/>.</summary>
    public const int DefaultMaxLength = 2000;

    private readonly string? _raw;
    private readonly int _maxLength;

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
    public override string ToString() => Sanitize(Raw, MaxLength);

    private static string Sanitize(string text, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\n' or '\t')) continue;
            if (c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) continue;
            sb.Append(c);
            if (sb.Length > maxLength) break; // long enough to know it is truncated
        }
        if (sb.Length <= maxLength) return sb.ToString();

        var keep = maxLength - 1;
        if (keep > 0 && char.IsHighSurrogate(sb[keep - 1])) keep--; // do not split a surrogate pair
        return sb.ToString(0, keep) + "…";
    }
}
