using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using A2A.Json;

namespace A2A.Skills;

/// <summary>
///     Client-directed skill selection: a client names the skill it wants in <c>Message.metadata["skillId"]</c>
///     (A2A epic #1989). The id is a hint from the caller, not an authorization decision.
/// </summary>
[Experimental("A2AEXP001",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public static class SkillSelection
{
    /// <summary>Metadata key that holds the skill id.</summary>
    public const string MetadataKey = "skillId";

    /// <summary>URI of the optional skill-selection extension.</summary>
    public const string ExtensionUri = "https://github.com/roman-ambinder/a2a/extensions/skill-selection/v1";

    /// <summary>Name of the extension param that makes the agent refuse a skill it does not have.</summary>
    public const string RejectUnknownSkillsParam = "rejectUnknownSkills";

    /// <summary>Creates the optional extension entry for an agent card. Clients need not support it.</summary>
    /// <param name="rejectUnknownSkills">
    ///     True to refuse, with InvalidParams, a caller that activated the extension and names a skill not on the card.
    ///     Default false.
    /// </param>
    public static AgentExtension CreateExtension(bool rejectUnknownSkills = false)
    {
        return new AgentExtension
        {
            Uri = ExtensionUri,
            Required = false,
            Description = "Lets a client name the skill it wants in message metadata.",
            Params = rejectUnknownSkills
                ? new Dictionary<string, JsonElement> { [RejectUnknownSkillsParam] = A2AJson.SerializeToElement(true) }
                : null
        };
    }

    /// <summary>
    ///     True when <paramref name="card" /> declares the extension with <see cref="RejectUnknownSkillsParam" /> set to
    ///     true.
    /// </summary>
    public static bool RejectsUnknownSkills(AgentCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (card.Capabilities.Extensions is not { } declared) return false;
        foreach (var extension in declared)
        {
            if (!string.Equals(extension.Uri, ExtensionUri, StringComparison.Ordinal)) continue;
            return extension.Params is { } values
                   && values.TryGetValue(RejectUnknownSkillsParam, out var value)
                   && value.ValueKind == JsonValueKind.True;
        }

        return false;
    }
}

/// <summary>Read and write the requested skill on messages.</summary>
[Experimental("A2AEXP001",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public static class SkillSelectionMessageExtensions
{
    /// <summary>
    ///     The skill id in the message metadata. Null when the key is absent, or its value is empty, not a string or not valid
    ///     text. Does not throw on
    ///     a bad shape.
    /// </summary>
    /// <remarks>The caller writes this value. Treat it as untrusted text, and do not use it for authorization.</remarks>
    public static string? GetRequestedSkillId(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Metadata is not { } metadata ||
            !metadata.TryGetValue(SkillSelection.MetadataKey, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        string? id;
        try
        {
            id = value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        } // text that is not valid UTF-16, such as a lone surrogate escape

        return string.IsNullOrEmpty(id) ? null : id;
    }

    /// <summary>Writes <paramref name="skillId" /> to the message metadata (created when null) and returns the same message.</summary>
    /// <exception cref="ArgumentException"><paramref name="skillId" /> is null or empty.</exception>
    public static Message SetRequestedSkillId(this Message message, string skillId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrEmpty(skillId);
        message.Metadata ??= [];
        message.Metadata[SkillSelection.MetadataKey] = A2AJson.SerializeToElement(skillId);
        return message;
    }
}