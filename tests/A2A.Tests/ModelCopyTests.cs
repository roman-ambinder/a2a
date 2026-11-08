using System.Collections;
using System.Text.Json;
using A2A.Json;

namespace A2A.Tests;

public class ModelCopyTests
{
    private static JsonElement J(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message Msg(string id, byte[]? raw = null) => new()
    {
        MessageId = id,
        ContextId = "c",
        TaskId = "t",
        Role = Role.Agent,
        Parts =
        [
            Part.FromText("hi", "text/plain") with { Metadata = new() { ["a"] = J("1") } },
            Part.FromBytes(raw ?? [1, 2, 3], "application/octet-stream", "a.bin") with { Metadata = new() { ["r"] = J("[2]") } },
            Part.FromUrl(new Uri("https://x/y"), "image/png", "y.png") with { Metadata = new() { ["u"] = J("\"s\"") } },
            Part.FromData(J("{\"k\":[1,2]}")) with { Metadata = new() { ["d"] = J("{\"n\":null}") } },
        ],
        Metadata = new() { ["m"] = J("{\"x\":1}") },
        Extensions = ["e1"],
        ReferenceTaskIds = ["r1"],
    };

    private static Artifact Art(string id) => new()
    {
        ArtifactId = id,
        Name = "n",
        Description = "d",
        Parts = [Part.FromText("a"), Part.FromData(J("[1]"))],
        Metadata = new() { ["am"] = J("true") },
        Extensions = ["ae"],
    };

    /// <summary>A message whose optional collections are all null.</summary>
    private static Message BareMsg(string id) => new()
    {
        MessageId = id,
        Role = Role.User,
        Parts = [Part.FromText("bare")],
    };

    /// <summary>An artifact whose optional collections are all null.</summary>
    private static Artifact BareArt(string id) => new()
    {
        ArtifactId = id,
        Parts = [Part.FromText("bare")],
    };

    private static AgentTask Task() => new()
    {
        Id = "t",
        ContextId = "c",
        Status = new() { State = TaskState.Working, Message = Msg("s"), Timestamp = DateTimeOffset.UnixEpoch },
        Artifacts = [Art("a1"), Art("a2"), BareArt("a3")],
        History = [Msg("h1"), Msg("h2"), BareMsg("h3")],
        Metadata = new() { ["tm"] = J("\"v\"") },
    };

    [Fact]
    public void CopyForUpdate_shares_messages_but_not_lists()
    {
        var t = Task();
        var c = t.CopyForUpdate();
        Assert.NotSame(t, c);
        Assert.NotSame(t.Status, c.Status);
        Assert.Same(t.Status.Message, c.Status.Message);
        Assert.NotSame(t.History, c.History);
        Assert.NotSame(t.Artifacts, c.Artifacts);
        Assert.NotSame(t.Metadata, c.Metadata);
        for (var i = 0; i < t.History!.Count; i++) Assert.Same(t.History[i], c.History![i]);
        for (var i = 0; i < t.Artifacts!.Count; i++) Assert.Same(t.Artifacts[i], c.Artifacts![i]);
        c.History!.Add(Msg("h3"));
        Assert.Equal(3, t.History.Count);
        Assert.Equal(A2AJson.Serialize(t), A2AJson.Serialize(t.CopyForUpdate()));
    }

    [Fact]
    public void CopyForUpdate_keeps_null_collections_null()
    {
        var t = new AgentTask { Id = "t", Status = new() { State = TaskState.Submitted } };
        var c = t.CopyForUpdate();
        Assert.Null(c.History);
        Assert.Null(c.Artifacts);
        Assert.Null(c.Metadata);
        Assert.Null(c.Status.Message);
    }

    [Fact]
    public void CopyWithParts_leaves_original_untouched()
    {
        var a = Art("a1");
        var extra = Part.FromText("more");
        var c = a.CopyWithParts([extra]);
        Assert.NotSame(a, c);
        Assert.Equal(2, a.Parts.Count);
        Assert.Equal(3, c.Parts.Count);
        Assert.Same(a.Parts[0], c.Parts[0]);
        Assert.Same(extra, c.Parts[2]);
        Assert.NotSame(a.Metadata, c.Metadata);
        Assert.Equal(a.Metadata!["am"].GetRawText(), c.Metadata!["am"].GetRawText());
        Assert.Equal(a.ArtifactId, c.ArtifactId);
        Assert.Equal(a.Name, c.Name);
        Assert.Equal(a.Description, c.Description);
        Assert.Equal(a.Extensions, c.Extensions);
    }

    [Fact]
    public void DeepCopy_equals_json_clone()
    {
        var t = Task();
        Assert.Equal(A2AJson.Serialize(A2AJson.Clone(t)), A2AJson.Serialize(t.DeepCopy()));
        Assert.Equal(A2AJson.Serialize(t), A2AJson.Serialize(t.DeepCopy()));
        var bare = new AgentTask { Id = "t", Status = new() { State = TaskState.Submitted } };
        Assert.Equal(A2AJson.Serialize(bare), A2AJson.Serialize(bare.DeepCopy()));
    }

    [Fact]
    public void DeepCopy_shares_nothing()
    {
        var t = Task();
        var c = t.DeepCopy();
        NotSame(t, c);
        NotSame(t.Status, c.Status);
        NotSameMessage(t.Status.Message!, c.Status.Message!);
        NotSameDict(t.Metadata, c.Metadata);
        NotSameList(t.History, c.History);
        for (var i = 0; i < t.History!.Count; i++) NotSameMessage(t.History[i], c.History![i]);
        NotSameList(t.Artifacts, c.Artifacts);
        for (var i = 0; i < t.Artifacts!.Count; i++)
        {
            var o = t.Artifacts[i];
            var n = c.Artifacts![i];
            NotSame(o, n);
            NotSameList(o.Parts, n.Parts);
            NotSameList(o.Extensions, n.Extensions);
            NotSameDict(o.Metadata, n.Metadata);
            for (var j = 0; j < o.Parts.Count; j++) NotSamePart(o.Parts[j], n.Parts[j]);
        }
    }

    [Fact]
    public void DeepCopy_raw_bytes_and_json_are_independent()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var doc = JsonDocument.Parse("{\"k\":1}");
        var m = Msg("m", bytes);
        m.Parts[3] = Part.FromData(doc.RootElement);
        var t = new AgentTask { Id = "t", Status = new() { State = TaskState.Working }, History = [m] };
        var c = t.DeepCopy();
        bytes[0] = 99;
        doc.Dispose();
        var raw = (RawPart)c.History![0].Parts[1];
        Assert.Equal(1, raw.Raw.Span[0]);
        Assert.Equal("{\"k\":1}", ((DataPart)c.History[0].Parts[3]).Data.GetRawText());
    }

    [Fact]
    public void Message_DeepCopy_equals_json_clone()
    {
        foreach (var m in new[] { Msg("m"), BareMsg("b") })
        {
            Assert.Equal(A2AJson.Serialize(A2AJson.Clone(m)), A2AJson.Serialize(m.DeepCopy()));
            Assert.Equal(A2AJson.Serialize(m), A2AJson.Serialize(m.DeepCopy()));
        }
    }

    [Fact]
    public void Message_DeepCopy_shares_nothing()
    {
        foreach (var m in new[] { Msg("m"), BareMsg("b") }) NotSameMessage(m, m.DeepCopy());
    }

    private static TaskPushNotificationConfig PushConfig() => new()
    {
        Tenant = "ten",
        Id = "c1",
        TaskId = "t",
        Url = "https://hooks.test/x",
        Token = "tok",
        Authentication = new AuthenticationInfo { Scheme = "Basic", Credentials = "secret" },
    };

    [Fact]
    public void PushConfig_DeepCopy_equals_json_clone()
    {
        foreach (var c in new[] { PushConfig(), new TaskPushNotificationConfig { Url = "https://hooks.test/bare" } })
        {
            Assert.Equal(A2AJson.Serialize(A2AJson.Clone(c)), A2AJson.Serialize(c.DeepCopy()));
            Assert.Equal(A2AJson.Serialize(c), A2AJson.Serialize(c.DeepCopy()));
        }
    }

    [Fact]
    public void PushConfig_DeepCopy_shares_nothing()
    {
        var c = PushConfig();
        var copy = c.DeepCopy();
        NotSame(c, copy);
        NotSame(c.Authentication, copy.Authentication);
        copy.Token = "changed";
        copy.Authentication!.Credentials = "changed";
        Assert.Equal("tok", c.Token);
        Assert.Equal("secret", c.Authentication!.Credentials);
        Assert.Null(new TaskPushNotificationConfig { Url = "https://hooks.test/bare" }.DeepCopy().Authentication);
    }

    private static StreamResponse[] Events() =>
    [
        StreamResponse.Of(Task()),
        StreamResponse.Of(Msg("m")),
        StreamResponse.Of(new TaskStatusUpdateEvent
        {
            TaskId = "t", ContextId = "c",
            Status = new() { State = TaskState.InputRequired, Message = Msg("s"), Timestamp = DateTimeOffset.UnixEpoch },
            Metadata = new() { ["sm"] = J("1") },
        }),
        StreamResponse.Of(new TaskStatusUpdateEvent { TaskId = "t", ContextId = "c", Status = new() { State = TaskState.Working } }),
        StreamResponse.Of(new TaskArtifactUpdateEvent
        {
            TaskId = "t", ContextId = "c", Artifact = Art("a"), Append = true, LastChunk = true, Metadata = new() { ["am"] = J("2") },
        }),
        StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "t", ContextId = "c", Artifact = BareArt("b") }),
    ];

    [Fact]
    public void StreamResponse_DeepCopy_equals_json_clone()
    {
        foreach (var e in Events())
        {
            var copy = e.DeepCopy();
            Assert.Equal(e.GetType(), copy.GetType());
            Assert.Equal(A2AJson.Serialize(A2AJson.Clone(e)), A2AJson.Serialize(copy));
            Assert.Equal(A2AJson.Serialize(e), A2AJson.Serialize(copy));
        }
    }

    [Fact]
    public void StreamResponse_DeepCopy_shares_nothing()
    {
        foreach (var e in Events())
        {
            var copy = e.DeepCopy();
            NotSame(e, copy);
            switch (e, copy)
            {
                case (TaskEvent o, TaskEvent n):
                    NotSame(o.Task, n.Task);
                    NotSame(o.Task.Status, n.Task.Status);
                    NotSameMessage(o.Task.Status.Message!, n.Task.Status.Message!);
                    break;
                case (MessageEvent o, MessageEvent n):
                    NotSameMessage(o.Message, n.Message);
                    break;
                case (StatusUpdateEvent o, StatusUpdateEvent n):
                    NotSame(o.StatusUpdate, n.StatusUpdate);
                    NotSame(o.StatusUpdate.Status, n.StatusUpdate.Status);
                    if (o.StatusUpdate.Status.Message is { } om) NotSameMessage(om, n.StatusUpdate.Status.Message!);
                    else Assert.Null(n.StatusUpdate.Status.Message);
                    NotSameDict(o.StatusUpdate.Metadata, n.StatusUpdate.Metadata);
                    break;
                case (ArtifactUpdateEvent o, ArtifactUpdateEvent n):
                    NotSame(o.ArtifactUpdate, n.ArtifactUpdate);
                    NotSame(o.ArtifactUpdate.Artifact, n.ArtifactUpdate.Artifact);
                    NotSameList(o.ArtifactUpdate.Artifact.Parts, n.ArtifactUpdate.Artifact.Parts);
                    NotSameList(o.ArtifactUpdate.Artifact.Extensions, n.ArtifactUpdate.Artifact.Extensions);
                    NotSameDict(o.ArtifactUpdate.Artifact.Metadata, n.ArtifactUpdate.Artifact.Metadata);
                    for (var i = 0; i < o.ArtifactUpdate.Artifact.Parts.Count; i++)
                        NotSamePart(o.ArtifactUpdate.Artifact.Parts[i], n.ArtifactUpdate.Artifact.Parts[i]);
                    NotSameDict(o.ArtifactUpdate.Metadata, n.ArtifactUpdate.Metadata);
                    break;
                default:
                    Assert.Fail($"copy changed the event kind: {e.GetType().Name} -> {copy.GetType().Name}");
                    break;
            }
        }
    }

    private static void NotSame(object? a, object? b)
    {
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
    }

    private static void NotSameList(IList? a, IList? b)
    {
        if (a is null) { Assert.Null(b); return; }
        NotSame(a, b);
    }

    private static void NotSameDict(IDictionary? a, IDictionary? b)
    {
        if (a is null) { Assert.Null(b); return; }
        NotSame(a, b);
    }

    private static void NotSameMessage(Message o, Message n)
    {
        NotSame(o, n);
        NotSameList(o.Parts, n.Parts);
        NotSameList(o.Extensions, n.Extensions);
        NotSameList(o.ReferenceTaskIds, n.ReferenceTaskIds);
        NotSameDict(o.Metadata, n.Metadata);
        for (var i = 0; i < o.Parts.Count; i++) NotSamePart(o.Parts[i], n.Parts[i]);
    }

    private static void NotSamePart(Part o, Part n)
    {
        NotSame(o, n);
        Assert.Equal(o.GetType(), n.GetType());
        NotSameDict(o.Metadata, n.Metadata);
        if (o is RawPart ro)
        {
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(ro.Raw, out var oa));
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray(((RawPart)n).Raw, out var na));
            Assert.NotSame(oa.Array, na.Array);
        }
    }
}
