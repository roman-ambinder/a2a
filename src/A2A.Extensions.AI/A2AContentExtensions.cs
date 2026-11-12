using A2A.Extensions.AI.Shared;
using Microsoft.Extensions.AI;

namespace A2A.Extensions.AI;

/// <summary>
///     Conversions between A2A messages, artifacts and parts, and <c>Microsoft.Extensions.AI</c> chat messages and
///     content.
/// </summary>
/// <remarks>
///     Some content has no A2A form (function calls and results, reasoning, errors, usage, hosted files):
///     <see cref="ToPart" />
///     returns null for it and the message conversions drop it. Text media type and file name are lost through
///     <see cref="TextContent" />. JSON in a data part is re-encoded: the bytes may differ, the meaning does not. Metadata
///     values
///     that are not JSON elements, strings, booleans or numbers are dropped.
/// </remarks>
public static class A2AContentExtensions
{
    /// <summary>
    ///     Converts a part to AI content. Never returns null. An unusable media type becomes
    ///     <c>application/octet-stream</c>; a URL part with a relative URL becomes plain <see cref="AIContent" /> that
    ///     <see cref="ToPart" /> turns into null.
    /// </summary>
    public static AIContent ToAIContent(this Part part)
    {
        ArgumentNullException.ThrowIfNull(part);
        var content = ModelContent.Convert(part, false, true) ?? new AIContent();
        content.RawRepresentation = part;
        if (part.Metadata is { Count: > 0 } metadata)
        {
            content.AdditionalProperties = [];
            foreach (var (key, value) in metadata) content.AdditionalProperties[key] = value;
        }

        return content;
    }

    /// <summary>
    ///     Converts AI content to a part, or returns null when A2A has no form for it. Always built from the content,
    ///     never from <see cref="AIContent.RawRepresentation" />.
    /// </summary>
    public static Part? ToPart(this AIContent content)
    {
        return ModelContent.ConvertToPart(content, true);
    }

    /// <summary>
    ///     Converts an A2A message to a chat message. <c>Agent</c> becomes <c>Assistant</c>, every other role becomes
    ///     <c>User</c>.
    /// </summary>
    public static ChatMessage ToChatMessage(this Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new ChatMessage(message.Role == Role.Agent ? ChatRole.Assistant : ChatRole.User,
            ToContents(message.Parts))
        {
            MessageId = message.MessageId,
            AdditionalProperties = ModelContent.ToProperties(message.Metadata)
        };
    }

    /// <summary>
    ///     Converts a chat message to an A2A message with a new id when the chat message has none. <c>Assistant</c> becomes
    ///     <c>Agent</c>; every other role becomes <c>User</c>. Content without an A2A form is dropped.
    /// </summary>
    public static Message ToA2AMessage(this ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new Message
        {
            MessageId = string.IsNullOrEmpty(message.MessageId) ? Message.NewId() : message.MessageId,
            Role = message.Role == ChatRole.Assistant ? Role.Agent : Role.User,
            Parts = ToParts(message.Contents),
            Metadata = ModelContent.ToMetadata(message.AdditionalProperties)
        };
    }

    /// <summary>Converts an artifact to an assistant chat message. The message id is the artifact id.</summary>
    public static ChatMessage ToChatMessage(this Artifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new ChatMessage(ChatRole.Assistant, ToContents(artifact.Parts))
        {
            MessageId = artifact.ArtifactId,
            AuthorName = artifact.Name,
            AdditionalProperties = ModelContent.ToProperties(artifact.Metadata)
        };
    }

    /// <summary>
    ///     Converts a chat message to an artifact. <paramref name="artifactId" /> defaults to a new GUID,
    ///     <paramref name="name" /> to null.
    /// </summary>
    public static Artifact ToA2AArtifact(this ChatMessage message, string? artifactId = null, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new Artifact
        {
            ArtifactId = string.IsNullOrEmpty(artifactId) ? Guid.NewGuid().ToString() : artifactId,
            Name = name,
            Parts = ToParts(message.Contents),
            Metadata = ModelContent.ToMetadata(message.AdditionalProperties)
        };
    }

    private static List<AIContent> ToContents(List<Part> parts)
    {
        var list = new List<AIContent>(parts.Count);
        foreach (var part in parts) list.Add(part.ToAIContent());
        return list;
    }

    private static List<Part> ToParts(IList<AIContent> contents)
    {
        var list = new List<Part>(contents.Count);
        foreach (var content in contents)
            if (content.ToPart() is { } part)
                list.Add(part);
        return list;
    }
}
