namespace A2A.Server.Audit;

/// <summary>
/// One row of the audit trail: who did what, to which task, and how it ended. An event has no free-form field and never holds
/// content: no message parts, form values, webhook URLs, push tokens, credentials, bearer tokens or exception messages.
/// The auditor cleans every string before the sink sees it: at most 256 characters, and control characters and lone
/// surrogates become <c>_</c>.
/// </summary>
public sealed record A2AAuditEvent
{
    /// <summary>The <see cref="Actor"/> of an admin call that did not name one (the overloads without an <c>actor</c> argument).</summary>
    public const string UnknownOperator = "operator";

    /// <summary>A GUID (N format). The <see cref="A2AAuditOutcome.Attempted"/> row and the final row of one action share it.</summary>
    public required string Id { get; init; }

    /// <summary>When the event was made (UTC, from the server's <see cref="TimeProvider"/>).</summary>
    public required DateTimeOffset Time { get; init; }

    /// <summary>What happened.</summary>
    public required A2AAuditAction Action { get; init; }

    /// <summary>How far it got.</summary>
    public required A2AAuditOutcome Outcome { get; init; }

    /// <summary>The caller's principal, or the <c>actor</c> an admin call named.</summary>
    public required string Actor { get; init; }

    /// <summary>The tenant the call is bound to. The default tenant is the empty string.</summary>
    public required string Tenant { get; init; }

    /// <summary>The task the action is about, if any.</summary>
    public string? TaskId { get; init; }

    /// <summary>
    /// The push config id, artifact object id, dead-letter delivery id, or for an approval the id of the status message it
    /// used. Null otherwise.
    /// </summary>
    public string? Resource { get; init; }

    /// <summary>The pipeline operation name (for example <c>GetTask</c>). Set on <see cref="A2AAuditAction.AccessDenied"/>.</summary>
    public string? Operation { get; init; }

    /// <summary>
    /// An <see cref="A2AErrorCode"/> name or a fixed word (<c>Canceled</c>, <c>Error</c>, <c>duplicate</c>, <c>recovered</c>,
    /// <c>NotFound</c>, <c>CancelRequested</c>), or for an answered form its action (<c>Accept</c>, <c>Decline</c>, <c>Cancel</c>).
    /// Never exception text.
    /// </summary>
    public string? Reason { get; init; }

    /// <summary>The protocol binding of the call (<c>JSONRPC</c>, <c>HTTP+JSON</c>, <c>GRPC</c>), if known.</summary>
    public string? Binding { get; init; }

    /// <summary>The caller's IP address as the binding saw it, if known.</summary>
    public string? RemoteAddress { get; init; }

    /// <summary>The W3C trace id of the current activity, if there is one.</summary>
    public string? TraceId { get; init; }
}
