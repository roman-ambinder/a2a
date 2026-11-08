using System.Globalization;
using System.Text;
using A2A.Text;

namespace A2A.Client.Peers;

/// <summary>
/// Compares agent names for the registry. Names are untrusted, so every input is sanitized and capped first. The confusable table
/// is curated (Cyrillic, Greek and Latin lookalikes), not all of UTS #39.
/// </summary>
internal static class AgentNameNormalizer
{
    // Keys are written as escapes so a confusable cannot hide in source. Mapped before lower-casing: Greek H and h differ.
    private static readonly Dictionary<int, char> Confusables = BuildConfusables();

    /// <summary>Key for duplicate checks: NFKC, invariant lower case, whitespace collapsed. Empty when nothing remains.</summary>
    public static string NameKey(string? name, int maxLength)
    {
        if (!TryNormalize(Prepare(name, maxLength), NormalizationForm.FormKC, out var normalized))
        {
            return "";
        }

        var sb = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(Rune.ToLowerInvariant(rune).ToString());
        }

        // NFKC can expand text (the ellipsis becomes three dots), so cap again.
        var keep = Math.Min(sb.Length, maxLength);
        if (keep > 0 && keep < sb.Length && char.IsHighSurrogate(sb[keep - 1]))
        {
            keep--;
        }

        return sb.ToString(0, keep);
    }

    /// <summary>Key for lookalike checks. Empty when no letters or digits remain.</summary>
    public static string Skeleton(string? name, int maxLength)
    {
        if (!TryNormalize(Prepare(name, maxLength), NormalizationForm.FormKD, out var decomposed))
        {
            return "";
        }

        var sb = new StringBuilder(decomposed.Length);
        foreach (var rune in decomposed.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            var mapped = Confusables.TryGetValue(rune.Value, out var latin) ? new Rune(latin) : rune;
            mapped = Rune.ToLowerInvariant(mapped);
            if (Rune.IsLetterOrDigit(mapped))
            {
                sb.Append(mapped.ToString());
            }
        }

        return sb.ToString().Replace("rn", "m", StringComparison.Ordinal).Replace("vv", "w", StringComparison.Ordinal);
    }

    /// <summary>True when one token holds letters from two of Latin, Greek and Cyrillic.</summary>
    public static bool IsMixedScript(string? name, int maxLength)
    {
        var scripts = Script.None;
        foreach (var rune in Prepare(name, maxLength).EnumerateRunes())
        {
            // A mark must not end a token, or one placed at each script boundary would hide the mix.
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark
                or UnicodeCategory.SpacingCombiningMark)
            {
                continue;
            }

            if (!Rune.IsLetterOrDigit(rune))
            {
                scripts = Script.None; // token boundary
                continue;
            }

            scripts |= ScriptOf(rune.Value);
            if (System.Numerics.BitOperations.PopCount((uint)scripts) > 1)
            {
                return true;
            }
        }

        return false;
    }

    // string.Normalize throws on input it dislikes. A name that still breaks it counts as empty.
    private static bool TryNormalize(string text, NormalizationForm form, out string normalized)
    {
        try
        {
            normalized = text.Normalize(form);
            return true;
        }
        catch (ArgumentException)
        {
            normalized = "";
            return false;
        }
    }

    // Order matters: sanitize, replace what Normalize rejects (invalid UTF-16, noncharacters), drop invisibles, fold fullwidth.
    private static string Prepare(string? name, int maxLength)
    {
        var sanitized = TextSanitizer.Sanitize(name ?? "", maxLength);
        var sb = new StringBuilder(sanitized.Length);
        foreach (var rune in sanitized.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0xFDD0 and <= 0xFDEF || (value & 0xFFFE) == 0xFFFE)
            {
                sb.Append('\uFFFD');
                continue;
            }

            if (IsInvisible(rune))
            {
                continue;
            }

            sb.Append(value is >= 0xFF01 and <= 0xFF5E ? new Rune(value - 0xFEE0).ToString() : rune.ToString());
        }

        return sb.ToString();
    }

    // Characters the sanitizer keeps that render as nothing, so they must not make two names differ.
    private static bool IsInvisible(Rune rune) =>
        Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format || rune.Value is
            0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or (>= 0x180B and <= 0x180F) or 0x2800 or 0x3164
            or (>= 0xFE00 and <= 0xFE0F) or 0xFFA0 or (>= 0xE0100 and <= 0xE01EF);

    [Flags]
    private enum Script
    {
        None = 0,
        Latin = 1,
        Greek = 2,
        Cyrillic = 4,
    }

    private static Script ScriptOf(int cp) => cp switch
    {
        (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= 0xC0 and <= 0x24F) or (>= 0x1E00 and <= 0x1EFF) => Script.Latin,
        (>= 0x370 and <= 0x3FF) or (>= 0x1F00 and <= 0x1FFF) => Script.Greek,
        (>= 0x400 and <= 0x52F) or (>= 0x1C80 and <= 0x1C8F) or (>= 0x2DE0 and <= 0x2DFF) or (>= 0xA640 and <= 0xA69F) => Script.Cyrillic,
        _ => Script.None,
    };

    private static Dictionary<int, char> BuildConfusables()
    {
        var map = new Dictionary<int, char>();
        void Add(char to, params int[] from)
        {
            foreach (var cp in from)
            {
                map[cp] = to;
            }
        }

        // Cyrillic
        Add('a', 0x0410, 0x0430); // А а
        Add('b', 0x0412, 0x0432); // В в
        Add('e', 0x0415, 0x0435); // Е е
        Add('s', 0x0405, 0x0455); // Ѕ ѕ
        Add('l', 0x0406); // І
        Add('i', 0x0456); // і
        Add('j', 0x0408, 0x0458); // Ј ј
        Add('k', 0x041A, 0x043A); // К к
        Add('m', 0x041C, 0x043C); // М м
        Add('h', 0x041D, 0x043D); // Н н
        Add('o', 0x041E, 0x043E); // О о
        Add('p', 0x0420, 0x0440); // Р р
        Add('c', 0x0421, 0x0441); // С с
        Add('t', 0x0422, 0x0442); // Т т
        Add('y', 0x0423, 0x0443); // У у
        Add('x', 0x0425, 0x0445); // Х х
        Add('h', 0x04BA, 0x04BB); // Һ һ
        Add('d', 0x0501); // ԁ
        Add('q', 0x051B); // ԛ
        Add('w', 0x051D); // ԝ
        Add('b', 0x042C, 0x044C); // Ь ь
        Add('v', 0x0474, 0x0475); // Ѵ ѵ

        // Greek
        Add('a', 0x0391, 0x03B1); // Α α
        Add('b', 0x0392, 0x03B2); // Β β
        Add('e', 0x0395, 0x03B5); // Ε ε
        Add('z', 0x0396, 0x03B6); // Ζ ζ
        Add('h', 0x0397); // Η
        Add('n', 0x03B7); // η
        Add('l', 0x0399); // Ι
        Add('i', 0x03B9); // ι
        Add('k', 0x039A, 0x03BA); // Κ κ
        Add('m', 0x039C); // Μ
        Add('u', 0x03BC); // μ
        Add('n', 0x039D); // Ν
        Add('v', 0x03BD); // ν
        Add('o', 0x039F, 0x03BF); // Ο ο
        Add('p', 0x03A1, 0x03C1); // Ρ ρ
        Add('t', 0x03A4, 0x03C4); // Τ τ
        Add('y', 0x03A5); // Υ
        Add('u', 0x03C5); // υ
        Add('x', 0x03A7, 0x03C7); // Χ χ
        Add('w', 0x03C9); // ω
        Add('y', 0x03B3); // γ
        Add('σ', 0x03C2); // ς -> σ (stays Greek; a final sigma has no Latin twin)

        // Latin
        Add('l', 'I', '1', '|');
        Add('o', '0');
        Add('i', 0x0131); // ı
        Add('a', 0x0251); // ɑ
        Add('g', 0x0261); // ɡ
        Add('f', 0x0192); // ƒ
        Add('l', 0x0142, 0x0141); // ł Ł
        Add('d', 0x0111, 0x0110); // đ Đ
        Add('o', 0x00F8, 0x00D8); // ø Ø
        return map;
    }
}
