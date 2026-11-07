using System.Text;

namespace A2A.Validation;

/// <summary>
/// Makes peer-supplied text safe to display, log or put in a prompt: removes C0/C1 control characters except <c>\n</c> and
/// <c>\t</c>, Unicode bidi controls (U+202A to U+202E, U+2066 to U+2069), zero-width and invisible format characters
/// (U+200B to U+200F, U+2060 to U+2064, U+FEFF, U+061C, U+180E) and Unicode tag characters (U+E0000 to U+E007F, which can
/// hide text), and caps the length, ending with an ellipsis.
/// </summary>
internal static class TextSanitizer
{
    /// <summary>Length cap for header values (<c>typ</c>, <c>kid</c>, <c>alg</c>, hosts) quoted in error messages.</summary>
    public const int MaxHeaderValueLength = 200;

    public static string Sanitize(string text, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 1);
        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var codePoint = char.ConvertToUtf32(c, text[i + 1]);
                i++;
                if (IsTag(codePoint)) continue;
                sb.Append(c).Append(text[i]);
            }
            else
            {
                if (IsStripped(c)) continue;
                sb.Append(c);
            }
            if (sb.Length > maxLength) break; // long enough to know it is truncated
        }
        if (sb.Length <= maxLength) return sb.ToString();

        var keep = maxLength - 1;
        if (keep > 0 && char.IsHighSurrogate(sb[keep - 1])) keep--; // do not split a surrogate pair
        return sb.ToString(0, keep) + "\u2026";
    }

    /// <summary>Unicode tag characters, U+E0000 to U+E007F.</summary>
    private static bool IsTag(int codePoint) => codePoint is >= 0xE0000 and <= 0xE007F;

    /// <summary>Control (except newline and tab), bidi control, zero-width or invisible format character.</summary>
    private static bool IsStripped(char c) =>
        (char.IsControl(c) && c is not ('\n' or '\t'))
        || c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')
            or (>= '\u200B' and <= '\u200F') or (>= '\u2060' and <= '\u2064')
            or '\uFEFF' or '\u061C' or '\u180E';

    /// <summary>A single-line form for quoting a peer value inside an error message.</summary>
    public static string ForMessage(string? value) =>
        value is null ? "" : Sanitize(value, MaxHeaderValueLength).Replace('\n', ' ').Replace('\t', ' ');
}
