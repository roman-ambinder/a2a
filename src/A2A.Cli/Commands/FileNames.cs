using System.Globalization;
using System.Text;

namespace A2A.Cli.Commands;

/// <summary>Makes a safe local file name out of a name that an agent chose.</summary>
internal static class FileNames
{
    public const int MaxLength = 200;

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    ///     Keeps only the last path segment (split at both <c>/</c> and <c>\</c>), drops control and invisible characters and
    ///     leading
    ///     dots, replaces characters that Windows does not allow, and caps the length. Uses <paramref name="fallback" />,
    ///     sanitized the
    ///     same way, when nothing usable is left.
    /// </summary>
    public static string Sanitize(string? name, string fallback)
    {
        var clean = Clean(name);
        if (clean.Length == 0) clean = Clean(fallback);
        return clean.Length == 0 ? "artifact" : clean;
    }

    private static string Clean(string? name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var cut = name.LastIndexOfAny(['/', '\\']);
        var segment = cut < 0 ? name : name[(cut + 1)..];

        var sb = new StringBuilder(segment.Length);
        foreach (var ch in segment)
        {
            var category = char.GetUnicodeCategory(ch);
            if (char.IsControl(ch) || category is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator) continue;
            sb.Append(ch is '<' or '>' or ':' or '"' or '|' or '?' or '*' ? '_' : ch);
        }

        // No leading dots (hidden files, "." and ".."), no leading space, no trailing dots or spaces (Windows drops them).
        var text = sb.ToString().TrimStart('.', ' ').TrimEnd('.', ' ');
        if (text.Length > MaxLength)
        {
            text = text[..MaxLength];
            if (char.IsHighSurrogate(text[^1])) text = text[..^1];
            text = text.TrimEnd('.', ' ');
        }

        if (text.Length == 0) return "";

        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? text : text[..dot];
        return ReservedDeviceNames.Contains(stem) ? "_" + text : text;
    }

    /// <summary>
    ///     Adds <c>-2</c>, <c>-3</c> before the extension until the name is not in <paramref name="used" />, and records
    ///     it.
    /// </summary>
    public static string Unique(string name, ISet<string> used)
    {
        var candidate = name;
        var ext = Path.GetExtension(name);
        var stem = name[..^ext.Length];
        for (var n = 2; !used.Add(candidate); n++)
            candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}{ext}");
        return candidate;
    }
}