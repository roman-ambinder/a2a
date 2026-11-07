namespace A2A.Server;

/// <summary>The A2A operations an <see cref="A2AAuthorizationHandler"/> is asked about. Same set on every binding.</summary>
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
}
