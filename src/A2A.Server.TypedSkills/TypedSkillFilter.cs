using A2A.Server.Authorization;
using A2A.Server.Pipeline;

namespace A2A.Server.TypedSkills;

/// <summary>
///     Refuses a send that goes to a typed skill but does not fit it, before anything is stored and before the agent
///     runs.
/// </summary>
internal sealed class TypedSkillFilter(TypedSkillSet set) : IA2AOperationFilter
{
    public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next,
        CancellationToken cancellationToken)
    {
        // A missing message is not ours to judge: the core refuses it.
        if (context.Operation.Kind is A2AOperation.SendMessage or A2AOperation.SendStreamingMessage
            && request is SendMessageRequest { Message: { } message }
            && set.Resolve(message, context.Call) is { } skill
            && skill.Check(message) is { } violations)
            throw TypedSkillRefusal.Refuse("The input does not fit the skill.", violations);
        return next(context, request, cancellationToken);
    }
}