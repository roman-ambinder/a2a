using System.Globalization;
using System.Text.Json;

namespace A2A.Elicitations;

/// <summary>A small typed form an agent asks a user to fill in. Build one on the server, or read one with <c>GetElicitationForm()</c>.</summary>
public sealed class ElicitationForm
{
    private const double MaxExactInteger = 9007199254740992; // 2^53
    private readonly Dictionary<string, ElicitationField> _byName = new(StringComparer.Ordinal);

    /// <summary>Creates a form.</summary>
    /// <param name="fields">1 to <see cref="Elicitation.MaxFields"/> fields with different names.</param>
    /// <param name="id">The form id, <c>[A-Za-z0-9._~-]+</c>, up to <see cref="Elicitation.MaxFormIdLength"/> characters. Null makes a new one.</param>
    /// <param name="allowSensitiveFields">
    /// False (the default) refuses a field that looks like a request for a secret. Set it to true only when you are sure the
    /// field does not ask for one: answers are stored in the task history and sent to webhooks.
    /// </param>
    /// <exception cref="ArgumentException">The fields or the id are not allowed, or a field looks sensitive.</exception>
    public ElicitationForm(IEnumerable<ElicitationField> fields, string? id = null, bool allowSensitiveFields = false)
    {
        ArgumentNullException.ThrowIfNull(fields);
        id ??= Guid.NewGuid().ToString("N");
        if (!Elicitation.IsFormId(id))
            throw new ArgumentException($"A form id must match [A-Za-z0-9._~-]+ and have at most {Elicitation.MaxFormIdLength} characters.", nameof(id));
        var list = new List<ElicitationField>();
        foreach (var field in fields)
        {
            if (field is null) throw new ArgumentException("A field is null.", nameof(fields));
            if (!_byName.TryAdd(field.Name, field)) throw new ArgumentException($"The field name '{field.Name}' is used twice.", nameof(fields));
            list.Add(field);
        }
        if (list.Count is 0 or > Elicitation.MaxFields)
            throw new ArgumentException($"A form needs 1 to {Elicitation.MaxFields} fields.", nameof(fields));
        Id = id;
        Fields = list;
        HasSensitiveFields = list.Exists(f => f.LooksSensitive);
        if (HasSensitiveFields && !allowSensitiveFields)
            throw new ArgumentException(
                $"The field '{list.Find(f => f.LooksSensitive)!.Name}' looks like a request for a secret. Do not collect secrets in a form; use the auth-required flow. Pass allowSensitiveFields: true only if this is a mistake of the check.",
                nameof(fields));
    }

    /// <summary>The form id. An answer names it.</summary>
    public string Id { get; }

    /// <summary>The fields, in order.</summary>
    public IReadOnlyList<ElicitationField> Fields { get; }

    /// <summary>True when a field looks like a request for a secret (<see cref="ElicitationField.LooksSensitive"/>).</summary>
    public bool HasSensitiveFields { get; }

    /// <summary>Checks the values of an answer against the fields. Does not change anything.</summary>
    /// <param name="content">The values, by field name.</param>
    /// <exception cref="A2AException">
    /// InvalidParams. The error names the rule and the field, and never repeats a value. Its metadata has <c>extension</c> and,
    /// when one field is at fault, <c>field</c>.
    /// </exception>
    public void Validate(IReadOnlyDictionary<string, JsonElement> content)
    {
        ArgumentNullException.ThrowIfNull(content);
        foreach (var name in content.Keys)
            if (!_byName.ContainsKey(name)) throw Refuse("The answer has a value for a field that is not in the form.");
        foreach (var field in Fields)
        {
            if (!content.TryGetValue(field.Name, out var value))
            {
                if (field.Required) throw Refuse($"The field '{field.Name}' is required.", field.Name);
                continue;
            }
            CheckValue(field, value);
        }
    }

    private static void CheckValue(ElicitationField field, JsonElement value)
    {
        switch (field.Type)
        {
            case ElicitationFieldType.String: CheckText(field, value); break;
            case ElicitationFieldType.Boolean:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Refuse($"The field '{field.Name}' must be true or false.", field.Name);
                break;
            default: CheckNumber(field, value); break;
        }
    }

    private static void CheckText(ElicitationField field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String) throw Refuse($"The field '{field.Name}' must be text.", field.Name);
        string text;
        try { text = value.GetString()!; }
        catch (InvalidOperationException) { throw Refuse($"The field '{field.Name}' is not valid text.", field.Name); }
        if (!Elicitation.IsValidText(text)) throw Refuse($"The field '{field.Name}' is not valid text.", field.Name);

        var length = Elicitation.CodePoints(text);
        if (length > Elicitation.MaxStringLength) throw Refuse($"The field '{field.Name}' is longer than {Elicitation.MaxStringLength} characters.", field.Name);
        if (field.MinLength is { } min && length < min) throw Refuse($"The field '{field.Name}' is shorter than {min} characters.", field.Name);
        if (field.MaxLength is { } max && length > max) throw Refuse($"The field '{field.Name}' is longer than {max} characters.", field.Name);
        if (field.Options is { } options && !options.Contains(text, StringComparer.Ordinal))
            throw Refuse($"The field '{field.Name}' is not one of the options.", field.Name);
        if (field.Format is { } format && !HasFormat(text, format))
            throw Refuse($"The field '{field.Name}' must be a valid {FormatName(format)}.", field.Name);
    }

    private static void CheckNumber(ElicitationField field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw Refuse($"The field '{field.Name}' must be a number.", field.Name);
        if (field.Type == ElicitationFieldType.Integer && (number != Math.Floor(number) || Math.Abs(number) > MaxExactInteger))
            throw Refuse($"The field '{field.Name}' must be a whole number.", field.Name);
        if (field.Minimum is { } min && number < min) throw Refuse($"The field '{field.Name}' is below the minimum.", field.Name);
        if (field.Maximum is { } max && number > max) throw Refuse($"The field '{field.Name}' is above the maximum.", field.Name);
    }

    private static string FormatName(ElicitationFormat format) =>
        format switch { ElicitationFormat.Email => "email address", ElicitationFormat.Date => "date", _ => "date and time" };

    private static bool HasFormat(string text, ElicitationFormat format) => format switch
    {
        ElicitationFormat.Email => IsEmail(text),
        ElicitationFormat.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
    };

    /// <summary>One <c>@</c>, text on both sides, a dot in the domain, no white space or control characters. Not a full RFC 5322 check.</summary>
    private static bool IsEmail(string text)
    {
        if (text.Length > 254) return false;
        foreach (var c in text)
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return false;
        var at = text.IndexOf('@');
        if (at < 1 || at != text.LastIndexOf('@')) return false;
        var domain = text[(at + 1)..];
        var dot = domain.LastIndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }

    private static A2AException Refuse(string text, string? field = null)
    {
        var metadata = new Dictionary<string, string> { ["extension"] = Elicitation.ExtensionUri };
        if (field is not null) metadata["field"] = field;
        return new A2AException(A2AErrorCode.InvalidParams, text) { Metadata = metadata };
    }

    /// <summary>The form as the value of the metadata key.</summary>
    internal JsonElement ToMetadata() => Elicitation.Build(w =>
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteStartObject("requestedSchema");
        w.WriteString("type", "object");
        w.WriteStartObject("properties");
        foreach (var field in Fields)
        {
            w.WritePropertyName(field.Name);
            field.WriteTo(w);
        }
        w.WriteEndObject();
        if (Fields.Any(f => f.Required))
        {
            w.WriteStartArray("required");
            foreach (var field in Fields.Where(f => f.Required)) w.WriteStringValue(field.Name);
            w.WriteEndArray();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    });
}
