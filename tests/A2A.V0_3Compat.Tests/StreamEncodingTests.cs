using System.Text.Json;
using A2A.Server.Streaming;
using A2A.Server.V0_3Compat;

namespace A2A.V0_3Compat.Tests;

/// <summary>
/// The v0.3 stream bytes, encoded once per event and shared by subscribers, must be exactly what encoding the event for
/// each subscriber gives: the JSON-RPC envelope (with that subscriber's id) around the result, and the HTTP+JSON event.
/// </summary>
public class StreamEncodingTests
{
    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Metadata() => new() { ["trace"] = J("\"abc\""), ["n"] = J("{\"a\":[1,true,null]}") };

    private static List<Part> Parts() =>
    [
        new TextPart("hello é 中文 <tag> \u2028") { Metadata = Metadata() },
        new RawPart(new byte[] { 0, 1, 2, 250 }) { Filename = "a.bin", MediaType = "application/octet-stream" },
        new UrlPart(new Uri("https://example.com/f.png")) { Filename = "f.png", MediaType = "image/png" },
        new DataPart(J("{\"k\":[1,2.5,\"s\"],\"o\":{\"z\":null}}")) { Metadata = Metadata() },
    ];

    private static Message Message(string id, Role role) => new()
    {
        MessageId = id, ContextId = "ctx-1", TaskId = "task-1", Role = role, Parts = Parts(), Metadata = Metadata(),
        Extensions = ["https://ext/a"], ReferenceTaskIds = ["task-0"],
    };

    private static AgentTaskStatus Status(TaskState state, int minute, Message? message) =>
        new() { State = state, Message = message, Timestamp = new DateTimeOffset(2026, 11, 12, 10, minute, 3, 456, TimeSpan.Zero) };

    public static IEnumerable<StreamResponse> Events()
    {
        var timeline = new List<TimelineEntry> { TimelineEntry.FromMessage(Message("m-1", Role.User), 1) };
        for (var i = 0; i < 10; i++)
            timeline.Add(TimelineEntry.FromStatus(Status(TaskState.Working, i, i % 3 == 0 ? Message("s-" + i, Role.Agent) : null), 2 + i));
        var task = new AgentTask
        {
            Id = "task-1", ContextId = "ctx-1", Status = Status(TaskState.InputRequired, 59, Message("s-last", Role.Agent)),
            Artifacts = [new Artifact { ArtifactId = "art-1", Name = "report", Description = "d", Parts = Parts(), Metadata = Metadata() }],
            History = [Message("m-1", Role.User)], Metadata = Metadata(), Generation = 12, Timeline = timeline,
        };
        yield return new TaskEvent(task);
        yield return new MessageEvent(Message("m-solo", Role.Agent));
        yield return new StatusUpdateEvent(new TaskStatusUpdateEvent
        {
            TaskId = "task-1", ContextId = "ctx-1", Status = Status(TaskState.Completed, 1, Message("s-done", Role.Agent)), Metadata = Metadata(),
            Generation = 13,
        });
        yield return new ArtifactUpdateEvent(new TaskArtifactUpdateEvent
        {
            TaskId = "task-1", ContextId = "ctx-1", Artifact = task.Artifacts[0], Append = true, LastChunk = true, Metadata = Metadata(),
        });
    }

    private static readonly JsonElement?[] Ids = [null, J("1"), J("\"req-é\""), J("-2.5e3")];

    [Fact]
    public void Shared_bytes_equal_encoding_per_subscriber()
    {
        foreach (var evt in Events())
        {
            // A stored event (it carries its v1.0 bytes) and a snapshot (it does not).
            foreach (var sequenced in new[] { EventEncoder.Encode(3, evt), new SequencedEvent(0, evt) })
            {
                foreach (var id in Ids)
                {
                    var expected = V03Json.ToUtf8(V03JsonCodec.WriteSuccess(id, V03JsonCodec.Instance.WriteStreamEvent(evt)));
                    var actual = V03StreamEncoding.SuccessPrefix(id)
                        .Concat(V03StreamEncoding.JsonRpcResult(sequenced).ToArray())
                        .Concat(V03StreamEncoding.EnvelopeEnd.ToArray()).ToArray();
                    Assert.Equal(System.Text.Encoding.UTF8.GetString(expected), System.Text.Encoding.UTF8.GetString(actual));
                }

                Assert.Equal(V03Json.ToUtf8(V03ProtoJsonCodec.Instance.WriteStreamEvent(evt)), V03StreamEncoding.RestEvent(sequenced).ToArray());
            }
        }
    }

    [Fact]
    public void A_stored_event_is_encoded_once_for_all_subscribers()
    {
        var stored = EventEncoder.Encode(5, Events().First());
        var first = V03StreamEncoding.JsonRpcResult(stored);
        // Another subscriber's copy of the same event (the sequence may be restamped): the same bytes.
        Assert.True(first.Equals(V03StreamEncoding.JsonRpcResult(stored with { Sequence = 6 })));
        Assert.True(V03StreamEncoding.RestEvent(stored).Equals(V03StreamEncoding.RestEvent(stored)));

        // A snapshot is not shared: it is encoded for its caller.
        var snapshot = new SequencedEvent(0, stored.Event);
        Assert.False(V03StreamEncoding.JsonRpcResult(snapshot).Equals(V03StreamEncoding.JsonRpcResult(snapshot)));
    }

    [Fact]
    public void An_event_with_no_v03_form_still_fails_to_encode()
    {
        var evt = EventEncoder.Encode(1, new MessageUpdateEvent(new TaskMessageUpdateEvent { TaskId = "t", Message = Message("m", Role.User) }));
        Assert.Throws<JsonException>(() => V03StreamEncoding.JsonRpcResult(evt));
    }
}
