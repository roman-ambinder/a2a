using System.Text.Json;
using A2A.Text;

namespace A2A.Elicitations;

/// <summary>One field of an <see cref="ElicitationForm" />. Make one with a factory method such as <see cref="Text" />.</summary>
public sealed class ElicitationField
{
    private ElicitationField(string name, ElicitationFieldType type, string? title, string? description, bool required,
        int? minLength, int? maxLength, double? minimum, double? maximum, ElicitationFormat? format,
        IReadOnlyList<string>? options)
    {
        Name = name;
        Type = type;
        Title = title;
        Description = description;
        Required = required;
        MinLength = minLength;
        MaxLength = maxLength;
        Minimum = minimum;
        Maximum = maximum;
        Format = format;
        Options = options;
        LooksSensitive = ElicitationSensitivity.Looks(name, title, description);
    }

    /// <summary>The field name. It is the key of the value in the answer.</summary>
    public string Name { get; }

    /// <summary>The type of the value.</summary>
    public ElicitationFieldType Type { get; }

    /// <summary>A short label for people. Null when not set.</summary>
    public string? Title { get; }

    /// <summary>Longer help text for people. Null when not set.</summary>
    public string? Description { get; }

    /// <summary>True when the answer must have a value for this field.</summary>
    public bool Required { get; }

    /// <summary>The least length of a text value, in code points. Null for no limit.</summary>
    public int? MinLength { get; }

    /// <summary>
    ///     The most length of a text value, in code points. Null for no limit (<see cref="Elicitation.MaxStringLength" />
    ///     still applies).
    /// </summary>
    public int? MaxLength { get; }

    /// <summary>The least value of a number, inclusive. Null for no limit.</summary>
    public double? Minimum { get; }

    /// <summary>The most value of a number, inclusive. Null for no limit.</summary>
    public double? Maximum { get; }

    /// <summary>The format a text value must have. Null for any text.</summary>
    public ElicitationFormat? Format { get; }

    /// <summary>The allowed text values of a choice field. Null for any text.</summary>
    public IReadOnlyList<string>? Options { get; }

    /// <summary>
    ///     True when the name, title or description looks like a request for a secret (a password, a token, a card number).
    ///     A client refuses to send a value for such a field unless the caller opts in.
    /// </summary>
    public bool LooksSensitive { get; }

    /// <summary>A text field.</summary>
    /// <param name="name">
    ///     The field name, <c>[A-Za-z_][A-Za-z0-9_-]*</c>, up to <see cref="Elicitation.MaxNameLength" />
    ///     characters.
    /// </param>
    /// <param name="title">A short label, or null.</param>
    /// <param name="description">Help text, or null.</param>
    /// <param name="required">True when the answer must have a value.</param>
    /// <param name="minLength">Least length in code points, or null.</param>
    /// <param name="maxLength">Most length in code points, or null. At most <see cref="Elicitation.MaxStringLength" />.</param>
    /// <param name="format">The format the text must have, or null.</param>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static ElicitationField Text(string name, string? title = null, string? description = null,
        bool required = false,
        int? minLength = null, int? maxLength = null, ElicitationFormat? format = null)
    {
        return Create(name, ElicitationFieldType.String, title, description, required, minLength, maxLength, null, null,
            format, null);
    }

    /// <summary>A whole-number field.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="title">A short label, or null.</param>
    /// <param name="description">Help text, or null.</param>
    /// <param name="required">True when the answer must have a value.</param>
    /// <param name="minimum">Least value, inclusive, or null.</param>
    /// <param name="maximum">Most value, inclusive, or null.</param>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static ElicitationField Integer(string name, string? title = null, string? description = null,
        bool required = false,
        double? minimum = null, double? maximum = null)
    {
        return Create(name, ElicitationFieldType.Integer, title, description, required, null, null, minimum, maximum,
            null, null);
    }

    /// <summary>A number field.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="title">A short label, or null.</param>
    /// <param name="description">Help text, or null.</param>
    /// <param name="required">True when the answer must have a value.</param>
    /// <param name="minimum">Least value, inclusive, or null.</param>
    /// <param name="maximum">Most value, inclusive, or null.</param>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static ElicitationField Number(string name, string? title = null, string? description = null,
        bool required = false,
        double? minimum = null, double? maximum = null)
    {
        return Create(name, ElicitationFieldType.Number, title, description, required, null, null, minimum, maximum,
            null, null);
    }

    /// <summary>A true-or-false field.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="title">A short label, or null.</param>
    /// <param name="description">Help text, or null.</param>
    /// <param name="required">True when the answer must have a value.</param>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static ElicitationField Boolean(string name, string? title = null, string? description = null,
        bool required = false)
    {
        return Create(name, ElicitationFieldType.Boolean, title, description, required, null, null, null, null, null,
            null);
    }

    /// <summary>A text field with a fixed list of allowed values.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="options">
    ///     The allowed values: 1 to <see cref="Elicitation.MaxOptions" />, not empty, not repeated, up to
    ///     <see cref="Elicitation.MaxOptionLength" /> characters, without control or invisible characters.
    /// </param>
    /// <param name="title">A short label, or null.</param>
    /// <param name="description">Help text, or null.</param>
    /// <param name="required">True when the answer must have a value.</param>
    /// <exception cref="ArgumentException">A value is not allowed.</exception>
    public static ElicitationField Choice(string name, IEnumerable<string> options, string? title = null,
        string? description = null,
        bool required = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Create(name, ElicitationFieldType.String, title, description, required, null, null, null, null, null,
            options.ToList());
    }

    /// <summary>The one place that checks a field. The factories and the reader of a peer's form both use it.</summary>
    internal static ElicitationField Create(string name, ElicitationFieldType type, string? title, string? description,
        bool required,
        int? minLength, int? maxLength, double? minimum, double? maximum, ElicitationFormat? format,
        IReadOnlyList<string>? options)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Elicitation.IsName(name))
            throw new ArgumentException(
                $"A field name must match [A-Za-z_][A-Za-z0-9_-]* and have at most {Elicitation.MaxNameLength} characters.",
                nameof(name));
        CheckText(title, Elicitation.MaxTitleLength, nameof(title));
        CheckText(description, Elicitation.MaxDescriptionLength, nameof(description));

        var isString = type == ElicitationFieldType.String;
        var isNumber = type is ElicitationFieldType.Integer or ElicitationFieldType.Number;
        if (!isString && (minLength is not null || maxLength is not null || format is not null || options is not null))
            throw new ArgumentException("Only a text field has a length, format or options.");
        if (!isNumber && (minimum is not null || maximum is not null))
            throw new ArgumentException("Only a number field has a minimum or maximum.");

        if (minLength is < 0 or > Elicitation.MaxStringLength) throw new ArgumentOutOfRangeException(nameof(minLength));
        if (maxLength is < 0 or > Elicitation.MaxStringLength) throw new ArgumentOutOfRangeException(nameof(maxLength));
        if (minLength > maxLength) throw new ArgumentException("minLength is greater than maxLength.");
        if (minimum is { } lo && !double.IsFinite(lo)) throw new ArgumentOutOfRangeException(nameof(minimum));
        if (maximum is { } hi && !double.IsFinite(hi)) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (minimum > maximum) throw new ArgumentException("minimum is greater than maximum.");
        if (format is not null && !Enum.IsDefined(format.Value)) throw new ArgumentOutOfRangeException(nameof(format));

        if (options is not null)
        {
            if (format is not null) throw new ArgumentException("A field with options cannot also have a format.");
            if (options.Count is 0 or > Elicitation.MaxOptions)
                throw new ArgumentException($"A choice needs 1 to {Elicitation.MaxOptions} options.", nameof(options));
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in options)
                if (string.IsNullOrEmpty(option) || option.Length > Elicitation.MaxOptionLength ||
                    !Elicitation.IsValidText(option)
                    || TextSanitizer.Sanitize(option, Elicitation.MaxOptionLength) != option || !seen.Add(option))
                    throw new ArgumentException(
                        "An option must be new, not empty, short, and free of control and invisible characters.",
                        nameof(options));
            options = options.ToArray();
        }

        return new ElicitationField(name, type, title, description, required, minLength, maxLength, minimum, maximum,
            format, options);
    }

    private static void CheckText(string? text, int max, string parameter)
    {
        if (text is null) return;
        if (text.Length > max || !Elicitation.IsValidText(text))
            throw new ArgumentException($"At most {max} characters of valid text.", parameter);
    }

    /// <summary>Writes the field as a JSON Schema property value.</summary>
    internal void WriteTo(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("type", Type switch
        {
            ElicitationFieldType.String => "string",
            ElicitationFieldType.Integer => "integer",
            ElicitationFieldType.Number => "number",
            _ => "boolean"
        });
        if (Title is not null) w.WriteString("title", Title);
        if (Description is not null) w.WriteString("description", Description);
        if (MinLength is { } minLength) w.WriteNumber("minLength", minLength);
        if (MaxLength is { } maxLength) w.WriteNumber("maxLength", maxLength);
        if (Format is { } format)
            w.WriteString("format",
                format switch
                {
                    ElicitationFormat.Email => "email", ElicitationFormat.Date => "date", _ => "date-time"
                });
        if (Options is { } options)
        {
            w.WriteStartArray("enum");
            foreach (var option in options) w.WriteStringValue(option);
            w.WriteEndArray();
        }

        if (Minimum is { } minimum) w.WriteNumber("minimum", minimum);
        if (Maximum is { } maximum) w.WriteNumber("maximum", maximum);
        w.WriteEndObject();
    }
}