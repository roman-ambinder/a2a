using System.Security.Cryptography;
using System.Text;
using A2A.Diagnostics;
using A2A.Text;
using Microsoft.Extensions.Logging;

namespace A2A.Server.Telemetry;

/// <summary>
///     Records a security event (spec 13.4): counts <see cref="A2ATelemetry.Metrics.SecurityEvents" /> and writes one
///     structured log
///     line with the event, operation, tenant, resource id and a principal hash. The log line never carries the principal,
///     a token
///     or a webhook URL. The hash (the first 16 hex characters of the SHA-256 of the principal) is computed only when the
///     line is written.
/// </summary>
internal static class SecurityEventLog
{
    /// <summary>The logger category of the log lines.</summary>
    public const string Category = "A2A.Server.Security";

    /// <summary>Records an event of a call. The logger comes from <see cref="A2AServerOptions.SecurityLogger" />.</summary>
    public static void Record(A2AServerOptions options, string securityEvent, string? operation, ServerCallContext call,
        string? resourceId)
    {
        Record(options.SecurityLogger, securityEvent, operation, call.Tenant, call.Principal, resourceId);
    }

    /// <summary>
    ///     Records an event. <paramref name="logger" /> may be null (the counter still counts). <paramref name="resourceId" />
    ///     is the task or
    ///     context the event is about, or null.
    /// </summary>
    public static void Record(ILogger? logger, string securityEvent, string? operation, string tenant,
        string? principal, string? resourceId)
    {
        if (operation is null)
            ServerTelemetry.SecurityEvents.Add(1,
                new KeyValuePair<string, object?>(A2ATelemetry.Tags.SecurityEvent, securityEvent));
        else
            ServerTelemetry.SecurityEvents.Add(1,
                new KeyValuePair<string, object?>(A2ATelemetry.Tags.SecurityEvent, securityEvent),
                new KeyValuePair<string, object?>(A2ATelemetry.Tags.Operation, operation));

        if (logger is null) return;
        var information = securityEvent == A2ATelemetry.SecurityEventNames.Unauthenticated;
        if (!logger.IsEnabled(information ? LogLevel.Information : LogLevel.Warning)) return;
        var hash = HashPrincipal(principal);
        // The caller chose these, and may not be authenticated: one line, capped.
        var safeTenant = TextSanitizer.ForMessage(tenant);
        var safeResource = resourceId is null ? null : TextSanitizer.ForMessage(resourceId);
        if (information)
            ServerLog.SecurityEventInformation(logger, securityEvent, operation ?? "-", safeTenant, safeResource, hash);
        else ServerLog.SecurityEventWarning(logger, securityEvent, operation ?? "-", safeTenant, safeResource, hash);
    }

    /// <summary>
    ///     Records an anomaly (<see cref="AnomalyDetector" />): counts an <see cref="A2ATelemetry.SecurityEventNames.Anomaly" />
    ///     security event and <see cref="ServerTelemetry.Anomalies" /> (tagged with <paramref name="kind" />), and writes one
    ///     warning (1202) with the kind, the count, the threshold, the tenant and the principal hash.
    /// </summary>
    public static void RecordAnomaly(ILogger? logger, string kind, string operation, string tenant, string principal,
        int count, int threshold)
    {
        ServerTelemetry.SecurityEvents.Add(1,
            new KeyValuePair<string, object?>(A2ATelemetry.Tags.SecurityEvent, A2ATelemetry.SecurityEventNames.Anomaly),
            new KeyValuePair<string, object?>(A2ATelemetry.Tags.Operation, operation));
        ServerTelemetry.Anomalies.Add(1, new KeyValuePair<string, object?>(ServerTelemetry.AnomalyKindTag, kind));
        if (logger is null || !logger.IsEnabled(LogLevel.Warning)) return;
        // The tenant is the caller's choice: one line, capped.
        ServerLog.SecurityAnomaly(logger, kind, operation, count, threshold, TextSanitizer.ForMessage(tenant),
            HashPrincipal(principal));
    }

    /// <summary>The first 16 hex characters (lower case) of the SHA-256 of <paramref name="principal" />.</summary>
    internal static string HashPrincipal(string? principal)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(principal ?? string.Empty)), 0, 8)
            .ToLowerInvariant();
    }
}