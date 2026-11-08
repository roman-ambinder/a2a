using System.Text.Json;
using A2A.Json;
using A2A.JsonRpc;
using BenchmarkDotNet.Attributes;

namespace A2A.Benchmarks;

/// <summary>Encodes one JSON-RPC success response carrying a status-update event, the way the SSE endpoint does: the stored event bytes are copied into the envelope by JsonRpcWriter.</summary>
[MemoryDiagnoser]
public class JsonRpcEnvelopeBenchmark
{
    private JsonElement? _id;
    private byte[] _eventJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _id = JsonDocument.Parse("1").RootElement.Clone();
        var evt = new StatusUpdateEvent(new TaskStatusUpdateEvent
        {
            TaskId = "task-1", ContextId = "ctx-1",
            Status = new AgentTaskStatus
            {
                State = TaskState.Working,
                Message = Message.FromAgent("working on it"),
                Timestamp = DateTimeOffset.UnixEpoch,
            },
        });
        _eventJson = A2AJson.SerializeToUtf8Bytes<StreamResponse>(evt);
    }

    [Benchmark]
    public byte[] Encode() =>
        JsonRpcWriter.Success(_id, _eventJson);
}
