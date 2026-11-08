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
}
