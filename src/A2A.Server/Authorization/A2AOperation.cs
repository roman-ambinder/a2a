namespace A2A.Server;

/// <summary>The A2A operations an <see cref="A2AAuthorizationHandler"/> is asked about. Same set on every binding.</summary>
public enum A2AOperation
{
    SendMessage,
    SendStreamingMessage,
    GetTask,
    ListTasks,
    CancelTask,
    SubscribeToTask,
    CreateTaskPushNotificationConfig,
    GetTaskPushNotificationConfig,
    ListTaskPushNotificationConfigs,
    DeleteTaskPushNotificationConfig,
    GetExtendedAgentCard,
}
