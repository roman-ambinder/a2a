using System.Globalization;
using System.Text;
using A2A.Json;

namespace A2A.Server.Storage;

/// <summary>Opaque keyset cursor: (status timestamp ticks, task id).</summary>
public static class PageCursor
{
    /// <summary>Encodes the position after <paramref name="last"/> as an opaque token.</summary>
    public static string Encode(TaskRecord last)
    {
        var ticks = (last.Task.Status.Timestamp ?? last.CreatedAt).UtcTicks;
        return Signing.Base64Url.Encode(Encoding.UTF8.GetBytes($"{ticks.ToString(CultureInfo.InvariantCulture)}|{last.Task.Id}"));
    }

    /// <summary>Decodes a token. Returns null for a null or empty token. Throws InvalidParams for a malformed one.</summary>
    public static (long Ticks, string Id)? Decode(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var s = Encoding.UTF8.GetString(Signing.Base64Url.Decode(token));
            var bar = s.IndexOf('|');
            if (bar <= 0) throw new FormatException();
            return (long.Parse(s[..bar], CultureInfo.InvariantCulture), s[(bar + 1)..]);
        }
        catch (FormatException)
        {
            throw A2AException.InvalidParams("pageToken is invalid");
        }
    }
}
