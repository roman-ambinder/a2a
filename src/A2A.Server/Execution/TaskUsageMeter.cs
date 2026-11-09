using System.Globalization;
using System.Text.Json;

namespace A2A.Server.Execution;

/// <summary>
/// The usage (tokens and cost units) of one task as one run sees it, shared by the run's <see cref="TaskUpdater"/> and its
/// <see cref="Execution"/>, under one lock. It holds the total (the stored total when the run started, plus this run's
/// reports) and the part of this run's reports that is not stored yet. Sums saturate: a wrapped total would pass the check.
/// </summary>
internal sealed class TaskUsageMeter(A2ALimits? limits = null)
{
    /// <summary>Task metadata key of the stored usage: <c>{"tokens":n,"costUnits":x}</c>. Owned by the server: an agent cannot write it.</summary>
    internal const string MetadataKey = "a2a.usage";

    private readonly object _gate = new();
    private long _tokens;
    private decimal _cost;
    private long _pendingTokens;
    private decimal _pendingCost;
    private bool _exceeded;

    /// <summary>The task's total tokens as this run sees it.</summary>
    public long Tokens { get { lock (_gate) return _tokens; } }

    /// <summary>The task's total cost units as this run sees it.</summary>
    public decimal CostUnits { get { lock (_gate) return _cost; } }

    /// <summary>Sets the total to what the stored task holds, before the agent runs.</summary>
    public void Seed(Dictionary<string, JsonElement>? taskMetadata)
    {
        var (tokens, cost) = Read(taskMetadata);
        lock (_gate)
        {
            _tokens = tokens;
            _cost = cost;
        }
    }

    /// <summary>
    /// Adds a report to the total and to the part not stored yet. <see cref="UsageOutcome.Crossed"/> for the report that takes the
    /// total strictly past a budget (once), <see cref="UsageOutcome.Exceeded"/> for every report after it.
    /// </summary>
    public UsageOutcome Report(long tokens, decimal cost)
    {
        lock (_gate)
        {
            _tokens = Add(_tokens, tokens);
            _cost = Add(_cost, cost);
            _pendingTokens = Add(_pendingTokens, tokens);
            _pendingCost = Add(_pendingCost, cost);
            if (_exceeded) return UsageOutcome.Exceeded;
            var past = (limits?.MaxTaskTokens is > 0 and { } maxTokens && _tokens > maxTokens)
                       || (limits?.MaxTaskCostUnits is > 0 and { } maxCost && _cost > maxCost);
            if (!past) return UsageOutcome.Within;
            _exceeded = true;
            return UsageOutcome.Crossed;
        }
    }

    /// <summary>What was reported and is not stored yet.</summary>
    public (long Tokens, decimal CostUnits) Pending()
    {
        lock (_gate) return (_pendingTokens, _pendingCost);
    }

    /// <summary>Marks <paramref name="stored"/> as stored, once the commit that carried it succeeded.</summary>
    public void MarkStored((long Tokens, decimal CostUnits) stored)
    {
        lock (_gate)
        {
            _pendingTokens = Math.Max(0, _pendingTokens - stored.Tokens);
            _pendingCost = Math.Max(0, _pendingCost - stored.CostUnits);
        }
    }

    /// <summary>
    /// Adds <paramref name="pending"/> to the usage stored in the metadata of <paramref name="task"/> (a copy the caller owns).
    /// Nothing is written when there is nothing to add. The stored total is what is summed, so two runs never overwrite each other.
    /// </summary>
    public static void Apply(AgentTask task, (long Tokens, decimal CostUnits) pending)
    {
        if (pending is { Tokens: 0, CostUnits: 0 }) return;
        var (tokens, cost) = Read(task.Metadata);
        tokens = Add(tokens, pending.Tokens);
        cost = Add(cost, pending.CostUnits);
        using var doc = JsonDocument.Parse(string.Create(CultureInfo.InvariantCulture, $"{{\"tokens\":{tokens},\"costUnits\":{cost}}}"));
        (task.Metadata ??= [])[MetadataKey] = doc.RootElement.Clone();
    }

    /// <summary>The usage stored in <paramref name="metadata"/>; zero for a missing or malformed value.</summary>
    public static (long Tokens, decimal CostUnits) Read(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue(MetadataKey, out var value) || value.ValueKind != JsonValueKind.Object) return (0, 0);
        long tokens = 0;
        decimal cost = 0;
        if (value.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tv) && tv > 0) tokens = tv;
        if (value.TryGetProperty("costUnits", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var cv) && cv > 0) cost = cv;
        return (tokens, cost);
    }

    private static long Add(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;

    private static decimal Add(decimal a, decimal b) => a > decimal.MaxValue - b ? decimal.MaxValue : a + b;
}

/// <summary>The effect of one usage report on the budget.</summary>
internal enum UsageOutcome
{
    /// <summary>Still within every budget.</summary>
    Within,

    /// <summary>This report took the total past a budget. Only one report gets this.</summary>
    Crossed,

    /// <summary>A budget was crossed already (by this or an earlier report).</summary>
    Exceeded,
}
