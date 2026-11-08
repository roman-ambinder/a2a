using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace A2A.Elicitations;

/// <summary>The values for <see cref="ElicitationAnswer.Accept"/>. Add them with a collection initializer.</summary>
/// <example><c>new ElicitationValues { { "city", "Oslo" }, { "nights", 2 } }</c></example>
public sealed class ElicitationValues : IEnumerable<KeyValuePair<string, JsonElement>>
{
    private readonly Dictionary<string, JsonElement> _values = new(StringComparer.Ordinal);

    /// <summary>Adds a text value.</summary>
    /// <param name="name">The field name. Used once.</param>
    /// <param name="value">The text.</param>
    /// <exception cref="ArgumentException">The name is empty or used already, or the text is not valid UTF-16.</exception>
    public void Add(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!Elicitation.IsValidText(value)) throw new ArgumentException("The text is not valid UTF-16.", nameof(value));
        Put(name, Elicitation.Build(w => w.WriteStringValue(value)));
    }

    /// <summary>Adds a whole-number value.</summary>
    /// <param name="name">The field name. Used once.</param>
    /// <param name="value">The number.</param>
    /// <exception cref="ArgumentException">The name is empty or used already.</exception>
    public void Add(string name, long value) => Put(name, Elicitation.Build(w => w.WriteNumberValue(value)));

    /// <summary>Adds a number value.</summary>
    /// <param name="name">The field name. Used once.</param>
    /// <param name="value">The number. Not NaN or infinity.</param>
    /// <exception cref="ArgumentException">The name is empty or used already, or the number is not finite.</exception>
    public void Add(string name, double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentException("The number is not finite.", nameof(value));
        Put(name, Elicitation.Build(w => w.WriteNumberValue(value)));
    }

    /// <summary>Adds a true-or-false value.</summary>
    /// <param name="name">The field name. Used once.</param>
    /// <param name="value">The value.</param>
    /// <exception cref="ArgumentException">The name is empty or used already.</exception>
    public void Add(string name, bool value) => Put(name, Elicitation.Build(w => w.WriteBooleanValue(value)));

    private void Put(string name, JsonElement value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_values.TryAdd(name, value)) throw new ArgumentException($"A value for '{name}' was added twice.", nameof(name));
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>What the user did with a form: accepted it with values, declined it, or cancelled it.</summary>
/// <remarks>The values are what the user typed. Treat them as untrusted input, and do not use them for authorization.</remarks>
public sealed class ElicitationAnswer
{
    private const double MaxExactInteger = 9007199254740992; // 2^53
    private static readonly IReadOnlyDictionary<string, JsonElement> NoContent = new ReadOnlyDictionary<string, JsonElement>(new Dictionary<string, JsonElement>());

    private ElicitationAnswer(string formId, ElicitationAction action, IReadOnlyDictionary<string, JsonElement> content)
    {
        FormId = formId;
        Action = action;
        Content = content;
    }

    /// <summary>The id of the form this answers.</summary>
    public string FormId { get; }

    /// <summary>What the user did.</summary>
    public ElicitationAction Action { get; }

    /// <summary>The values by field name. Empty unless <see cref="Action"/> is <see cref="ElicitationAction.Accept"/>.</summary>
    public IReadOnlyDictionary<string, JsonElement> Content { get; }

    internal static ElicitationAnswer Create(string formId, ElicitationAction action, Dictionary<string, JsonElement>? content) =>
        new(formId, action, content is null ? NoContent : new ReadOnlyDictionary<string, JsonElement>(content));

    /// <summary>The text value of <paramref name="name"/>. Null when absent or not text.</summary>
    /// <param name="name">The field name.</param>
    public string? GetString(string name)
    {
        if (!Content.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        try { return value.GetString(); }
        catch (InvalidOperationException) { return null; } // not valid UTF-16
    }

    /// <summary>The whole-number value of <paramref name="name"/>. Reads <c>3.0</c> too, because gRPC sends every number as a double. Null when absent or not a whole number.</summary>
    /// <param name="name">The field name.</param>
    public long? GetInt64(string name)
    {
        if (!Content.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetInt64(out var whole)) return whole;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number != Math.Floor(number) || Math.Abs(number) > MaxExactInteger) return null;
        return (long)number;
    }

    /// <summary>The number value of <paramref name="name"/>. Null when absent or not a number.</summary>
    /// <param name="name">The field name.</param>
    public double? GetDouble(string name)
    {
        if (!Content.TryGetValue(name, out var value) || value.ValueKind != JsonValueKind.Number) return null;
        return value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;
    }

    /// <summary>The true-or-false value of <paramref name="name"/>. Null when absent or not true or false.</summary>
    /// <param name="name">The field name.</param>
    public bool? GetBoolean(string name) =>
        Content.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    /// <summary>Accepts <paramref name="form"/> with <paramref name="values"/>. The values are checked against the form first.</summary>
    /// <param name="form">The form to answer.</param>
    /// <param name="values">The values by field name.</param>
    /// <param name="allowSensitive">
    /// False (the default) refuses a value for a field that <see cref="ElicitationField.LooksSensitive"/>. An omitted field is fine.
    /// Set it to true only when the user knowingly sends it: answers are stored in the task history and sent to webhooks.
    /// </param>
    /// <exception cref="A2AException">InvalidParams: a value breaks a rule of the form, or is for a sensitive field.</exception>
    public static ElicitationAnswer Accept(ElicitationForm form, ElicitationValues values, bool allowSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(values);
        var content = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (name, value) in values) content[name] = value;
        form.Validate(content);
        if (!allowSensitive)
        {
            foreach (var field in form.Fields)
            {
                if (!field.LooksSensitive || !content.ContainsKey(field.Name)) continue;
                throw new A2AException(A2AErrorCode.InvalidParams,
                    $"The field '{field.Name}' looks like a request for a secret. Do not send it, or pass allowSensitive: true if the user chose to.")
                {
                    Metadata = new() { ["extension"] = Elicitation.ExtensionUri, ["field"] = field.Name },
                };
            }
        }
        return Create(form.Id, ElicitationAction.Accept, content);
    }

    /// <summary>Declines <paramref name="form"/>: the user refuses this request.</summary>
    /// <param name="form">The form to answer.</param>
    public static ElicitationAnswer Decline(ElicitationForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Create(form.Id, ElicitationAction.Decline, null);
    }

    /// <summary>Cancels <paramref name="form"/>: the user dismissed the request without a decision.</summary>
    /// <param name="form">The form to answer.</param>
    public static ElicitationAnswer Cancel(ElicitationForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return Create(form.Id, ElicitationAction.Cancel, null);
    }

    /// <summary>
    /// Builds the user message that carries this answer. Send it with <c>UseElicitation()</c> options.
    /// </summary>
    /// <param name="taskId">The id of the task that waits for the answer.</param>
    /// <param name="contextId">The context id, or null.</param>
    /// <param name="text">
    /// The text part, for a plain peer. Default: a sentence that names the action only. It never repeats the values. If you pass your own text, do not put values in it.
    /// </param>
    public Message ToMessage(string taskId, string? contextId = null, string? text = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(taskId);
        var message = Message.FromUser(text ?? DefaultText(Action), contextId, taskId);
        message.Metadata = new()
        {
            [Elicitation.MetadataKey] = Elicitation.Build(w =>
            {
                w.WriteStartObject();
                w.WriteString("id", FormId);
                w.WriteString("action", Action switch { ElicitationAction.Accept => "accept", ElicitationAction.Decline => "decline", _ => "cancel" });
                if (Action == ElicitationAction.Accept)
                {
                    w.WriteStartObject("content");
                    foreach (var (name, value) in Content)
                    {
                        w.WritePropertyName(name);
                        value.WriteTo(w);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }),
        };
        return message;
    }

    private static string DefaultText(ElicitationAction action) => action switch
    {
        ElicitationAction.Accept => "I answered the form.",
        ElicitationAction.Decline => "I declined the form.",
        _ => "I cancelled the form.",
    };
}
