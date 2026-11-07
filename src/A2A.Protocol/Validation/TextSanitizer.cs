using System.Text;

namespace A2A.Validation;

/// <summary>
/// Makes peer-supplied text safe to display, log or put in a prompt: removes C0/C1 control characters except <c>\n</c> and
/// <c>\t</c>, removes Unicode bidi controls (U+202A to U+202E, U+2066 to U+2069), and caps the length, ending with an ellipsis.
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
        foreach (var c in text)
        {
            if (char.IsControl(c) && c is not ('\n' or '\t')) continue;
            if (c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')) continue;
            sb.Append(c);
            if (sb.Length > maxLength) break; // long enough to know it is truncated
        }
        if (sb.Length <= maxLength) return sb.ToString();

        var keep = maxLength - 1;
        if (keep > 0 && char.IsHighSurrogate(sb[keep - 1])) keep--; // do not split a surrogate pair
        return sb.ToString(0, keep) + "…";
    }

    /// <summary>A single-line form for quoting a peer value inside an error message.</summary>
    public static string ForMessage(string? value) =>
        value is null ? "" : Sanitize(value, MaxHeaderValueLength).Replace('\n', ' ').Replace('\t', ' ');
}
