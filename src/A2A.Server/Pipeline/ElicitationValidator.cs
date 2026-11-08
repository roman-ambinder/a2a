using System.Text.Json;
using A2A.Elicitations;
using A2A.Server.Agents;

namespace A2A.Server.Pipeline;

/// <summary>Checks the answer to a form before the agent runs. Refusals are InvalidParams that name the extension, and never repeat a value.</summary>
internal static class ElicitationValidator
{
    /// <summary>
    /// True when the message carries an answer key and the caller activated the extension (the card declares it). Without
    /// activation the key is ignored. A message without the key costs one dictionary lookup.
    /// </summary>
    public static async ValueTask<bool> IsAnswerAsync(Message message, ServerCallContext call, RequestCards requestCards, CancellationToken ct)
    {
        if (message.Metadata is not { } metadata || !metadata.ContainsKey(Elicitation.MetadataKey)) return false;
        // Resolve first: with a card per tenant, this is what fills ActivatedExtensions. A tenant with no agent is refused elsewhere.
        if (await requestCards.ResolveAsync(call, ct).ConfigureAwait(false) is null) return false;
        return call.ActivatedExtensions.Contains(Elicitation.ExtensionUri);
    }

    /// <summary>
    /// Checks <paramref name="message"/> against the form pending in <paramref name="task"/>. The pending form is the one in the
    /// status message of a task in InputRequired that was not answered yet (<see cref="Elicitation.AnsweredKey"/>). A status
    /// message without an id fails closed, as if it were used. <paramref name="answered"/> is the status message id to record
    /// in the same update as the answer.
    /// </summary>
    public static ElicitationAnswer Resolve(AgentTask task, Message message, out string answered)
    {
        var status = task.Status.Message;
        var form = task.GetElicitationForm();
        if (form is null || status is null || string.IsNullOrEmpty(status.MessageId)
            || (task.Metadata is { } taskMetadata && taskMetadata.TryGetValue(Elicitation.AnsweredKey, out var used)
                && used.ValueKind == JsonValueKind.String && string.Equals(used.GetString(), status.MessageId, StringComparison.Ordinal)))
            throw Refuse("The task has no form waiting for an answer.");

        var answer = message.GetElicitationAnswer()
            ?? throw Refuse("The answer is not well formed. It needs an id, an action (accept, decline or cancel), and content only for accept.");

        if (!string.Equals(answer.FormId, form.Id, StringComparison.Ordinal)) throw Refuse("The answer is for another form.");
        if (answer.Action == ElicitationAction.Accept) form.Validate(answer.Content); // names the field and the rule, never the value
        answered = status.MessageId;
        return answer;
    }

    public static A2AException Refuse(string text) => new(A2AErrorCode.InvalidParams, text)
    {
        Metadata = new() { ["extension"] = Elicitation.ExtensionUri },
    };
}
