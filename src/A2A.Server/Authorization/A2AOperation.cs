namespace A2A.Server.Authorization;

/// <summary>
///     The kinds of operation an <see cref="A2AAuthorizationHandler" /> is asked about: the 11 built-in operations,
///     the same set on every binding, and <see cref="Extension" />.
/// </summary>
public enum A2AOperation
{
    /// <summary>SendMessage (spec §3.1.1).</summary>
    SendMessage,

    /// <summary>SendStreamingMessage (spec §3.1.2).</summary>
    SendStreamingMessage,

    /// <summary>GetTask (spec §3.1.3).</summary>
    GetTask,

    /// <summary>ListTasks (spec §3.1.4).</summary>
    ListTasks,

    /// <summary>CancelTask (spec §3.1.5).</summary>
    CancelTask,

    /// <summary>SubscribeToTask (spec §3.1.6).</summary>
    SubscribeToTask,

    /// <summary>CreateTaskPushNotificationConfig (spec §3.1.7).</summary>
    CreateTaskPushNotificationConfig,

    /// <summary>GetTaskPushNotificationConfig (spec §3.1.8).</summary>
    GetTaskPushNotificationConfig,

    /// <summary>ListTaskPushNotificationConfigs (spec §3.1.9).</summary>
    ListTaskPushNotificationConfigs,

    /// <summary>DeleteTaskPushNotificationConfig (spec §3.1.10).</summary>
    DeleteTaskPushNotificationConfig,

    /// <summary>GetExtendedAgentCard (spec §3.1.11).</summary>
    GetExtendedAgentCard,

    /// <summary>
    ///     An extension operation a host registered; <see cref="Pipeline.A2AOperationDescriptor.Name" /> says which. Always
    ///     the
    ///     last value: the built-in operations are the values before it.
    /// </summary>
    Extension
}