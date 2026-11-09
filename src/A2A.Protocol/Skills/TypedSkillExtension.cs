using System.Text.Json;

namespace A2A.Skills;

/// <summary>
/// The typed-skills extension: an optional entry on the Agent Card that holds a JSON Schema for the input and the output of each typed
/// skill. A plain peer ignores it. Agents write it with <c>A2A.Server.TypedSkills</c>; clients can read it here.
/// </summary>
[global::System.Diagnostics.CodeAnalysis.Experimental("A2AEXP001", UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public static class TypedSkillExtension
{
    /// <summary>URI of the optional typed-skills extension.</summary>
    public const string ExtensionUri = "https://github.com/roman-ambinder/a2a/extensions/typed-skills/v1";

    /// <summary>Name of the extension param that maps a skill id to its schemas.</summary>
    public const string SkillsParam = "skills";

    /// <summary>Name of the input schema, inside the entry of a skill.</summary>
    public const string InputSchemaKey = "inputSchema";

    /// <summary>Name of the output schema, inside the entry of a skill.</summary>
    public const string OutputSchemaKey = "outputSchema";

    /// <summary>Reads the schemas of one skill from <paramref name="card"/>.</summary>
    /// <param name="card">The Agent Card.</param>
    /// <param name="skillId">The <see cref="AgentSkill.Id"/>. Compared ordinally.</param>
    /// <param name="inputSchema">The JSON Schema of the input. Default when this method returns false.</param>
    /// <param name="outputSchema">The JSON Schema of the output. Default when this method returns false.</param>
    /// <returns>True when the card declares the extension and both schemas of the skill are present and are JSON objects or booleans.</returns>
    /// <remarks>The card comes from a peer. The schemas are data: do not follow references in them.</remarks>
    public static bool TryGetSchemas(AgentCard card, string skillId, out JsonElement inputSchema, out JsonElement outputSchema)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(skillId);
        inputSchema = default;
        outputSchema = default;
        if (card.Capabilities.Extensions is not { } declared) return false;
        foreach (var extension in declared)
        {
            if (!string.Equals(extension.Uri, ExtensionUri, StringComparison.Ordinal)) continue;
            if (extension.Params is not { } values || !values.TryGetValue(SkillsParam, out var skills) || skills.ValueKind != JsonValueKind.Object) return false;
            if (!skills.TryGetProperty(skillId, out var entry) || entry.ValueKind != JsonValueKind.Object) return false;
            if (!entry.TryGetProperty(InputSchemaKey, out var input) || !IsSchema(input)) return false;
            if (!entry.TryGetProperty(OutputSchemaKey, out var output) || !IsSchema(output)) return false;
            inputSchema = input;
            outputSchema = output;
            return true;
        }
        return false;
    }

    private static bool IsSchema(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False;
}
