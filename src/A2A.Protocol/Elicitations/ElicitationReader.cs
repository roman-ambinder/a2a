using System.Text.Json;
using A2A.Text;

namespace A2A.Elicitations;

/// <summary>Reads a form or an answer from peer JSON. Strict: a keyword that is not in the spec makes the whole value unreadable. Never throws.</summary>
internal static class ElicitationReader
{
    public static ElicitationForm? ReadForm(JsonElement element)
    {
        try { return ReadFormCore(element); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; } // a rule of a field, or text that is not valid UTF-16
    }

    private static ElicitationForm? ReadFormCore(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? id = null;
        JsonElement schema = default;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id":
                    if (property.Value.ValueKind != JsonValueKind.String) return null;
                    id = property.Value.GetString();
                    break;
                case "requestedSchema": schema = property.Value; break;
                default: return null;
            }
        }
        if (id is null || schema.ValueKind != JsonValueKind.Object) return null;

        JsonElement properties = default;
        var hasType = false;
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in schema.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != "object") return null;
                    hasType = true;
                    break;
                case "properties": properties = property.Value; break;
                case "required":
                    if (property.Value.ValueKind != JsonValueKind.Array) return null;
                    foreach (var name in property.Value.EnumerateArray())
                        if (name.ValueKind != JsonValueKind.String || !required.Add(name.GetString()!)) return null;
                    break;
                default: return null;
            }
        }
        if (!hasType || properties.ValueKind != JsonValueKind.Object) return null;

        var fields = new List<ElicitationField>();
        var requiredFound = 0;
        foreach (var property in properties.EnumerateObject())
        {
            if (fields.Count >= Elicitation.MaxFields) return null;
            var isRequired = required.Contains(property.Name);
            if (isRequired) requiredFound++;
            if (ReadField(property.Name, property.Value, isRequired) is not { } field) return null;
            fields.Add(field);
        }
        if (requiredFound != required.Count) return null; // a required name with no field

        return new ElicitationForm(fields, id, allowSensitiveFields: true); // the client decides what to do with a sensitive field
    }

    private static ElicitationField? ReadField(string name, JsonElement element, bool required)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? type = null, title = null, description = null;
        int? minLength = null, maxLength = null;
        double? minimum = null, maximum = null;
        ElicitationFormat? format = null;
        List<string>? options = null;
        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name)
            {
                case "type":
                    if (value.ValueKind != JsonValueKind.String) return null;
                    type = value.GetString();
                    break;
                case "title":
                    if (value.ValueKind != JsonValueKind.String) return null;
                    title = TextSanitizer.Sanitize(value.GetString()!, Elicitation.MaxTitleLength);
                    break;
                case "description":
                    if (value.ValueKind != JsonValueKind.String) return null;
                    description = TextSanitizer.Sanitize(value.GetString()!, Elicitation.MaxDescriptionLength);
                    break;
                case "minLength":
                    if (WholeNumber(value) is not { } min) return null;
                    minLength = min;
                    break;
                case "maxLength":
                    if (WholeNumber(value) is not { } max) return null;
                    maxLength = max;
                    break;
                case "format":
                    if (value.ValueKind != JsonValueKind.String) return null;
                    format = value.GetString() switch
                    {
                        "email" => ElicitationFormat.Email,
                        "date" => ElicitationFormat.Date,
                        "date-time" => ElicitationFormat.DateTime,
                        _ => null,
                    };
                    if (format is null) return null; // for example "uri"
                    break;
                case "enum":
                    if (value.ValueKind != JsonValueKind.Array) return null;
                    options = [];
                    foreach (var option in value.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.String) return null;
                        options.Add(option.GetString()!);
                    }
                    break;
                case "minimum":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var lo)) return null;
                    minimum = lo;
                    break;
                case "maximum":
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var hi)) return null;
                    maximum = hi;
                    break;
                default: return null; // nesting, arrays, default, pattern, and anything else
            }
        }
        var fieldType = type switch
        {
            "string" => ElicitationFieldType.String,
            "integer" => ElicitationFieldType.Integer,
            "number" => ElicitationFieldType.Number,
            "boolean" => ElicitationFieldType.Boolean,
            _ => (ElicitationFieldType?)null,
        };
        return fieldType is null
            ? null
            : ElicitationField.Create(name, fieldType.Value, title, description, required, minLength, maxLength, minimum, maximum, format, options);
    }

    /// <summary>A whole number that fits an int. gRPC turns every number into a double, so 3.0 counts.</summary>
    private static int? WholeNumber(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) return null;
        if (!double.IsFinite(number) || number != Math.Floor(number) || Math.Abs(number) > int.MaxValue) return null;
        return (int)number;
    }

    public static ElicitationAnswer? ReadAnswer(JsonElement element)
    {
        try { return ReadAnswerCore(element); }
        catch (InvalidOperationException) { return null; } // a name or text that is not valid UTF-16
    }

    private static ElicitationAnswer? ReadAnswerCore(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? id = null, action = null;
        JsonElement content = default;
        var hasContent = false;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "id":
                    if (property.Value.ValueKind != JsonValueKind.String) return null;
                    id = ReadText(property.Value);
                    break;
                case "action":
                    if (property.Value.ValueKind != JsonValueKind.String) return null;
                    action = ReadText(property.Value);
                    break;
                case "content":
                    content = property.Value;
                    hasContent = true;
                    break;
                default: return null;
            }
        }
        if (id is null || !Elicitation.IsFormId(id)) return null;
        var kind = action switch
        {
            "accept" => ElicitationAction.Accept,
            "decline" => ElicitationAction.Decline,
            "cancel" => ElicitationAction.Cancel,
            _ => (ElicitationAction?)null,
        };
        if (kind is null) return null;
        if (kind != ElicitationAction.Accept) return hasContent ? null : ElicitationAnswer.Create(id, kind.Value, null);
        if (!hasContent || content.ValueKind != JsonValueKind.Object) return null;

        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in content.EnumerateObject()) values[property.Name] = property.Value;
        return ElicitationAnswer.Create(id, kind.Value, values);
    }

    private static string? ReadText(JsonElement value)
    {
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; } // not valid UTF-16
    }
}
