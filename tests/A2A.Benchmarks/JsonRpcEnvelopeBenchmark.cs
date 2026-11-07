using System.Text.Json;
using A2A.Json;
using A2A.JsonRpc;
using BenchmarkDotNet.Attributes;

namespace A2A.Benchmarks;

/// <summary>Encodes one JSON-RPC success response carrying a status-update event, the way the SSE endpoint does today.</summary>
[MemoryDiagnoser]
public class JsonRpcEnvelopeBenchmark
{
    private JsonElement? _id;
    private StreamResponse _event = null!;

    [GlobalSetup]
    public void Setup()
    {
        _id = JsonDocument.Parse("1").RootElement.Clone();
        _event = new StatusUpdateEvent(new TaskStatusUpdateEvent
        {
            TaskId = "task-1", ContextId = "ctx-1",
            Status = new AgentTaskStatus
            {
                State = TaskState.Working,
                Message = Message.FromAgent("working on it"),
                Timestamp = DateTimeOffset.UnixEpoch,
            },
        });
    }

    [Benchmark]
    public byte[] Encode() =>
        A2AJson.SerializeToUtf8Bytes(JsonRpcResponse.Success(_id, A2AJson.SerializeToElement(_event)));
}
