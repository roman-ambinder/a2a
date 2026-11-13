using System.Collections.Concurrent;
using System.Text.Json;
using A2A.Json;
using A2A.Server.Streaming;

namespace A2A.Tests;

/// <summary>
/// ADR-004 follow-up: <see cref="A2AJson.Options" /> writes <see cref="AgentTask.History" />, <see cref="AgentTask.Timeline" />
/// and <see cref="Artifact.Parts" /> from the shared version, without building the lists. These tests pin that the bytes
/// are the ones the getter contract wrote (the options without the modifier, <see cref="Reference" />), that writing never
/// builds a list, and that reading gives the same model as before.
/// </summary>
public class SharedListJsonTests
{
    // The contract before the change: the generated properties, read through the public getters.
    private static readonly JsonSerializerOptions Reference = A2AJson.CreateOptions(false, writeSharedLists: false);
    private static readonly JsonSerializerOptions ReferenceV10 = A2AJson.CreateOptions(true, writeSharedLists: false);

    private static Message M(string id) => new() { MessageId = id, Role = Role.Agent, Parts = [Part.FromText(id)] };

    private static TimelineEntry E(long generation) =>
        TimelineEntry.FromStatus(new AgentTaskStatus { State = TaskState.Working }, generation);

    private static AgentTask NewTask() => new() { Id = "t", ContextId = "c", Status = new() { State = TaskState.Working } };

    private static byte[] Ref<T>(T value, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToUtf8Bytes(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)
            (options ?? Reference).GetTypeInfo(typeof(T)));

    private static byte[] New<T>(T value, JsonSerializerOptions? options = null) =>
        JsonSerializer.SerializeToUtf8Bytes(value, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)
            (options ?? A2AJson.Options).GetTypeInfo(typeof(T)));

    /// <summary>Which lists of the task (and of each artifact) are built.</summary>
    private static string Built(AgentTask task) =>
        $"{task.HistoryStore.IsMaterialized}/{task.TimelineStore.IsMaterialized}/" +
        string.Join(",", task.Artifacts?.Select(a => a?.PartsStore.IsMaterialized.ToString() ?? "null") ?? []);

    /// <summary>
    /// Serializes with the shared options (both versions) and checks nothing got built, then compares with the getter
    /// contract (which builds the lists, so it goes last).
    /// </summary>
    private static void AssertSameBytes(AgentTask task)
    {
        var before = Built(task);
        var json = New(task);
        var v10 = New(task, A2AJson.V10Options);
        Assert.Equal(before, Built(task));
        Assert.Equal(Ref(task), json);
        Assert.Equal(Ref(task, ReferenceV10), v10);
        // Once built, the shared options write the list itself: still the same bytes.
        Assert.Equal(json, New(task));
    }

    private static AgentTask SharedTask(int messages, int timelineMax, int parts)
    {
        var task = NewTask();
        task.Artifacts = [new Artifact { ArtifactId = "a", Name = "out", Parts = [Part.FromText("p0")] }];
        for (var i = 0; i < messages; i++)
        {
            task = task.CopyForUpdate();
            task.AppendToHistory(M($"m{i}"));
            task.AppendToTimeline(E(i + 1), timelineMax);
        }

        for (var i = 1; i < parts; i++)
        {
            task = task.CopyForUpdate();
            task.Artifacts![0] = task.Artifacts[0].CopyWithParts([Part.FromText($"p{i}")]);
        }

        return task;
    }

    [Fact]
    public async Task Writing_does_not_build_the_lists_on_any_path()
    {
        var task = SharedTask(30, 10, 20);
        var artifact = task.Artifacts![0];
        Assert.Equal("False/False/False", Built(task));

        var json = A2AJson.Serialize(task);
        _ = A2AJson.SerializeToUtf8Bytes(task);
        _ = A2AJson.SerializeToElement(task);
        _ = JsonSerializer.Serialize(task, A2AJson.GetTypeInfo<AgentTask>("1.0"));
        _ = A2AJson.Serialize(artifact);
        _ = A2AJson.Serialize(StreamResponse.Of(task));
        _ = A2AJson.Serialize(StreamResponse.Of(new TaskArtifactUpdateEvent { TaskId = "t", Artifact = artifact }));
        _ = A2AJson.Serialize(new TaskResponse(task));
        var encoded = EventEncoder.Encode(0, StreamResponse.Of(task));
        _ = EventEncoder.MeasureUtf8(StreamResponse.Of(task));
        using (var stream = new MemoryStream())
        {
            await JsonSerializer.SerializeAsync(stream, task, A2AJson.GetTypeInfo<AgentTask>(), TestContext.Current.CancellationToken);
            Assert.Equal(json, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }

        Assert.Equal("False/False/False", Built(task));
        Assert.False(artifact.PartsStore.IsMaterialized);
        var encoderReference = A2AJson.OptionsFor(A2AProtocol.Version) == A2AJson.V10Options ? ReferenceV10 : Reference;
        Assert.Equal(Ref<StreamResponse>(StreamResponse.Of(task), encoderReference), encoded.Json.ToArray());
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(json), Ref(task));
        Assert.Equal("True/True/True", Built(task)); // the getter contract builds them
    }

    [Fact]
    public void Every_list_state_writes_the_getter_contract_bytes()
    {
        // Null lists, and an artifact list that holds a null.
        var none = NewTask();
        AssertSameBytes(none);
        AssertSameBytes(none.CopyForUpdate());
        none.Artifacts = [null!];
        AssertSameBytes(none);

        // Empty lists, set and shared.
        var empty = NewTask();
        empty.History = [];
        empty.Timeline = [];
        empty.Artifacts = [new Artifact { ArtifactId = "e", Parts = [] }];
        AssertSameBytes(empty);
        AssertSameBytes(empty.CopyForUpdate());
        var emptyShared = empty.CopyForUpdate();
        emptyShared.Artifacts![0] = emptyShared.Artifacts[0].CopyWithParts([]);
        AssertSameBytes(emptyShared);

        // An artifact whose parts were set to null (not valid on the wire, but the getter contract wrote it).
        var nullParts = NewTask();
        nullParts.Artifacts = [new Artifact { ArtifactId = "n", Parts = null! }];
        AssertSameBytes(nullParts);

        // Shared only, with the timeline trimmed by KeepLast (the start moved) and grown past several arrays.
        var shared = SharedTask(200, 50, 100);
        AssertSameBytes(shared);

        // Forks from an old version: each keeps its own items.
        var old = SharedTask(20, 5, 5);
        var forkA = old.CopyForUpdate();
        forkA.AppendToHistory(M("a"));
        forkA.AppendToTimeline(E(-1), 5);
        var forkB = old.CopyForUpdate();
        forkB.AppendToHistory(M("b"));
        forkB.Artifacts![0] = forkB.Artifacts[0].CopyWithParts([Part.FromText("b")]);
        AssertSameBytes(old);
        AssertSameBytes(forkA);
        AssertSameBytes(forkB);

        // Built: read, then changed by the owner of the copy, then shared again by a copy.
        var built = SharedTask(10, 0, 3);
        built.History!.Add(M("owner"));
        built.Timeline!.RemoveAt(0);
        built.Artifacts![0].Parts.Add(Part.FromText("owner"));
        AssertSameBytes(built);
        var afterBuilt = built.CopyForUpdate();
        afterBuilt.AppendToHistory(M("next"));
        afterBuilt.Artifacts![0] = afterBuilt.Artifacts[0].CopyWithParts([Part.FromText("next")]);
        AssertSameBytes(afterBuilt);

        // Set after a shared version: the set list wins.
        var set = SharedTask(5, 0, 2);
        set.History = [M("set")];
        AssertSameBytes(set);

        // A 1.1 artifact (generations, role) and a task with metadata: the order of every member is kept.
        var full = SharedTask(3, 0, 3);
        full.Generation = 7;
        full.Metadata = new() { ["k"] = JsonDocument.Parse("1").RootElement };
        full.Artifacts![0].StartGeneration = 2;
        full.Artifacts[0].EndGeneration = 5;
        full.Artifacts[0].Role = Role.Agent;
        full.Artifacts[0].Metadata = new() { ["m"] = JsonDocument.Parse("\"v\"").RootElement };
        full.Artifacts[0].Extensions = ["urn:x"];
        full.Artifacts[0].Description = "d";
        AssertSameBytes(full);
    }

    [Fact]
    public void Random_updates_write_the_getter_contract_bytes_without_building()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var task = NewTask();
            var versions = new List<AgentTask>();
            for (var step = 0; step < 300; step++)
            {
                if (versions.Count > 0 && random.Next(10) == 0) task = versions[random.Next(versions.Count)];
                if (random.Next(10) == 0) _ = task.History;
                if (random.Next(10) == 0) _ = task.Timeline;
                if (random.Next(10) == 0) _ = task.Artifacts?.FirstOrDefault()?.Parts;
                var next = task.CopyForUpdate();
                switch (random.Next(3))
                {
                    case 0:
                        next.AppendToHistory(M($"m{step}"));
                        break;
                    case 1:
                        next.AppendToTimeline(E(step), 25);
                        break;
                    default:
                        var part = Part.FromText($"p{step}");
                        if (next.Artifacts is not { Count: > 0 }) next.Artifacts = [new Artifact { ArtifactId = "a", Parts = [part] }];
                        else next.Artifacts[0] = next.Artifacts[0].CopyWithParts([part]);
                        break;
                }

                versions.Add(next);
                task = next;
            }

            // Write every version first (nothing may get built), then compare with the getter contract.
            var states = versions.Select(Built).ToList();
            var written = versions.Select(v => (New(v), New(v, A2AJson.V10Options))).ToList();
            Assert.Equal(states, versions.Select(Built).ToList());
            for (var i = 0; i < versions.Count; i++)
            {
                Assert.Equal(Ref(versions[i]), written[i].Item1);
                Assert.Equal(Ref(versions[i], ReferenceV10), written[i].Item2);
            }
        }
    }

    [Theory]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"}}""")]
    [InlineData("""{"id":"t","contextId":"c","status":{"state":"TASK_STATE_WORKING"},"history":[],"timeline":[]}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"history":null,"timeline":null,"artifacts":null}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"history":[{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hi"}]}],"timeline":[{"status":{"state":"TASK_STATE_WORKING"},"generation":"2"}],"generation":"2"}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"artifacts":[{"artifactId":"a"},{"artifactId":"b","parts":[]},{"artifactId":"c","parts":null},{"parts":[{"text":"x"}],"artifactId":"d","name":"n","startGeneration":"1","role":"ROLE_AGENT"}]}""")]
    [InlineData("""{"status":{"state":"TASK_STATE_WORKING"},"history":[],"id":"t","unknown":{"a":[1]}}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"history":[null]}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"history":{}}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"timeline":"x"}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"artifacts":[{"name":"no id","parts":[]}]}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"artifacts":[{"artifactId":"a","parts":[{"text":"x"}],"parts":[]}]}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"history":[],"history":[]}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"artifacts":[{"artifactId":"a","Parts":[{"text":"x"}]}]}""")]
    [InlineData("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"artifacts":[{"artifactId":"a","parts":[{"bogus":1}]}]}""")]
    public void Reading_gives_what_the_getter_contract_read(string json)
    {
        var expected = Read(json, Reference);
        var actual = Read(json, A2AJson.Options);
        Assert.Equal(expected, actual);
        Assert.Equal(Read(json, ReferenceV10), Read(json, A2AJson.V10Options));

        static string Read(string json, JsonSerializerOptions options)
        {
            try
            {
                var task = JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<AgentTask>)
                    options.GetTypeInfo(typeof(AgentTask)))!;
                // Lists read from JSON are plain lists, as before: the getter returns them as they are.
                Assert.True(task.HistoryStore.IsMaterialized || task.History is null);
                Assert.True(task.TimelineStore.IsMaterialized || task.Timeline is null);
                foreach (var a in task.Artifacts ?? []) Assert.True(a.PartsStore.IsMaterialized);
                return "ok " + System.Text.Encoding.UTF8.GetString(Ref(task)) +
                       $" history:{task.History?.GetType().Name} parts:{string.Join(",", task.Artifacts?.Select(a => a.Parts?.GetType().Name) ?? [])}";
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }
    }

    [Fact]
    public void Artifact_reads_the_same_as_a_root_value()
    {
        foreach (var json in new[]
                 {
                     """{"artifactId":"a"}""", """{"artifactId":"a","parts":[{"text":"x"},{"data":{"k":1}}]}""",
                     """{"parts":[]}""", """{"artifactId":"a","parts":null}""", """{"artifactId":1}"""
                 })
        {
            Assert.Equal(Read(json, Reference), Read(json, A2AJson.Options));
        }

        static string Read(string json, JsonSerializerOptions options)
        {
            try
            {
                var a = JsonSerializer.Deserialize(json, (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Artifact>)
                    options.GetTypeInfo(typeof(Artifact)))!;
                return System.Text.Encoding.UTF8.GetString(Ref(a));
            }
            catch (Exception ex)
            {
                return Failure(ex);
            }
        }
    }

    /// <summary>
    /// A failed read fails the same way: the same exception at the same path and position. Only the text of a conversion
    /// error can name another type (see <see cref="Conversion_errors_name_the_read_only_list_type" />).
    /// </summary>
    private static string Failure(Exception ex) => ex is JsonException json
        ? $"{ex.GetType().Name} at {json.Path} {json.LineNumber}:{json.BytePositionInLine}"
        : ex.GetType().Name + ": " + ex.Message;

    [Fact]
    public void Conversion_errors_name_the_read_only_list_type()
    {
        // What changed in a failed read, as the ADR records: the conversion message of the three properties names the
        // new property type, and an artifact member that was a constructor argument names its own type, not Artifact.
        var (before, after) = Messages<AgentTask>("""{"id":"t","status":{"state":"TASK_STATE_WORKING"},"history":{}}""");
        Assert.StartsWith("The JSON value could not be converted to System.Collections.Generic.List`1[A2A.Message]. Path: $.history", before);
        Assert.Equal(before.Replace("List`1", "IReadOnlyList`1", StringComparison.Ordinal), after);
        (before, after) = Messages<Artifact>("""{"artifactId":1}""");
        Assert.StartsWith("The JSON value could not be converted to A2A.Artifact. Path: $.artifactId", before);
        Assert.Equal(before.Replace("A2A.Artifact", "System.String", StringComparison.Ordinal), after);

        static (string, string) Messages<T>(string json) => (Message<T>(json, Reference), Message<T>(json, A2AJson.Options));

        static string Message<T>(string json, JsonSerializerOptions options) =>
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json,
                (System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)))).Message;
    }

    [Fact]
    public void Contract_keeps_the_member_order_and_settings()
    {
        foreach (var type in new[] { typeof(AgentTask), typeof(Artifact) })
        {
            var expected = Reference.GetTypeInfo(type).Properties;
            var actual = A2AJson.Options.GetTypeInfo(type).Properties;
            Assert.Equal(expected.Select(p => p.Name), actual.Select(p => p.Name));
            Assert.Equal(expected.Select(p => p.IsRequired), actual.Select(p => p.IsRequired));
            Assert.Equal(expected.Select(p => p.Order), actual.Select(p => p.Order));
            Assert.Equal(expected.Select(p => p.AttributeProvider), actual.Select(p => p.AttributeProvider));
        }

        Assert.Equal(typeof(IReadOnlyList<Part>),
            A2AJson.Options.GetTypeInfo(typeof(Artifact)).Properties.Single(p => p.Name == "parts").PropertyType);
    }

    [Fact]
    public async Task Writing_while_another_thread_appends_sees_each_version_as_it_was()
    {
        var root = SharedTask(50, 40, 50);
        var rootJson = Ref(root.DeepCopy()); // the bytes of the root, from a copy so the root stays shared
        var versions = new ConcurrentQueue<(AgentTask Task, int Messages, int Parts, string Last)>();
        var failures = new ConcurrentQueue<string>();
        using var done = new CancellationTokenSource();

        // The appender: extends the newest version, and now and then forks from the root, so slots after the root are
        // claimed while the readers write it.
        var appender = Task.Run(() =>
        {
            var task = root;
            var messages = 50;
            var parts = 50;
            for (var i = 0; i < 3000; i++)
            {
                if (i % 500 == 0)
                {
                    task = root;
                    messages = 50;
                    parts = 50;
                }

                var next = task.CopyForUpdate();
                next.AppendToHistory(M($"x{i}"));
                next.AppendToTimeline(E(1000 + i), 40);
                next.Artifacts![0] = next.Artifacts[0].CopyWithParts([Part.FromText($"x{i}")]);
                task = next;
                messages++;
                parts++;
                versions.Enqueue((task, messages, parts, $"x{i}"));
            }

            done.Cancel();
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!done.IsCancellationRequested || !versions.IsEmpty)
            {
                if (!New(root).AsSpan().SequenceEqual(rootJson)) failures.Enqueue("the root changed");
                if (!versions.TryDequeue(out var v)) continue;
                using var doc = JsonDocument.Parse(New(v.Task));
                var history = doc.RootElement.GetProperty("history");
                var parts = doc.RootElement.GetProperty("artifacts")[0].GetProperty("parts");
                var timeline = doc.RootElement.GetProperty("timeline");
                if (history.GetArrayLength() != v.Messages || parts.GetArrayLength() != v.Parts ||
                    timeline.GetArrayLength() != 40 ||
                    history[v.Messages - 1].GetProperty("messageId").GetString() != v.Last)
                    failures.Enqueue($"version {v.Messages} wrote {history.GetArrayLength()} messages, {parts.GetArrayLength()} parts");
            }
        })).ToArray();

        await Task.WhenAll([appender, .. readers]).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Empty(failures);
        Assert.Equal("False/False/False", Built(root));
        Assert.Equal(rootJson, Ref(root));
    }
}
