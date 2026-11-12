namespace A2A;

/// <summary>Protocol constants: version, method names, headers, paths, media types.</summary>
public static class A2AProtocol
{
    /// <summary>Protocol version implemented by this SDK (Major.Minor).</summary>
    public const string Version = "1.0";

    /// <summary>
    ///     A2A 1.1 (release candidate): the version a caller asks for to get the 1.1 wire (generation, timeline, message
    ///     events).
    /// </summary>
    public const string Version11 = "1.1";

    /// <summary>Version the spec says to assume when the A2A-Version header is absent (§3.6.2).</summary>
    public const string ImplicitVersion = "0.3";

    /// <summary>Path where an agent publishes its Agent Card, relative to its origin.</summary>
    public const string WellKnownAgentCardPath = "/.well-known/agent-card.json";

    /// <summary>
    ///     Where A2A 0.1 to 0.2.x agents published their card (0.3 moved it to <see cref="WellKnownAgentCardPath" />). Served
    ///     and read only by the v0.3 compat packages.
    /// </summary>
    public const string LegacyAgentCardPath = "/.well-known/agent.json";

    /// <summary>Media type of A2A payloads on the HTTP+JSON and JSON-RPC bindings.</summary>
    public const string MediaType = "application/a2a+json";

    /// <summary>Plain JSON media type. Accepted on requests and responses as an alternative to <see cref="MediaType" />.</summary>
    public const string JsonMediaType = "application/json";

    /// <summary>
    ///     Media type of an Agent Card document (spec 1.1 §8.1, §14.1.2). The card endpoint answers with it when the request's
    ///     <c>Accept</c> header asks for it, and with <see cref="JsonMediaType" /> otherwise.
    /// </summary>
    public const string AgentCardMediaType = "application/a2a-agent-card+json";

    /// <summary>Media type of server-sent event streams.</summary>
    public const string EventStreamMediaType = "text/event-stream";

    /// <summary>Domain of the <c>google.rpc.ErrorInfo</c> detail attached to A2A errors.</summary>
    public const string ErrorDomain = "a2a-protocol.org";

    /// <summary>Metadata key (status message and follow-up message) carrying the hash of a pending approval.</summary>
    public const string ApprovalHashKey = "a2a.approval.hash";

    /// <summary>
    ///     Task metadata key, written only by the server: the message id of the status message whose approval was used. An
    ///     approval
    ///     is used at most once.
    /// </summary>
    public const string ApprovalConsumedKey = "a2a.approval.consumed";

    /// <summary>
    ///     Artifact metadata key, written only by the server: <c>true</c> on a stored artifact whose last chunk was sent.
    ///     Further
    ///     chunks cannot be appended to a sealed artifact. Clients can use it to tell a finished artifact from a partial one
    ///     in a snapshot.
    /// </summary>
    public const string ArtifactSealedKey = "a2a.artifact.sealed";

    /// <summary>
    ///     Metadata key on a CancelTask response: the cancel was requested but the execution has not stopped yet. Never
    ///     stored.
    /// </summary>
    public const string CancelRequestedKey = "a2a.cancelRequested";

    /// <summary>
    ///     Message metadata key, written only by the server: when the message was added to the task history, as a UTC ISO 8601
    ///     string in the same form as <c>TaskStatus.timestamp</c>. Informational: the order of the history is the order of the
    ///     list.
    ///     Read it with <see cref="MessageRecordedAtExtensions.GetRecordedAt" />. Messages stored before this key existed do
    ///     not have it.
    /// </summary>
    public const string MessageRecordedAtKey = "a2a.message.recordedAt";

    /// <summary>
    ///     True when <paramref name="version" /> (<c>Major.Minor</c>, patch ignored) is 1.1 or later in the 1.x line. Null,
    ///     empty,
    ///     0.3, 1.0 and anything that does not parse are false.
    /// </summary>
    public static bool IsVersion11OrLater(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        // Called for every stream event: parsed in place, no split strings. The first two dot-separated parts must be digits.
        var text = version.AsSpan().Trim();
        var dot = text.IndexOf('.');
        if (dot < 0) return false;
        var rest = text[(dot + 1)..];
        var next = rest.IndexOf('.');
        return TryParseDigits(text[..dot], out var major) && TryParseDigits(next < 0 ? rest : rest[..next],
                                                              out var minor)
                                                          && major == 1 && minor >= 1;
    }

    /// <summary>
    ///     <paramref name="version" /> without its patch part: <c>"1.0.2"</c> becomes <c>"1.0"</c> (spec §3.6: patch numbers
    ///     SHOULD NOT be used in requests, responses and Agent Cards). A version with no second dot, null and empty come back
    ///     unchanged.
    /// </summary>
    public static string? ToMajorMinor(string? version)
    {
        if (string.IsNullOrEmpty(version)) return version;
        var text = version!.Trim();
        var dot = text.IndexOf('.');
        if (dot < 0) return text;
        var next = text.IndexOf('.', dot + 1);
        return next < 0 ? text : text.Substring(0, next);
    }

    /// <summary>As <c>int.TryParse</c> with <c>NumberStyles.None</c>: ASCII digits only, at least one, no overflow.</summary>
    private static bool TryParseDigits(ReadOnlySpan<char> digits, out int value)
    {
        value = 0;
        if (digits.IsEmpty) return false;
        foreach (var c in digits)
        {
            if (c < '0' || c > '9') return false;
            var d = c - '0';
            if (value > (int.MaxValue - d) / 10) return false;
            value = value * 10 + d;
        }

        return true;
    }

    /// <summary>HTTP header names used by the protocol and by SDK extensions.</summary>
    public static class Headers
    {
        /// <summary>Request header carrying the protocol version the client speaks.</summary>
        public const string Version = "A2A-Version";

        /// <summary>Header listing the extension URIs the client wants active for a request.</summary>
        public const string Extensions = "A2A-Extensions";

        /// <summary>SDK extension: lets a server recognize a retried SendMessage. Defaults to the message id.</summary>
        public const string IdempotencyKey = "Idempotency-Key";

        /// <summary>
        ///     Webhook header carrying a per-delivery id, identical across retries of one delivery. Lets receivers
        ///     deduplicate.
        /// </summary>
        public const string DeliveryId = "X-A2A-Delivery-Id";

        /// <summary>Standard SSE resume header. Value is the last <c>id:</c> the client received (<c>{taskId}:{sequence}</c>).</summary>
        public const string LastEventId = "Last-Event-ID";

        /// <summary>Webhook header carrying <see cref="TaskPushNotificationConfig.Token" />.</summary>
        public const string NotificationToken = "X-A2A-Notification-Token";

        /// <summary>
        ///     SDK extension: monotonically increasing per-task sequence number on push notifications, for dedupe and
        ///     ordering.
        /// </summary>
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

        /// <summary>A2A 1.1 SendLiveMessage: a live, two-way exchange with an agent (spec 1.1 §3.1.12).</summary>
        public const string SendLiveMessage = "SendLiveMessage";

        private static readonly string[] BuiltIn =
        [
            SendMessage, SendStreamingMessage, GetTask, ListTasks, CancelTask, SubscribeToTask,
            CreateTaskPushNotificationConfig, GetTaskPushNotificationConfig, ListTaskPushNotificationConfigs,
            DeleteTaskPushNotificationConfig, GetExtendedAgentCard, SendLiveMessage
        ];

        /// <summary>
        ///     True when <paramref name="method" /> may not name a custom operation: one of the 11 built-in method names, or a
        ///     name that
        ///     starts with <c>rpc.</c> (JSON-RPC 2.0 reserves it). Both are compared ignoring case.
        /// </summary>
        /// <param name="method">The method name to check.</param>
        /// <returns>True when the name is reserved.</returns>
        public static bool IsReserved(string method)
        {
            if (method is null) return false;
            if (method.StartsWith("rpc.", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var builtIn in BuiltIn)
                if (string.Equals(builtIn, method, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}