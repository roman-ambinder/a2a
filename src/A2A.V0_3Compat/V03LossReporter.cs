using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace A2A.V0_3Compat;

/// <summary>
///     Reports what the v0.3 conversion cannot carry, and v0.3 traffic. Meter <c>A2A</c>: counter
///     <c>a2a.compat.v0_3.dropped</c>
///     (tag <c>field</c>) and counter <c>a2a.compat.v0_3.requests</c> (tags <c>binding</c>, <c>operation</c>). Spec A.2
///     asks for a warning when legacy objects are received, so each of these is logged once at Warning: a dropped field, a
///     card problem, a v0.3 operation served (per binding), and a v0.3 agent a client talks to (per URL).
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
    private static readonly ConcurrentDictionary<string, byte> LoggedOperations = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> LoggedAgents = new(StringComparer.Ordinal);
    private static ILogger _logger = NullLogger.Instance;

    /// <summary>Sets the logger. The first non-null call wins.</summary>
    public static void UseLogger(ILogger logger)
    {
        if (ReferenceEquals(_logger, NullLogger.Instance)) _logger = logger;
    }

    /// <summary>Counts one dropped value of <paramref name="field" /> (for example <c>part.filename</c>).</summary>
    public static void Drop(string field)
    {
        Drop(field, _logger);
    }

    internal static void Drop(string field, ILogger logger)
    {
        Dropped.Add(1, new KeyValuePair<string, object?>("field", field));
        if (LoggedFields.Count < MaxRemembered && LoggedFields.TryAdd(field, 0)) FieldDropped(logger, field);
    }

    /// <summary>Counts one request on the v0.3 surface.</summary>
    public static void Request(string binding, string operation)
    {
        Request(binding, operation, _logger);
    }

    /// <summary>Counts one request and warns once per binding and operation, to <paramref name="logger" />.</summary>
    internal static void Request(string binding, string operation, ILogger logger)
    {
        Requests.Add(1, new KeyValuePair<string, object?>("binding", binding),
            new KeyValuePair<string, object?>("operation", operation));
        if (LoggedOperations.Count < MaxRemembered && LoggedOperations.TryAdd(binding + " " + operation, 0))
            LegacyOperationServed(logger, operation, binding);
    }

    /// <summary>Warns once per URL that a client is talking to an agent through its v0.3 interface.</summary>
    public static void LegacyAgent(string agent, string url)
    {
        LegacyAgent(agent, url, _logger);
    }

    internal static void LegacyAgent(string agent, string url, ILogger logger)
    {
        if (LoggedAgents.Count < MaxRemembered && LoggedAgents.TryAdd(url, 0)) LegacyAgentUsed(logger, agent, url);
    }

    /// <summary>Logs a card conversion problem at Warning, once per distinct message.</summary>
    public static void CardProblem(string message)
    {
        if (LoggedProblems.Count < MaxRemembered && LoggedProblems.TryAdd(message, 0))
            CardProblemFound(_logger, message);
    }

    // Event ids 1600-1699: v0.3 compatibility (A2A.V0_3Compat).
    [LoggerMessage(EventId = 1600, Level = LogLevel.Warning,
        Message = "v0.3 compat dropped {Field} (v0.3 cannot carry it); logged once per field")]
    private static partial void FieldDropped(ILogger logger, string field);

    [LoggerMessage(EventId = 1601, Level = LogLevel.Warning, Message = "v0.3 Agent Card conversion: {Problem}")]
    private static partial void CardProblemFound(ILogger logger, string problem);

    [LoggerMessage(EventId = 1602, Level = LogLevel.Warning,
        Message = "Served a v0.3 '{Operation}' request over {Binding}. v0.3 names are deprecated; callers should move to A2A v1.0 (spec A.2). Logged once per operation.")]
    private static partial void LegacyOperationServed(ILogger logger, string operation, string binding);

    [LoggerMessage(EventId = 1603, Level = LogLevel.Warning,
        Message = "The agent '{Agent}' at {Url} offers only A2A v0.3, so the v0.3 compat transport is used. v0.3 is deprecated (spec A.2). Logged once per URL.")]
    private static partial void LegacyAgentUsed(ILogger logger, string agent, string url);
}