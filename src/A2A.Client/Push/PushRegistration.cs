namespace A2A.Client.Push;

/// <summary>
/// The result of <see cref="A2AClient.RegisterPushAsync"/>: the stored webhook config, and the task as it was read right after registering.
/// </summary>
public sealed class PushRegistration
{
    /// <summary>Creates a registration result.</summary>
    public PushRegistration(TaskPushNotificationConfig config, AgentTask task)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Task = task ?? throw new ArgumentNullException(nameof(task));
    }

    /// <summary>The config the server stored (this SDK's server blanks the secrets).</summary>
    public TaskPushNotificationConfig Config { get; }

    /// <summary>The task, read after the webhook was registered.</summary>
    public AgentTask Task { get; }

    /// <summary>
    /// True when the task is terminal or interrupted (see <see cref="A2AClient.WaitForTaskAsync"/>): no further notification is
    /// expected, so do not wait for a callback. Servers of other SDKs send no catch-up delivery for a task that already finished.
    /// </summary>
    public bool IsFinal => Task.Status.State.IsTerminal() || Task.Status.State.IsInterrupted();
}
