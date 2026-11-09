using System.Text;

namespace A2A.Mcp;

internal static class A2AMcpNames
{
    internal const int MaxToolNameLength = 128;

    internal static bool IsAllowedNameCharacter(char c) =>
        c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '.' or '-';

    /// <summary>Replaces every character outside <c>[A-Za-z0-9_.-]</c> with an underscore.</summary>
    internal static string Sanitize(string id)
    {
        var builder = new StringBuilder(id.Length);
        foreach (var c in id) builder.Append(IsAllowedNameCharacter(c) ? c : '_');
        return builder.ToString();
    }

    /// <summary>The name of a skill tool: the prefix, then the sanitized id, cut so the whole name has at most 128 characters.</summary>
    internal static string ToolName(string prefix, string skillId, int suffixRoom = 0)
    {
        var room = Math.Max(0, MaxToolNameLength - prefix.Length - suffixRoom);
        var body = Sanitize(skillId);
        if (body.Length > room) body = body[..room];
        return prefix + body;
    }
}
