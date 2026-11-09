using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace A2A.Signing;

/// <summary>JSON Canonicalization Scheme (RFC 8785).</summary>
public static class JsonCanonicalizer
{
    /// <summary>Canonicalizes a JSON node and returns the UTF-8 bytes.</summary>
    public static byte[] Canonicalize(JsonNode? node) => Encoding.UTF8.GetBytes(CanonicalizeToString(node));

    /// <summary>Canonicalizes a JSON node and returns the string.</summary>
    public static string CanonicalizeToString(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    /// <summary>Parses <paramref name="json"/>, canonicalizes it and returns the string.</summary>
    public static string CanonicalizeToString(string json) => CanonicalizeToString(JsonNode.Parse(json));

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                // RFC 8785 §3.2.3: sort by UTF-16 code units (ordinal). An array sort: no LINQ buffers per object.
                var members = new KeyValuePair<string, JsonNode?>[obj.Count];
                var m = 0;
                foreach (var member in obj) members[m++] = member;
                Array.Sort(members, static (a, b) => string.CompareOrdinal(a.Key, b.Key));
                foreach (var kv in members)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    Write(sb, kv.Value);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                for (var i = 0; i < arr.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Write(sb, arr[i]);
                }
                sb.Append(']');
                break;
            case JsonValue val:
                WriteValue(sb, val);
                break;
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue val)
    {
        using var parsed = val.TryGetValue<JsonElement>(out _) ? null : JsonDocument.Parse(val.ToJsonString());
        var element = parsed?.RootElement ?? val.GetValue<JsonElement>();
        switch (element.ValueKind)
        {
            case JsonValueKind.String: WriteString(sb, element.GetString()!); break;
            case JsonValueKind.True: sb.Append("true"); break;
            case JsonValueKind.False: sb.Append("false"); break;
            case JsonValueKind.Null: sb.Append("null"); break;
            case JsonValueKind.Number: sb.Append(FormatNumber(element.GetDouble())); break;
            default: throw new InvalidOperationException($"Unexpected JSON value kind {element.ValueKind}.");
        }
    }

    internal static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>ECMAScript Number.prototype.toString (RFC 8785 §3.2.2.3).</summary>
    internal static string FormatNumber(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) throw new InvalidOperationException("NaN/Infinity are not valid JSON.");
        if (d == 0) return "0";

        // Shortest round-trip digits and exponent.
        var shortest = ShortestRoundTrip(Math.Abs(d));
        ParseDigits(shortest, out var digits, out var exp10);

        var k = digits.Length;      // number of significant digits
        var n = exp10 + 1;          // position of decimal point relative to the digits
        var sb = new StringBuilder();
        if (d < 0) sb.Append('-');

        if (k <= n && n <= 21)
        {
            sb.Append(digits).Append('0', n - k);
        }
        else if (0 < n && n <= 21)
        {
            sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
        }
        else if (-6 < n && n <= 0)
        {
            sb.Append("0.").Append('0', -n).Append(digits);
        }
        else
        {
            sb.Append(digits[0]);
            if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
            var e = n - 1;
            sb.Append('e').Append(e >= 0 ? "+" : "-").Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static string ShortestRoundTrip(double abs)
    {
#if NETSTANDARD2_0
        // On .NET Framework "R" is not the shortest form (it tries 15 digits, then 17). Try each precision, shortest first.
        for (var precision = 1; precision <= 17; precision++)
        {
            var candidate = abs.ToString("G" + precision.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            // TryParse: on .NET Framework a candidate rounded past double.MaxValue (2E+308) overflows, and Parse would throw.
            if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed == abs) return candidate;
        }
#endif
        return abs.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>Splits a .NET "R" formatted positive number into significant digits and a base-10 exponent of the first digit.</summary>
    private static void ParseDigits(string s, out string digits, out int exp10)
    {
        var exp = 0;
        var ePos = s.IndexOfAny(['E', 'e']);
        if (ePos >= 0)
        {
            exp = int.Parse(s.AsSpan(ePos + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            s = s[..ePos];
        }
        var dot = s.IndexOf('.');
        var intPart = dot >= 0 ? s[..dot] : s;
        var fracPart = dot >= 0 ? s[(dot + 1)..] : string.Empty;
        var all = intPart + fracPart;
        var pointPos = intPart.Length + exp; // decimal point index within 'all'

        var lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0') lead++;
        all = all[lead..];
        pointPos -= lead;
        all = all.TrimEnd('0');
        if (all.Length == 0) all = "0";
        digits = all;
        exp10 = pointPos - 1;
    }
}
