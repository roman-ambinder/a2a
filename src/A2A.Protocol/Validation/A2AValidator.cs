namespace A2A.Validation;

/// <summary>Validates required fields and invariants (spec §5.7). Throws <see cref="A2AException"/> with InvalidParams.</summary>
public static class A2AValidator
{
    public static void ValidateIncoming(SendMessageRequest request)
    {
        if (request.Message is null) throw A2AException.InvalidParams("message is required");
        ValidateMessage(request.Message, "message");
        if (request.Message.Role != Role.User)
            throw A2AException.InvalidParams("message.role must be ROLE_USER for client messages");
        if (request.Configuration is { HistoryLength: < 0 })
            throw A2AException.InvalidParams("configuration.historyLength must be >= 0");
        if (request.Configuration?.TaskPushNotificationConfig is { } push)
            ValidatePushConfig(push, "configuration.taskPushNotificationConfig", requireTaskId: false);
    }

    /// <summary>Checks a request before the client sends it: <c>messageId</c> and <c>parts</c> must be present. Throws locally with InvalidParams.</summary>
    public static void ValidateOutgoing(SendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is null) throw A2AException.InvalidParams("message is required");
        ValidateMessage(request.Message, "message");
    }

    public static void ValidateMessage(Message message, string path)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId))
            throw A2AException.InvalidParams($"{path}.messageId is required");
        if (message.Parts is null || message.Parts.Count == 0)
            throw A2AException.InvalidParams($"{path}.parts must contain at least one part");
        for (var i = 0; i < message.Parts.Count; i++) ValidatePart(message.Parts[i], $"{path}.parts[{i}]");
    }

    public static void ValidatePart(Part part, string path)
    {
        if (part is null) throw A2AException.InvalidParams($"{path} is null");
    }

    public static void ValidateArtifact(Artifact artifact, string path)
    {
        if (string.IsNullOrWhiteSpace(artifact.ArtifactId))
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"{path}.artifactId is required");
        if (artifact.Parts is null || artifact.Parts.Count == 0)
            throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"{path}.parts must contain at least one part");
        foreach (var p in artifact.Parts)
        {
            if (p is null)
                throw new A2AException(A2AErrorCode.InvalidAgentResponse, $"{path} contains a null part");
        }
    }

    public static void ValidatePushConfig(TaskPushNotificationConfig config, string path, bool requireTaskId)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
            throw A2AException.InvalidParams($"{path}.url is required");
        if (!Uri.TryCreate(config.Url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw A2AException.InvalidParams($"{path}.url must be an absolute http(s) URL");
        if (requireTaskId && string.IsNullOrWhiteSpace(config.TaskId))
            throw A2AException.InvalidParams($"{path}.taskId is required");
        if (config.Authentication is { } auth && string.IsNullOrWhiteSpace(auth.Scheme))
            throw A2AException.InvalidParams($"{path}.authentication.scheme is required");
        if (config.Authentication is { } sig && string.Equals(sig.Scheme, A2A.Push.WebhookSignature.Scheme, StringComparison.OrdinalIgnoreCase)
            && !A2A.Push.WebhookSignature.TryParseSecret(sig.Credentials, out _))
            throw A2AException.InvalidParams("Invalid webhook signing secret.");
    }

    public static void RequireId(string? id, string name)
    {
        if (string.IsNullOrWhiteSpace(id)) throw A2AException.InvalidParams($"{name} is required");
    }
}
