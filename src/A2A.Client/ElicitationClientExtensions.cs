using A2A.Elicitations;

namespace A2A.Client;

/// <summary>Client helpers for structured elicitation: activate the extension and answer a form.</summary>
public static class ElicitationClientExtensions
{
    /// <summary>
    ///     Adds the elicitation extension to <paramref name="options" />, once. Use it on the call that may get a form and on
    ///     the call
    ///     that answers it. A plain A2A peer ignores it.
    /// </summary>
    /// <param name="options">The options for the call. They are changed and returned.</param>
    /// <returns>The same <paramref name="options" />.</returns>
    public static A2ARequestOptions UseElicitation(this A2ARequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Extensions is null or { IsReadOnly: true })
            options.Extensions = new List<string>(options.Extensions ?? []);
        if (!options.Extensions.Contains(Elicitation.ExtensionUri)) options.Extensions.Add(Elicitation.ExtensionUri);
        return options;
    }

    /// <summary>
    ///     Sends <paramref name="answer" /> to the form that <paramref name="task" /> waits on, with the extension
    ///     activated. For streaming, use <see cref="ElicitationAnswer.ToMessage" /> and <see cref="UseElicitation" />.
    /// </summary>
    /// <param name="client">The client of the agent that asked.</param>
    /// <param name="task">The task in <see cref="TaskState.InputRequired" /> whose status message has the form.</param>
    /// <param name="answer">
    ///     The answer, made with <see cref="ElicitationAnswer.Accept" />,
    ///     <see cref="ElicitationAnswer.Decline" /> or <see cref="ElicitationAnswer.Cancel" />.
    /// </param>
    /// <param name="options">Options for the call, or null for new ones. These options are changed.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="A2AException">
    ///     InvalidParams: the task has no readable pending form, or the answer is for another form.
    ///     Nothing is sent.
    /// </exception>
    public static Task<SendMessageResponse> AnswerElicitationAsync(this IA2AClient client, AgentTask task,
        ElicitationAnswer answer,
        A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(answer);
        var form = task.GetElicitationForm() ??
                   throw A2AException.InvalidParams("The task has no readable form waiting for an answer.");
        if (!string.Equals(form.Id, answer.FormId, StringComparison.Ordinal))
            throw A2AException.InvalidParams("The answer is for another form than the one the task waits on.");
        var request = new SendMessageRequest { Message = answer.ToMessage(task.Id, task.ContextId) };
        return client.SendMessageAsync(request, (options ?? new A2ARequestOptions()).UseElicitation(),
            cancellationToken);
    }
}