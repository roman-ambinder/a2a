using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace A2A.V0_3Compat;

/// <summary>
/// Reports what the v0.3 conversion cannot carry, and v0.3 traffic. Meter <c>A2A</c>: counter <c>a2a.compat.v0_3.dropped</c>
/// (tag <c>field</c>) and counter <c>a2a.compat.v0_3.requests</c> (tags <c>binding</c>, <c>operation</c>). Each field is logged
/// once at Debug; card problems are logged once each at Warning.
/// </summary>
internal static partial class V03LossReporter
{
    public const string MeterName = "A2A";
    public const string DroppedCounterName = "a2a.compat.v0_3.dropped";
    public const string RequestsCounterName = "a2a.compat.v0_3.requests";

    /// <summary>Most distinct fields and problems remembered, so memory stays bounded.</summary>
    private const int MaxRemembered = 256;

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Dropped =
        Meter.CreateCounter<long>(DroppedCounterName, description: "Fields dropped because v0.3 cannot carry them.");
    private static readonly Counter<long> Requests =
        Meter.CreateCounter<long>(RequestsCounterName, description: "Requests served on the v0.3 surface.");

    private static readonly ConcurrentDictionary<string, byte> LoggedFields = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> LoggedProblems = new(StringComparer.Ordinal);
    private static ILogger _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>Sets the logger. The first non-null call wins.</summary>
    public static void UseLogger(ILogger logger)
    {
        if (ReferenceEquals(_logger, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)) _logger = logger;
    }

    /// <summary>Counts one dropped value of <paramref name="field"/> (for example <c>part.filename</c>).</summary>
    public static void Drop(string field)
    {
        Dropped.Add(1, new KeyValuePair<string, object?>("field", field));
        if (LoggedFields.Count < MaxRemembered && LoggedFields.TryAdd(field, 0)) FieldDropped(_logger, field);
    }

    /// <summary>Counts one request on the v0.3 surface.</summary>
    public static void Request(string binding, string operation) =>
        Requests.Add(1, new KeyValuePair<string, object?>("binding", binding), new KeyValuePair<string, object?>("operation", operation));

    /// <summary>Logs a card conversion problem at Warning, once per distinct message.</summary>
    public static void CardProblem(string message)
    {
        if (LoggedProblems.Count < MaxRemembered && LoggedProblems.TryAdd(message, 0)) CardProblemFound(_logger, message);
    }

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "v0.3 compat dropped {Field} (v0.3 cannot carry it); logged once per field")]
    private static partial void FieldDropped(ILogger logger, string field);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "v0.3 Agent Card conversion: {Problem}")]
    private static partial void CardProblemFound(ILogger logger, string problem);
}
