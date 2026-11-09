namespace A2A.Validation;

/// <summary>Validates required fields and invariants (spec §5.7). Throws <see cref="A2AException"/> with InvalidParams.</summary>
public static partial class A2AValidator
{
    /// <summary>Checks a SendMessage request a server received. Throws <see cref="A2AException"/> with InvalidParams when it is invalid.</summary>
    public static void ValidateIncoming(SendMessageRequest request)
    {
        if (request.Message is null) throw A2AException.InvalidParams("message is required");
        ValidateMessage(request.Message, "message");
        if (request.Message.Role != Role.User)
            throw A2AException.InvalidParams("message.role must be ROLE_USER for client messages");
        if (request.Configuration is { HistoryLength: < 0 })
            throw A2AException.InvalidParams("configuration.historyLength must be >= 0");
        if (request.Configuration is { TimelineLength: < 0 })
            throw A2AException.InvalidParams("configuration.timelineLength must be >= 0");
        if (request.Configuration?.TaskPushNotificationConfig is { } push)
            ValidatePushConfig(push, "configuration.taskPushNotificationConfig", requireTaskId: false);
    }

    /// <summary>Checks that a message has a <c>messageId</c> and at least one part. <paramref name="path"/> prefixes the field name in error messages.</summary>
    public static void ValidateMessage(Message message, string path)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId))
            throw A2AException.InvalidParams($"{path}.messageId is required");
        if (message.Parts is null || message.Parts.Count == 0)
            throw A2AException.InvalidParams($"{path}.parts must contain at least one part");
        for (var i = 0; i < message.Parts.Count; i++)
        {
            if (message.Parts[i] is null) throw A2AException.InvalidParams($"{path}.parts[{i}] is null");
        }
    }

    /// <summary>Checks that a part is not null. <paramref name="path"/> prefixes the field name in error messages.</summary>
    public static void ValidatePart(Part part, string path)
    {
        if (part is null) throw A2AException.InvalidParams($"{path} is null");
    }

    /// <summary>Checks an artifact an agent produced. Throws InvalidAgentResponse when the id or the parts are missing.</summary>
    public static void ValidateArtifact(Artifact artifact, string path) => ValidateArtifact(artifact, path, allowEmptyParts: false);

    /// <summary>
    /// Checks an artifact an agent produced. Throws InvalidAgentResponse when the id is missing, the parts are missing, or a part is null.
    /// With <paramref name="allowEmptyParts"/>, an empty part list is valid (a chunk that only seals an artifact or updates its metadata).
    /// </summary>
    public static void ValidateArtifact(Artifact artifact, string path, bool allowEmptyParts)
    {
        if (string.IsNullOrWhiteSpace(artifact.ArtifactId))
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"{path}.artifactId is required");
        if (artifact.Parts is null || (artifact.Parts.Count == 0 && !allowEmptyParts))
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"{path}.parts must contain at least one part");
        foreach (var p in artifact.Parts)
        {
            if (p is null)
                throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"{path} contains a null part");
        }
    }

    /// <summary>The longest push config id a client may choose.</summary>
    public const int MaxPushConfigIdLength = 256;

    /// <summary>
    /// Checks a webhook registration: absolute http(s) URL, a scheme when authentication is set, a task id when
    /// <paramref name="requireTaskId"/> is true, and an id (when one is given) of at most <see cref="MaxPushConfigIdLength"/>
    /// visible ASCII characters, because the id is chosen by the client and written to logs.
    /// </summary>
    public static void ValidatePushConfig(TaskPushNotificationConfig config, string path, bool requireTaskId)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
            throw A2AException.InvalidParams($"{path}.url is required");
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw A2AException.InvalidParams($"{path}.url must be an absolute http(s) URL");
        if (requireTaskId && string.IsNullOrWhiteSpace(config.TaskId))
            throw A2AException.InvalidParams($"{path}.taskId is required");
        if (!string.IsNullOrEmpty(config.Id) && !IsValidPushConfigId(config.Id!))
            throw A2AException.InvalidParams($"{path}.id must be at most {MaxPushConfigIdLength} visible ASCII characters"); // never echoes the value
        if (config.Authentication is { } auth && string.IsNullOrWhiteSpace(auth.Scheme))
            throw A2AException.InvalidParams($"{path}.authentication.scheme is required");
        if (config.Authentication is { } sig && string.Equals(sig.Scheme, A2A.Push.WebhookSignature.Scheme, StringComparison.OrdinalIgnoreCase)
            && !A2A.Push.WebhookSignature.TryParseSecret(sig.Credentials, out _))
            throw A2AException.InvalidParams("Invalid webhook signing secret.");
    }

    private static bool IsValidPushConfigId(string id)
    {
        if (id.Length > MaxPushConfigIdLength) return false;
        foreach (var c in id)
        {
            if (c < '\u0021' || c > '\u007E') return false;
        }
        return true;
    }

    /// <summary>Throws InvalidParams when <paramref name="id"/> is null or blank. <paramref name="name"/> is the field name in the message.</summary>
    public static void RequireId(string? id, string name)
    {
        if (string.IsNullOrWhiteSpace(id)) throw A2AException.InvalidParams($"{name} is required");
    }
}
