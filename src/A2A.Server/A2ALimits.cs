namespace A2A.Server;

/// <summary>
/// Size, count and time limits that protect the server. Every limit is on by default; raise or lower them through
/// <see cref="A2AServerOptions.Limits"/>.
/// </summary>
public sealed class A2ALimits
{
    /// <summary>Largest accepted HTTP request body in bytes. Larger bodies get HTTP 413 (PAYLOAD_TOO_LARGE). Default 10 MB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Most parts in one message. More is InvalidRequest. Default 100.</summary>
    public int MaxPartsPerMessage { get; set; } = 100;

    /// <summary>Largest decoded <c>raw</c> part in bytes. Larger parts get HTTP 413 (PAYLOAD_TOO_LARGE). Default 8 MB.</summary>
    public int MaxRawPartBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Deepest JSON nesting accepted in a request. Default 32. The shared serializer options (<c>A2AJson.Options</c>) stop at 32,
    /// so values above 32 do not allow deeper payloads; lower the value to be stricter.
    /// </summary>
    public int MaxJsonDepth { get; set; } = 32;

    /// <summary>
    /// Most history messages returned for a task, also when the caller sends no <c>historyLength</c> or a larger one
    /// (the newest messages are kept). Default 100.
    /// </summary>
    public int MaxHistoryLengthReturned { get; set; } = 100;

    /// <summary>Upper bound on the ListTasks page size. Default 100.</summary>
    public int MaxListPageSize { get; set; } = 100;

    /// <summary>Most concurrent streams (subscriptions) per task. Default 10.</summary>
    public int MaxConcurrentStreamsPerTask { get; set; } = 10;

    /// <summary>Most concurrent streams per caller. Default 50.</summary>
    public int MaxConcurrentStreamsPerPrincipal { get; set; } = 50;

    /// <summary>Largest single stream event in bytes. Default 4 MB.</summary>
    public int MaxEventBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Longest a stream may stay open. Default 1 hour.</summary>
    public TimeSpan MaxStreamDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How often an open stream re-checks the caller's authorization. Default 5 minutes.</summary>
    public TimeSpan StreamReauthInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Most push notification configs per task. More is InvalidRequest. Default 10.</summary>
    public int MaxPushConfigsPerTask { get; set; } = 10;
}
