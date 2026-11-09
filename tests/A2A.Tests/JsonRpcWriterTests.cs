using System.Text;
using System.Text.Json;
using A2A.Json;
using A2A.JsonRpc;

namespace A2A.Tests;

public sealed class JsonRpcWriterTests
{
    private static StreamResponse Event() => new StatusUpdateEvent(new TaskStatusUpdateEvent
    {
        TaskId = "task-1", ContextId = "ctx-1",
        Status = new AgentTaskStatus
        {
            State = TaskState.Working,
            Message = Message.FromAgent("working <on> it é"),
            Timestamp = DateTimeOffset.UnixEpoch,
        },
    });

    public static TheoryData<string?> Ids => new([null, "\"abc\"", "42", "null", "\"x\\\"y\""]);

    [Theory]
    [MemberData(nameof(Ids))]
    public void Success_bytes_equal_legacy_serialization(string? rawId)
    {
        JsonElement? id = rawId is null ? null : JsonDocument.Parse(rawId).RootElement.Clone();
        var evt = Event();
        var legacy = A2AJson.SerializeToUtf8Bytes(JsonRpcResponse.Success(id, A2AJson.SerializeToElement(evt)));

        var payload = A2AJson.SerializeToUtf8Bytes(evt);
        Assert.Equal(legacy, JsonRpcWriter.Success(id, payload));
        Assert.Equal(legacy, JsonRpcWriter.Success(id, evt, A2AJson.GetTypeInfo<StreamResponse>()));

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, JsonRpcWriter.CreateWriterOptions()))
            JsonRpcWriter.WriteSuccess(w, id, evt, A2AJson.GetTypeInfo<StreamResponse>());
        Assert.Equal(legacy, buffer.WrittenSpan.ToArray());

        // A stream writes the prefix once, then each event's bytes and the closing brace.
        Assert.Equal(legacy, JsonRpcWriter.SuccessPrefix(id).Concat(payload).Append((byte)'}').ToArray());
    }

    [Fact]
    public void Request_bytes_equal_legacy_serialization()
    {
        var request = new SendMessageRequest { Message = Message.FromUser("hi <there> é") };
        var legacy = A2AJson.SerializeToUtf8Bytes(new JsonRpcRequest
        {
            Id = A2AJson.SerializeToElement("0123abc"),
            Method = A2AProtocol.Methods.SendMessage,
            Params = A2AJson.SerializeToElement(request),
        });
        var actual = JsonRpcWriter.Request("0123abc", A2AProtocol.Methods.SendMessage, request, A2AJson.GetTypeInfo<SendMessageRequest>());
        Assert.Equal(Encoding.UTF8.GetString(legacy), Encoding.UTF8.GetString(actual));
    }
}
