namespace A2A.Mcp;

internal static class A2AMcpMediaTypes
{
    internal const string OctetStream = "application/octet-stream";

    /// <summary>
    /// Returns <c>type/subtype</c> (parameters cut) when <paramref name="mediaType"/> looks like a media type, else
    /// <c>application/octet-stream</c>. A media type from a remote side is not trusted to be well formed.
    /// </summary>
    internal static string Normalize(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType)) return OctetStream;
        var main = mediaType.AsSpan();
        var semicolon = main.IndexOf(';');
        if (semicolon >= 0) main = main[..semicolon];
        main = main.Trim();
        var slash = main.IndexOf('/');
        if (slash <= 0 || slash == main.Length - 1 || main.Length > 127) return OctetStream;
        foreach (var c in main)
        {
            if (c == '/') continue;
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '+' or '_' or '!' or '#' or '$' or '&' or '^')) return OctetStream;
        }
        if (main[(slash + 1)..].Contains('/')) return OctetStream;
        return main.ToString();
    }
}
