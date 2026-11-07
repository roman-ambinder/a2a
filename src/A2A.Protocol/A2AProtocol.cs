namespace A2A;

/// <summary>Protocol constants: version, method names, headers, paths, media types.</summary>
public static class A2AProtocol
{
    /// <summary>Protocol version implemented by this SDK (Major.Minor).</summary>
    public const string Version = "1.0";

    /// <summary>Version the spec says to assume when the A2A-Version header is absent (§3.6.2).</summary>
    public const string ImplicitVersion = "0.3";

    /// <summary>Path where an agent publishes its Agent Card, relative to its origin.</summary>
    public const string WellKnownAgentCardPath = "/.well-known/agent-card.json";

    /// <summary>Media type of A2A payloads on the HTTP+JSON and JSON-RPC bindings.</summary>
    public const string MediaType = "application/a2a+json";
    /// <summary>Plain JSON media type. Accepted on requests and responses as an alternative to <see cref="MediaType"/>.</summary>
    public const string JsonMediaType = "application/json";
    /// <summary>Media type of server-sent event streams.</summary>
    public const string EventStreamMediaType = "text/event-stream";

    /// <summary>Domain of the <c>google.rpc.ErrorInfo</c> detail attached to A2A errors.</summary>
    public const string ErrorDomain = "a2a-protocol.org";

    /// <summary>HTTP header names used by the protocol and by SDK extensions.</summary>
    public static class Headers
    {
        /// <summary>Request header carrying the protocol version the client speaks.</summary>
        public const string Version = "A2A-Version";
        /// <summary>Header listing the extension URIs the client wants active for a request.</summary>
        public const string Extensions = "A2A-Extensions";

        /// <summary>SDK extension: lets a server recognize a retried SendMessage. Defaults to the message id.</summary>
        public const string IdempotencyKey = "Idempotency-Key";

        /// <summary>Webhook header carrying a per-delivery id, identical across retries of one delivery. Lets receivers deduplicate.</summary>
        public const string DeliveryId = "X-A2A-Delivery-Id";

        /// <summary>Standard SSE resume header. Value is the last <c>id:</c> the client received (<c>{taskId}:{sequence}</c>).</summary>
        public const string LastEventId = "Last-Event-ID";

        /// <summary>Webhook header carrying <see cref="TaskPushNotificationConfig.Token"/>.</summary>
        public const string NotificationToken = "X-A2A-Notification-Token";

        /// <summary>SDK extension: monotonically increasing per-task sequence number on push notifications, for dedupe and ordering.</summary>
        public const string NotificationSequence = "X-A2A-Notification-Sequence";
    }

    /// <summary>Operation names. They double as JSON-RPC method names.</summary>
    public static class Methods
    {
        /// <summary>SendMessage: sends a message and waits for the result (spec §3.1.1).</summary>
        public const string SendMessage = "SendMessage";
        /// <summary>SendStreamingMessage: sends a message and streams updates (spec §3.1.2).</summary>
        public const string SendStreamingMessage = "SendStreamingMessage";
        /// <summary>GetTask: fetches one task (spec §3.1.3).</summary>
        public const string GetTask = "GetTask";
        /// <summary>ListTasks: lists tasks with filters and paging (spec §3.1.4).</summary>
        public const string ListTasks = "ListTasks";
        /// <summary>CancelTask: asks the agent to cancel a task (spec §3.1.5).</summary>
        public const string CancelTask = "CancelTask";
        /// <summary>SubscribeToTask: streams updates of an existing task (spec §3.1.6).</summary>
        public const string SubscribeToTask = "SubscribeToTask";
        /// <summary>CreateTaskPushNotificationConfig: registers a webhook for a task (spec §3.1.7).</summary>
        public const string CreateTaskPushNotificationConfig = "CreateTaskPushNotificationConfig";
        /// <summary>GetTaskPushNotificationConfig: fetches one webhook registration (spec §3.1.8).</summary>
        public const string GetTaskPushNotificationConfig = "GetTaskPushNotificationConfig";
        /// <summary>ListTaskPushNotificationConfigs: lists the webhooks of a task (spec §3.1.9).</summary>
        public const string ListTaskPushNotificationConfigs = "ListTaskPushNotificationConfigs";
        /// <summary>DeleteTaskPushNotificationConfig: removes a webhook registration (spec §3.1.10).</summary>
        public const string DeleteTaskPushNotificationConfig = "DeleteTaskPushNotificationConfig";
        /// <summary>GetExtendedAgentCard: fetches the authenticated, extended Agent Card (spec §3.1.11).</summary>
        public const string GetExtendedAgentCard = "GetExtendedAgentCard";
    }
}
