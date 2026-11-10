using System.Diagnostics.Metrics;
using System.Text.Json.Nodes;

namespace A2A.V0_3Compat.Tests;

/// <summary>The golden JSON files (documents that validate against the v0.3 JSON schema, tag v0.3.0).</summary>
internal static class Golden
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    public static JsonNode Read(string name) => JsonNode.Parse(Text(name))!;

    public static JsonObject Object(string name) => Read(name).AsObject();
}

internal static class Nodes
{
    public static JsonNode Parse(string json) => JsonNode.Parse(json)!;

    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    public static string Show(JsonNode? node) => node?.ToJsonString() ?? "null";
}

/// <summary>Records what <see cref="V03LossReporter"/> counts while it is disposed of. Other tests may add to the counters too, so assert with Contains.</summary>
internal sealed class MeterRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly object _gate = new();
    public List<(string Counter, Dictionary<string, object?> Tags)> Measurements { get; } = [];

    public MeterRecorder()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == V03LossReporter.MeterName && instrument.Name.StartsWith("a2a.compat.v0_3.", StringComparison.Ordinal))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var map = new Dictionary<string, object?>();
            foreach (var t in tags) map[t.Key] = t.Value;
            lock (_gate) Measurements.Add((instrument.Name, map));
        });
        _listener.Start();
    }

    public IEnumerable<string> DroppedFields()
    {
        lock (_gate)
            return Measurements.Where(m => m.Counter == V03LossReporter.DroppedCounterName).Select(m => (string)m.Tags["field"]!).ToList();
    }

    public IEnumerable<(string Binding, string Operation)> Requests()
    {
        lock (_gate)
            return Measurements.Where(m => m.Counter == V03LossReporter.RequestsCounterName)
                .Select(m => ((string)m.Tags["binding"]!, (string)m.Tags["operation"]!)).ToList();
    }

    public void Dispose() => _listener.Dispose();
}
