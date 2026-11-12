using System.Text.Json;
using A2A.Server;
using A2A.Server.Execution;

namespace A2A.Tests;

/// <summary>
/// <see cref="TaskSize.AppendedBytes"/>: the engine carries the measured size from one stored task to the next, so the carried
/// size must be exactly what a full <see cref="TaskSize.Measure(AgentTask)"/> of the new task gives.
/// </summary>
public sealed class TaskSizeTests
{
    private static AgentTask Task() => new()
    {
        Id = "t", ContextId = "c", Status = new AgentTaskStatus { State = TaskState.Working },
        History = [new Message { MessageId = "m", Role = Role.User, Parts = [Part.FromText("hello")] }],
        Artifacts = [new Artifact { ArtifactId = "a", Name = "out", Parts = [Part.FromText("abc")] }],
    };

    private static StreamResponse Append(string artifactId, List<Part> parts, string? name = null, Dictionary<string, JsonElement>? meta = null,
        Dictionary<string, JsonElement>? eventMeta = null, bool append = true) =>
        StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c", Append = append, Metadata = eventMeta,
            Artifact = new Artifact { ArtifactId = artifactId, Name = name, Parts = parts, Metadata = meta },
        });

    private static Dictionary<string, JsonElement> Meta() => new() { ["k"] = JsonSerializer.SerializeToElement("value") };

    [Fact]
    public void Carried_size_equals_a_full_measure_for_an_append_of_parts()
    {
        var before = Task();
        var evt = Append("a", [
            Part.FromText("héllo wörld"),
            Part.FromBytes(new byte[] { 1, 2, 3, 4, 5 }, "application/octet-stream", "f.bin"),
            Part.FromUrl(new Uri("https://example.com/x")),
            Part.FromData(JsonSerializer.SerializeToElement(new { a = 1 })),
            new TextPart("with metadata") { Metadata = Meta(), Filename = "n.txt" },
        ]);

        var added = TaskSize.AppendedBytes(evt);

        Assert.NotNull(added);
        var after = TaskExecutionEngine.ApplyToCopy(before, evt)!;
        Assert.Equal(TaskSize.Measure(before) + added, TaskSize.Measure(after));
    }

    [Fact]
    public void Carried_size_equals_a_full_measure_for_an_append_to_a_missing_artifact()
    {
        var before = Task();
        var evt = Append("new", [Part.FromText("xyz")]);

        var added = TaskSize.AppendedBytes(evt);

        Assert.NotNull(added);
        Assert.Equal(TaskSize.Measure(before) + added, TaskSize.Measure(TaskExecutionEngine.ApplyToCopy(before, evt)!));
    }

    [Fact]
    public void An_append_that_changes_more_than_parts_is_measured_in_full()
    {
        Assert.Null(TaskSize.AppendedBytes(Append("a", [Part.FromText("x")], name: "renamed")));
        Assert.Null(TaskSize.AppendedBytes(Append("a", [Part.FromText("x")], meta: Meta())));
        Assert.Null(TaskSize.AppendedBytes(Append("a", [Part.FromText("x")], eventMeta: Meta())));
        Assert.Null(TaskSize.AppendedBytes(Append("a", [Part.FromText("x")], append: false)));
    }

    private static StreamResponse Status(Message? message, Dictionary<string, JsonElement>? eventMeta = null) =>
        StreamResponse.Of(new TaskStatusUpdateEvent
        {
            TaskId = "t", ContextId = "c", Metadata = eventMeta,
            Status = new AgentTaskStatus { State = TaskState.Working, Message = message },
        });

    [Fact]
    public void Carried_size_equals_a_full_measure_for_a_status_message()
    {
        var before = Task();
        var message = new Message
        {
            MessageId = "s", Role = Role.Agent, Metadata = Meta(),
            Parts = [Part.FromText("wörking"), new TextPart("more") { Metadata = Meta(), Filename = "n.txt" }],
        };
        message.Metadata![A2AProtocol.MessageRecordedAtKey] = JsonSerializer.SerializeToElement("2026-01-01T00:00:00Z");
        var evt = Status(message);

        var added = TaskSize.AppendedBytes(evt);

        Assert.NotNull(added);
        Assert.Equal(TaskSize.Measure(before) + added, TaskSize.Measure(TaskExecutionEngine.ApplyToCopy(before, evt)!));
    }

    [Fact]
    public void A_status_update_with_event_metadata_or_no_message_is_not_carried()
    {
        var message = new Message { MessageId = "s", Role = Role.Agent, Parts = [Part.FromText("x")] };
        Assert.Null(TaskSize.AppendedBytes(Status(message, Meta())));
        Assert.Null(TaskSize.AppendedBytes(Status(null)));
    }
}
