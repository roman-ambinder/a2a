namespace A2A;

/// <summary>Protocol constants: version, method names, headers, paths, media types.</summary>
public static class A2AProtocol
{
    /// <summary>Protocol version implemented by this SDK (Major.Minor).</summary>
    public const string Version = "1.0";

    /// <summary>Version the spec says to assume when the A2A-Version header is absent (§3.6.2).</summary>
    public const string ImplicitVersion = "0.3";

    public const string WellKnownAgentCardPath = "/.well-known/agent-card.json";

    public const string MediaType = "application/a2a+json";
    public const string JsonMediaType = "application/json";
    public const string EventStreamMediaType = "text/event-stream";

    public const string ErrorDomain = "a2a-protocol.org";

    public static class Headers
    {
        public const string Version = "A2A-Version";
        public const string Extensions = "A2A-Extensions";

        /// <summary>Webhook header carrying <see cref="TaskPushNotificationConfig.Token"/>.</summary>
        public const string NotificationToken = "X-A2A-Notification-Token";

        /// <summary>SDK extension: monotonically increasing per-task sequence number on push notifications, for dedupe and ordering.</summary>
        public const string NotificationSequence = "X-A2A-Notification-Sequence";
    }

    public static class Methods
    {
        public const string SendMessage = "SendMessage";
        public const string SendStreamingMessage = "SendStreamingMessage";
        public const string GetTask = "GetTask";
        public const string ListTasks = "ListTasks";
        public const string CancelTask = "CancelTask";
        public const string SubscribeToTask = "SubscribeToTask";
        public const string CreateTaskPushNotificationConfig = "CreateTaskPushNotificationConfig";
        public const string GetTaskPushNotificationConfig = "GetTaskPushNotificationConfig";
        public const string ListTaskPushNotificationConfigs = "ListTaskPushNotificationConfigs";
        public const string DeleteTaskPushNotificationConfig = "DeleteTaskPushNotificationConfig";
        public const string GetExtendedAgentCard = "GetExtendedAgentCard";
    }
}
