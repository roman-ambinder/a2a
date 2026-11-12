using A2A.Server.Pipeline;
using A2A.Server.Push;
using A2A.Server.Storage;
using A2A.V0_3Compat;

namespace A2A.Server.V0_3Compat;

/// <summary>
///     The webhook body of a push config created by a v0.3 caller: the task as it is now, as a v0.3 JSON-RPC <c>Task</c>
///     (with <c>kind: "task"</c>). The v0.3 spec leaves the body open; this is what the official Python SDK 0.3 sends (the
///     latest task, as JSON). Each event of the task sends the task as it is when the delivery is made, so a retried or late
///     delivery carries a newer state, never an older one. The history is capped like a GetTask answer.
/// </summary>
internal sealed class V03PushPayloadEncoder(TaskStore tasks, A2ALimits limits) : IPushPayloadEncoder
{
    public string ProtocolVersion => A2AProtocol.ImplicitVersion;

    public string ContentType => A2AProtocol.JsonMediaType;

    public async ValueTask<ReadOnlyMemory<byte>?> EncodeAsync(PushDelivery delivery, ReadOnlyMemory<byte> eventBody,
        CancellationToken cancellationToken)
    {
        var record = await tasks.GetAsync(delivery.Task, cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        var task = TaskShaper.Shape(record.Task, null, limits);
        return V03Json.ToUtf8(V03JsonCodec.Instance.WriteTask(task));
    }
}
