using System.Text.Json;

namespace A2A.Json;

/// <summary>Converter for <see cref="SendMessageResponse" />.</summary>
public sealed class SendMessageResponseConverter : OneOfConverter<SendMessageResponse>
{
    private static readonly string[] Names = ["task", "message"];

    /// <summary>Name of the union in error messages.</summary>
    protected override string UnionName => nameof(SendMessageResponse);

    /// <summary>Wire member names, one per case.</summary>
    protected override IReadOnlyList<string> MemberNames => Names;

    /// <summary>Reads the case that matches <paramref name="member" />.</summary>
    protected override SendMessageResponse ReadCase(ref Utf8JsonReader reader, string member,
        JsonSerializerOptions options)
    {
        return member == "task"
            ? new TaskResponse(ReadPayload<AgentTask>(ref reader, options))
            : new MessageResponse(ReadPayload<Message>(ref reader, options));
    }

    /// <summary>Writes the case as its wire member.</summary>
    protected override void WriteCase(Utf8JsonWriter writer, SendMessageResponse value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case TaskResponse t: WriteProperty(writer, "task", t.Task, options); break;
            case MessageResponse m: WriteProperty(writer, "message", m.Message, options); break;
            default: throw new JsonException($"Unknown SendMessageResponse type {value.GetType().Name}");
        }
    }
}

/// <summary>Converter for <see cref="StreamResponse" />.</summary>
public sealed class StreamResponseConverter : OneOfConverter<StreamResponse>
{
    private static readonly string[] Names =
        ["task", "message", "statusUpdate", "artifactUpdate", "messageUpdate", "error"];

    /// <summary>Name of the union in error messages.</summary>
    protected override string UnionName => nameof(StreamResponse);

    /// <summary>Wire member names, one per case.</summary>
    protected override IReadOnlyList<string> MemberNames => Names;

    /// <summary>Reads the case that matches <paramref name="member" />.</summary>
    protected override StreamResponse ReadCase(ref Utf8JsonReader reader, string member, JsonSerializerOptions options)
    {
        return member switch
        {
            "task" => new TaskEvent(ReadPayload<AgentTask>(ref reader, options)),
            "message" => new MessageEvent(ReadPayload<Message>(ref reader, options)),
            "statusUpdate" => new StatusUpdateEvent(ReadPayload<TaskStatusUpdateEvent>(ref reader, options)),
            "artifactUpdate" => new ArtifactUpdateEvent(ReadPayload<TaskArtifactUpdateEvent>(ref reader, options)),
            "messageUpdate" => new MessageUpdateEvent(ReadPayload<TaskMessageUpdateEvent>(ref reader, options)),
            _ => new StreamErrorEvent(ReadPayload<StreamError>(ref reader, options))
        };
    }

    /// <summary>Writes the case as its wire member.</summary>
    protected override void WriteCase(Utf8JsonWriter writer, StreamResponse value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case TaskEvent t: WriteProperty(writer, "task", t.Task, options); break;
            case MessageEvent m: WriteProperty(writer, "message", m.Message, options); break;
            case StatusUpdateEvent s: WriteProperty(writer, "statusUpdate", s.StatusUpdate, options); break;
            case ArtifactUpdateEvent a: WriteProperty(writer, "artifactUpdate", a.ArtifactUpdate, options); break;
            case MessageUpdateEvent u: WriteProperty(writer, "messageUpdate", u.MessageUpdate, options); break;
            case StreamErrorEvent e: WriteProperty(writer, "error", e.Error, options); break;
            default: throw new JsonException($"Unknown StreamResponse type {value.GetType().Name}");
        }
    }
}

/// <summary>Converter for <see cref="SecurityScheme" />.</summary>
public sealed class SecuritySchemeConverter : OneOfConverter<SecurityScheme>
{
    private static readonly string[] Names =
    [
        "apiKeySecurityScheme", "httpAuthSecurityScheme", "oauth2SecurityScheme", "openIdConnectSecurityScheme",
        "mtlsSecurityScheme"
    ];

    /// <summary>Name of the union in error messages.</summary>
    protected override string UnionName => nameof(SecurityScheme);

    /// <summary>Wire member names, one per case.</summary>
    protected override IReadOnlyList<string> MemberNames => Names;

    /// <summary>Reads the case that matches <paramref name="member" />.</summary>
    protected override SecurityScheme ReadCase(ref Utf8JsonReader reader, string member, JsonSerializerOptions options)
    {
        return member switch
        {
            "apiKeySecurityScheme" => new ApiKeyScheme(ReadPayload<ApiKeySecurityScheme>(ref reader, options)),
            "httpAuthSecurityScheme" => new HttpAuthScheme(ReadPayload<HttpAuthSecurityScheme>(ref reader, options)),
            "oauth2SecurityScheme" => new OAuth2Scheme(ReadPayload<OAuth2SecurityScheme>(ref reader, options)),
            "openIdConnectSecurityScheme" => new OpenIdConnectScheme(
                ReadPayload<OpenIdConnectSecurityScheme>(ref reader, options)),
            _ => new MutualTlsScheme(ReadPayload<MutualTlsSecurityScheme>(ref reader, options))
        };
    }

    /// <summary>Writes the case as its wire member.</summary>
    protected override void WriteCase(Utf8JsonWriter writer, SecurityScheme value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case ApiKeyScheme s: WriteProperty(writer, "apiKeySecurityScheme", s.Value, options); break;
            case HttpAuthScheme s: WriteProperty(writer, "httpAuthSecurityScheme", s.Value, options); break;
            case OAuth2Scheme s: WriteProperty(writer, "oauth2SecurityScheme", s.Value, options); break;
            case OpenIdConnectScheme s: WriteProperty(writer, "openIdConnectSecurityScheme", s.Value, options); break;
            case MutualTlsScheme s: WriteProperty(writer, "mtlsSecurityScheme", s.Value, options); break;
            default: throw new JsonException($"Unknown SecurityScheme type {value.GetType().Name}");
        }
    }
}