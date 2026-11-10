using A2A.Server.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.Server.Audit;

/// <summary>
///     The default sink: one Information log per event, with every field as a structured property. The category is
///     <c>A2A.Server.Audit.LoggerAuditSink</c>. Does nothing when Information logging is off. Replace it with a sink that
///     stores
///     the events (for example <c>AddPostgresAuditSink</c>) when logs are not your audit record. The line never has the
///     actor or the
///     address in clear, unless <see cref="A2AAuditOptions.LogIdentity" /> is set: the actor is <c>sha256:</c> and its
///     hash.
/// </summary>
public sealed partial class LoggerAuditSink : A2AAuditSink
{
    private readonly bool _logIdentity;
    private readonly ILogger _logger;

    /// <summary>Creates the sink. In an app the DI container does this.</summary>
    /// <param name="logger">The logger. Without one nothing is written.</param>
    /// <param name="options">The server options, for <see cref="A2AAuditOptions.LogIdentity" />.</param>
    public LoggerAuditSink(ILogger<LoggerAuditSink>? logger = null, A2AServerOptions? options = null)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _logIdentity = options?.Audit.LogIdentity == true;
    }

    /// <inheritdoc />
    public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        if (_logger.IsEnabled(LogLevel.Information))
            Audit(_logger, auditEvent.Id, auditEvent.Time, auditEvent.Action, auditEvent.Outcome,
                _logIdentity ? auditEvent.Actor : "sha256:" + SecurityEventLog.HashPrincipal(auditEvent.Actor),
                auditEvent.Tenant,
                auditEvent.TaskId, auditEvent.Resource, auditEvent.Operation, auditEvent.Reason, auditEvent.Binding,
                _logIdentity ? auditEvent.RemoteAddress : null, auditEvent.TraceId);
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 270, Level = LogLevel.Information,
        Message =
            "Audit {AuditId} at {AuditTime}: {AuditAction} {AuditOutcome} by {AuditActor} in tenant {AuditTenant}, task {AuditTaskId}, resource {AuditResource}, operation {AuditOperation}, reason {AuditReason}, binding {AuditBinding}, from {AuditRemoteAddress}, trace {AuditTraceId}")]
    private static partial void Audit(ILogger logger, string auditId, DateTimeOffset auditTime,
        A2AAuditAction auditAction,
        A2AAuditOutcome auditOutcome, string auditActor, string auditTenant, string? auditTaskId, string? auditResource,
        string? auditOperation, string? auditReason, string? auditBinding, string? auditRemoteAddress,
        string? auditTraceId);
}