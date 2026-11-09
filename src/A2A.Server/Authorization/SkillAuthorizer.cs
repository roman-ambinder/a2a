using System.Text.Json;
using A2A.Diagnostics;
using A2A.Json;
using A2A.Server.Agents;
using A2A.Server.Audit;
using A2A.Server.Pipeline;
using A2A.Server.Telemetry;
using A2A.Skills;

namespace A2A.Server.Authorization;

/// <summary>
/// Per-skill authorization (see <see cref="SkillAuthorizationOptions"/>): checks the skill a send names, asks again on an open
/// stream, answers the agent's own questions, and filters the extended card. One per request handler, built from its services.
/// </summary>
internal sealed class SkillAuthorizer(A2AAuthorizationHandler handler, SkillAuthorizationOptions options, IAgentCardProvider cards, RequestCards requestCards,
    A2AServerOptions? serverOptions = null, A2AAuditor? audit = null)
{
    /// <summary>True when the check applies to <paramref name="operation"/>: a mode is on and it starts a skill.</summary>
    public bool AppliesTo(A2AOperationDescriptor operation) =>
        options.Mode != SkillAuthorizationMode.Off && operation.Kind is A2AOperation.SendMessage or A2AOperation.SendStreamingMessage;

    /// <summary>
    /// Checks the skill <paramref name="request"/> names, when <see cref="AppliesTo"/>. Throws when it is refused; on success sets
    /// <see cref="ServerCallContext.AuthorizedSkillId"/>. An unknown skill and a denied skill give the same exception.
    /// </summary>
    public async Task RequireAsync(A2AOperationDescriptor operation, ServerCallContext call, object request, string? taskId, CancellationToken ct)
    {
        if (!AppliesTo(operation) || request is not SendMessageRequest send) return;
        if (send.Message is not { } message) throw Refuse("message is required");

        string? id = null;
        if (message.Metadata is { } metadata && metadata.TryGetValue(SkillSelection.MetadataKey, out var value))
        {
            if (value.ValueKind != JsonValueKind.String) throw Refuse("metadata.skillId must be a string");
            id = message.GetRequestedSkillId();
            // Null here means empty (no skill named) or unreadable text such as a lone surrogate (refused).
            if (id is null && !value.ValueEquals(string.Empty)) throw Refuse("metadata.skillId must be a string");
        }

        if (id is null)
        {
            if (options.Mode == SkillAuthorizationMode.Required) throw Refuse("metadata.skillId must name the skill to run");
            return;
        }

        var card = await requestCards.RequireAgentAsync(call, ct).ConfigureAwait(false);
        if (!await KnownSkills.IsKnownAsync(card, id, call, cards, handler, ct).ConfigureAwait(false)
            || !await handler.AuthorizeSkillAsync(call, id, ct).ConfigureAwait(false))
        {
            if (serverOptions is not null)
                SecurityEventLog.Record(serverOptions, A2ATelemetry.SecurityEventNames.PermissionDenied, operation.Name, call, string.IsNullOrEmpty(taskId) ? null : taskId);
            if (audit is not null)
                await OperationAuthorization.DeniedAsync(audit, call, operation, taskId, "PermissionDenied").ConfigureAwait(false);
            // The handler-denial path, so the caller cannot tell a hidden skill from a missing one.
            if (!string.IsNullOrEmpty(taskId)) throw A2AException.TaskNotFound(taskId);
            throw new A2AException(A2AErrorCode.PermissionDenied);
        }
        call.AuthorizedSkillId = id;
    }

    /// <summary>The decision of <see cref="RequireAsync"/> for one id, in any mode: on the card, then allowed by the handler.</summary>
    public async ValueTask<bool> IsAllowedAsync(ServerCallContext call, string skillId, CancellationToken ct)
    {
        if (await requestCards.ResolveAsync(call, ct).ConfigureAwait(false) is not { } card) return false;
        return await KnownSkills.IsKnownAsync(card, skillId, call, cards, handler, ct).ConfigureAwait(false)
            && await handler.AuthorizeSkillAsync(call, skillId, ct).ConfigureAwait(false);
    }

    /// <summary>For an open stream: false when the skill that was authorized for it is no longer allowed.</summary>
    public ValueTask<bool> StillAllowedAsync(A2AOperationDescriptor operation, ServerCallContext call, CancellationToken ct) =>
        operation.Kind == A2AOperation.SendStreamingMessage && call.AuthorizedSkillId is { } id
            ? IsAllowedAsync(call, id, ct)
            : ValueTask.FromResult(true);

    /// <summary>
    /// The extended card with only the skills the handler allows, when <see cref="SkillAuthorizationOptions.FilterExtendedCard"/> is set.
    /// Never changes <paramref name="card"/>: when a skill is removed, the result is a copy with <c>Signatures</c> cleared (they no
    /// longer match). When nothing is removed, <paramref name="card"/> itself.
    /// </summary>
    public async ValueTask<AgentCard> FilterExtendedCardAsync(ServerCallContext call, AgentCard card, CancellationToken ct)
    {
        if (!options.FilterExtendedCard || card.Skills is not { Count: > 0 } skills) return card;

        var keep = new bool[skills.Count];
        var removed = false;
        for (var i = 0; i < skills.Count; i++)
        {
            var id = skills[i]?.Id;
            keep[i] = !string.IsNullOrEmpty(id) && await handler.AuthorizeSkillAsync(call, id, ct).ConfigureAwait(false);
            removed |= !keep[i];
        }
        if (!removed) return card;

        var copy = JsonSerializer.Deserialize(A2AJson.SerializeToUtf8Bytes(card), A2AJson.GetTypeInfo<AgentCard>())
            ?? throw new InvalidOperationException("The Agent Card could not be copied.");
        var kept = new List<AgentSkill>(skills.Count);
        for (var i = 0; i < copy.Skills.Count && i < keep.Length; i++)
            if (keep[i]) kept.Add(copy.Skills[i]);
        copy.Skills = kept;
        copy.Signatures = null;
        return copy;
    }

    private static A2AException Refuse(string text) => new(A2AErrorCode.InvalidParams, text)
    {
        Metadata = new() { ["extension"] = SkillSelection.ExtensionUri },
    };
}
