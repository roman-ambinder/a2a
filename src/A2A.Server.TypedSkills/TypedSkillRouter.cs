using A2A.Skills;

namespace A2A.Server.TypedSkills;

/// <summary>
///     The executor of a host with typed skills: runs the typed skill a request names, and hands everything else to
///     the fallback.
/// </summary>
internal sealed class TypedSkillRouter(TypedSkillSet set, IServiceProvider services) : IAgentExecutor
{
    public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken)
    {
        if (set.Resolve(context.Message, context.Call) is { } skill)
            return skill.RunAsync(context, updater, services, cancellationToken);
        return set.CreateFallback(services)!.ExecuteAsync(context, updater, cancellationToken);
    }

    public Task CancelAsync(AgentTask task, TaskUpdater updater, CancellationToken cancellationToken)
    {
        // A task started by a typed skill has its skill id in its first message. Everything else belongs to the fallback.
        var first = task.History is { Count: > 0 } history ? history[0] : null;
        var typed = first?.GetRequestedSkillId() is { } id && set.Has(id);
        return !typed && set.CreateFallback(services) is { } fallback
            ? fallback.CancelAsync(task, updater, cancellationToken)
            : updater.CancelAsync(cancellationToken: cancellationToken).AsTask();
    }
}