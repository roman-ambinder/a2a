using System.Diagnostics.CodeAnalysis;

namespace A2A.Server.TypedSkills;

/// <summary>
///     What a typed skill handler gets: the checked input and the call. It has no <see cref="TaskUpdater" />; the SDK
///     starts and completes the task.
/// </summary>
/// <typeparam name="TInput">The input type of the skill.</typeparam>
[Experimental("A2AEXP001",
    UrlFormat = "https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/versioning.md#{0}")]
public sealed class TypedSkillContext<TInput>
{
    private readonly TaskUpdater _updater;

    internal TypedSkillContext(TInput input, AgentRequestContext request, IServiceProvider services,
        TaskUpdater updater)
    {
        Input = input;
        Request = request;
        Services = services;
        _updater = updater;
    }

    /// <summary>The input. It was refused before this point if it did not fit the type or failed the validator.</summary>
    public TInput Input { get; }

    /// <summary>The request: caller, tenant, message and the rest.</summary>
    public AgentRequestContext Request { get; }

    /// <summary>The services of this execution (a scope that ends with it).</summary>
    public IServiceProvider Services { get; }

    /// <summary>Sends a short status text to anyone who follows the task. The task stays working.</summary>
    /// <param name="text">The text. It reaches the caller.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public ValueTask ReportProgressAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _updater.UpdateStatusAsync(TaskState.Working, _updater.NewAgentMessage(text),
            cancellationToken: cancellationToken);
    }
}