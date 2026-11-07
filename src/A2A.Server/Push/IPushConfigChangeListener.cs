using A2A.Server.Storage;

namespace A2A.Server.Push;

/// <summary>
/// Told when a task's push configs change through <see cref="PushConfigService"/> (create, delete, inline save), so a component
/// that caches the configs of a task (such as <see cref="HttpPushNotificationSender"/>) can list them again.
/// </summary>
public interface IPushConfigChangeListener
{
    /// <summary>Called after a push config of <paramref name="key"/> was stored or deleted. Must return quickly and must not throw.</summary>
    void ConfigChanged(TaskKey key);
}

/// <summary>Listener used when the registered <see cref="IPushNotificationSender"/> does not listen for config changes.</summary>
internal sealed class NoOpPushConfigChangeListener : IPushConfigChangeListener
{
    public static readonly NoOpPushConfigChangeListener Instance = new();

    private NoOpPushConfigChangeListener() { }

    public void ConfigChanged(TaskKey key) { }
}
