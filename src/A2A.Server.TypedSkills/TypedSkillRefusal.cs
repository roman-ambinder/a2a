using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using A2A.Skills;

namespace A2A.Server.TypedSkills;

/// <summary>
///     Builds the InvalidParams errors of typed skills. Texts are fixed: they never repeat a value, an id or a .NET
///     type name from the caller's side.
/// </summary>
internal static class TypedSkillRefusal
{
    private const string BadRequestType = "type.googleapis.com/google.rpc.BadRequest";
    private const int MaxViolations = 20;
    private const int MaxTextLength = 200;

    public static A2AException Refuse(string text, IReadOnlyList<TypedSkillValidationError>? violations = null)
    {
        return new A2AException(A2AErrorCode.InvalidParams, text)
        {
            Metadata = new Dictionary<string, string> { ["extension"] = TypedSkillExtension.ExtensionUri },
            Details = violations is { Count: > 0 } ? [BadRequest(violations)] : null
        };
    }

    private static JsonElement BadRequest(IReadOnlyList<TypedSkillValidationError> violations)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("@type", BadRequestType);
            writer.WriteStartArray("fieldViolations");
            for (var i = 0; i < violations.Count && i < MaxViolations; i++)
            {
                writer.WriteStartObject();
                writer.WriteString("field", Clip(violations[i].Field));
                writer.WriteString("description", Clip(violations[i].Description));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string Clip(string text)
    {
        return text.Length <= MaxTextLength ? text : text.Substring(0, MaxTextLength);
    }

    /// <summary>
    ///     The path of a <see cref="JsonException" />, without keys and indexes a caller chose. It keeps the property names of
    ///     the type and
    ///     cuts the path (with <c>[*]</c>) where it leaves them: below a dictionary, a list or a property it cannot place.
    /// </summary>
    public static string SafePath(string? path, JsonTypeInfo root)
    {
        if (string.IsNullOrEmpty(path)) return "$";
        var safe = new StringBuilder("$");
        var info = root;
        var i = 1;
        while (i < path!.Length && path[i] == '.' && info.Kind == JsonTypeInfoKind.Object)
        {
            var end = path.IndexOfAny(['.', '['], i + 1);
            var name = end < 0 ? path.Substring(i + 1) : path.Substring(i + 1, end - i - 1);
            var property = info.Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
            if (property is null) break;
            safe.Append('.').Append(property.Name);
            i = end < 0 ? path.Length : end;
            try
            {
                info = info.Options.GetTypeInfo(property.PropertyType);
            }
            catch (NotSupportedException)
            {
                break;
            }
        }

        if (i < path.Length) safe.Append("[*]");
        return safe.ToString();
    }
}