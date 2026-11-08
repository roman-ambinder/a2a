namespace A2A.Elicitations;

/// <summary>Read the form and the answer of structured elicitation from messages and tasks.</summary>
public static class ElicitationMessageExtensions
{
    /// <summary>
    /// The form in the message metadata. Null when there is none, or it is not readable. Does not throw on a bad shape. Reading
    /// is strict: a keyword outside the spec makes the form unreadable.
    /// </summary>
    /// <remarks>
    /// The form is peer input. Show the user which agent is asking, and never fill it without the user. A form can have fields
    /// that <see cref="ElicitationField.LooksSensitive"/>: reading does not refuse them.
    /// </remarks>
    /// <param name="message">The status message of the task.</param>
    public static ElicitationForm? GetElicitationForm(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Metadata is { } metadata && metadata.TryGetValue(Elicitation.MetadataKey, out var value) ? ElicitationReader.ReadForm(value) : null;
    }

    /// <summary>The form in the status message of <paramref name="task"/>, only while the task is in <see cref="TaskState.InputRequired"/>. Null otherwise, or when it is not readable.</summary>
    /// <param name="task">The task.</param>
    public static ElicitationForm? GetElicitationForm(this AgentTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task.Status.State == TaskState.InputRequired ? task.Status.Message?.GetElicitationForm() : null;
    }

    /// <summary>The answer in the message metadata. Null when there is none, or it is not readable. Does not check the values against a form.</summary>
    /// <param name="message">The user message.</param>
    public static ElicitationAnswer? GetElicitationAnswer(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.Metadata is { } metadata && metadata.TryGetValue(Elicitation.MetadataKey, out var value) ? ElicitationReader.ReadAnswer(value) : null;
    }

    /// <summary>Writes <paramref name="form"/> to the message metadata (created when null) and returns the same message.</summary>
    /// <param name="message">The status message.</param>
    /// <param name="form">The form to ask.</param>
    public static Message SetElicitationForm(this Message message, ElicitationForm form)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(form);
        message.Metadata ??= [];
        message.Metadata[Elicitation.MetadataKey] = form.ToMetadata();
        return message;
    }
}
