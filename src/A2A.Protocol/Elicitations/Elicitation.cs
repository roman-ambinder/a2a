using System.Buffers;
using System.Text.Json;

namespace A2A.Elicitations;

/// <summary>
///     Structured elicitation: an agent that waits in <c>TASK_STATE_INPUT_REQUIRED</c> can ask for a small typed form. A
///     client that
///     activated the extension reads the form and answers with typed values, or declines, or cancels. A plain peer sees
///     the text
///     question only.
/// </summary>
/// <remarks>
///     Do not collect secrets with a form. Answers are stored in the task history and sent to webhooks with the task.
/// </remarks>
public static class Elicitation
{
    /// <summary>URI of the optional elicitation extension.</summary>
    public const string ExtensionUri = "https://github.com/roman-ambinder/a2a/extensions/elicitation/v1";

    /// <summary>Metadata key of the form (on a status message) and of the answer (on a user message). It is the extension URI.</summary>
    public const string MetadataKey = ExtensionUri;

    /// <summary>Task metadata key that holds the id of the status message whose form was answered.</summary>
    public const string AnsweredKey = "a2a.elicitation.answered";

    /// <summary>Most fields in one form.</summary>
    public const int MaxFields = 32;

    /// <summary>Longest field name, in characters.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Longest field title, in characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Longest field description, in characters.</summary>
    public const int MaxDescriptionLength = 1000;

    /// <summary>Longest form id, in characters.</summary>
    public const int MaxFormIdLength = 128;

    /// <summary>Most options of a choice field.</summary>
    public const int MaxOptions = 64;

    /// <summary>Longest option value, in characters.</summary>
    public const int MaxOptionLength = 256;

    /// <summary>Longest text answer, in code points. A field's <c>maxLength</c> may not exceed it.</summary>
    public const int MaxStringLength = 4096;

    /// <summary>Creates the optional extension entry for an agent card. Clients need not support it.</summary>
    public static AgentExtension CreateExtension()
    {
        return new AgentExtension
        {
            Uri = ExtensionUri,
            Required = false,
            Description = "Lets an agent ask for a small typed form when it needs input."
        };
    }

    /// <summary>Names match <c>[A-Za-z_][A-Za-z0-9_-]*</c>, up to <see cref="MaxNameLength" /> characters.</summary>
    internal static bool IsName(string text)
    {
        if (text.Length is 0 or > MaxNameLength) return false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var letter = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_';
            if (!letter && (i == 0 || !(c is >= '0' and <= '9' or '-'))) return false;
        }

        return true;
    }

    /// <summary>Form ids match <c>[A-Za-z0-9._~-]+</c>, up to <see cref="MaxFormIdLength" /> characters.</summary>
    internal static bool IsFormId(string text)
    {
        if (text.Length is 0 or > MaxFormIdLength) return false;
        foreach (var c in text)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '~' or '-'))
                return false;
        return true;
    }

    /// <summary>False for text with a lone surrogate, which JSON cannot carry.</summary>
    internal static bool IsValidText(string text)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else if (char.IsSurrogate(text[i])) return false;
        return true;
    }

    /// <summary>Counts code points, as JSON Schema counts string length.</summary>
    internal static int CodePoints(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            count++;
        }

        return count;
    }

    /// <summary>Writes JSON with <paramref name="write" /> and returns it as a detached element. No reflection serialization.</summary>
    internal static JsonElement Build(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        using var doc = JsonDocument.Parse(buffer.WrittenMemory);
        return doc.RootElement.Clone();
    }
}

/// <summary>The type of a form field.</summary>
public enum ElicitationFieldType
{
    /// <summary>Text.</summary>
    String,

    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>Any finite number.</summary>
    Number,

    /// <summary>True or false.</summary>
    Boolean
}

/// <summary>The format a text field must have.</summary>
public enum ElicitationFormat
{
    /// <summary>An email address.</summary>
    Email,

    /// <summary>A date, <c>yyyy-MM-dd</c>.</summary>
    Date,

    /// <summary>A date and time with a zone, as in RFC 3339.</summary>
    DateTime
}

/// <summary>What the user did with a form.</summary>
public enum ElicitationAction
{
    /// <summary>The user sent values.</summary>
    Accept,

    /// <summary>The user refuses this request.</summary>
    Decline,

    /// <summary>The user dismissed the request without a decision.</summary>
    Cancel
}