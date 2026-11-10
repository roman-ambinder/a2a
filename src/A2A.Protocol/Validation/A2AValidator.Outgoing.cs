using System.Text.Json;

namespace A2A.Validation;

public static partial class A2AValidator
{
    /// <summary>
    ///     Checks a SendMessage request before the client sends it: <c>message</c>, <c>messageId</c>, <c>role</c>,
    ///     <c>parts</c> (and the content of each part), and the nested webhook's <c>url</c> and <c>authentication.scheme</c>.
    ///     Throws locally with InvalidParams.
    /// </summary>
    public static void ValidateOutgoing(SendMessageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Message is null)
        {
            // A2A 1.1: artifact updates to an existing task may come without a message; each names its task.
            if (request.ArtifactUpdates is not { Count: > 0 } updates)
                throw A2AException.InvalidParams("message is required");
            for (var i = 0; i < updates.Count; i++)
                if (string.IsNullOrEmpty(updates[i]?.TaskId))
                    throw A2AException.InvalidParams(
                        $"artifactUpdates[{i}].taskId is required when the request has no message");
            return;
        }

        ValidateMessage(request.Message, "message");
        if (request.Message.Role == Role.Unspecified) throw A2AException.InvalidParams("message.role is required");
        var parts = request.Message.Parts;
        for (var i = 0; i < parts.Count; i++)
            switch (parts[i])
            {
                case TextPart { Text: null }:
                    throw A2AException.InvalidParams($"message.parts[{i}].text is required");
                case UrlPart { Url: null }:
                    throw A2AException.InvalidParams($"message.parts[{i}].url is required");
                case DataPart { Data.ValueKind: JsonValueKind.Undefined }:
                    throw A2AException.InvalidParams($"message.parts[{i}].data is required");
            }

        if (request.Configuration?.TaskPushNotificationConfig is { } push)
            RequirePushFields(push, "configuration.taskPushNotificationConfig.url",
                "configuration.taskPushNotificationConfig.authentication.scheme");
    }

    /// <summary>Checks a GetTask request before the client sends it: <c>id</c> must be present.</summary>
    public static void ValidateOutgoing(GetTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.Id, "id");
    }

    /// <summary>Checks a CancelTask request before the client sends it: <c>id</c> must be present.</summary>
    public static void ValidateOutgoing(CancelTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.Id, "id");
    }

    /// <summary>Checks a SubscribeToTask request before the client sends it: <c>id</c> must be present.</summary>
    public static void ValidateOutgoing(SubscribeToTaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.Id, "id");
    }

    /// <summary>
    ///     Checks a CreateTaskPushNotificationConfig request before the client sends it: <c>url</c>, <c>taskId</c> and,
    ///     when authentication is set, <c>authentication.scheme</c> must be present. Presence only.
    /// </summary>
    public static void ValidateOutgoing(TaskPushNotificationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Url)) throw A2AException.InvalidParams("config.url is required");
        RequireId(config.TaskId, "config.taskId");
        RequirePushFields(config, "config.url", "config.authentication.scheme");
    }

    /// <summary>
    ///     Checks a GetTaskPushNotificationConfig request before the client sends it: <c>taskId</c> and <c>id</c> must be
    ///     present.
    /// </summary>
    public static void ValidateOutgoing(GetTaskPushNotificationConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.TaskId, "taskId");
        RequireId(request.Id, "id");
    }

    /// <summary>
    ///     Checks a DeleteTaskPushNotificationConfig request before the client sends it: <c>taskId</c> and <c>id</c> must
    ///     be present.
    /// </summary>
    public static void ValidateOutgoing(DeleteTaskPushNotificationConfigRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.TaskId, "taskId");
        RequireId(request.Id, "id");
    }

    /// <summary>Checks a ListTaskPushNotificationConfigs request before the client sends it: <c>taskId</c> must be present.</summary>
    public static void ValidateOutgoing(ListTaskPushNotificationConfigsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireId(request.TaskId, "taskId");
    }

    // Presence of url, then authentication.scheme. Paths are passed as constants so nothing is built on the happy path.
    private static void RequirePushFields(TaskPushNotificationConfig config, string urlPath, string schemePath)
    {
        if (string.IsNullOrWhiteSpace(config.Url)) throw A2AException.InvalidParams($"{urlPath} is required");
        if (config.Authentication is { } auth && string.IsNullOrWhiteSpace(auth.Scheme))
            throw A2AException.InvalidParams($"{schemePath} is required");
    }
}