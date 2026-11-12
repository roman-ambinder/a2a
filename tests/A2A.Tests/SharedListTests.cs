using System.Text.Json;
using A2A.Json;
using A2A.Server.Execution;

namespace A2A.Tests;

/// <summary>
/// ADR-004: the task lists (<see cref="AgentTask.History"/>, <see cref="AgentTask.Timeline"/>, <see cref="Artifact.Parts"/>)
/// share one buffer between versions until they are read. These tests pin what must not change: every version keeps its
/// content, forks are independent, readers of one instance get one list, and the JSON is the same as with plain lists.
/// </summary>
public class SharedListTests
{
    private static Message M(string id) => new() { MessageId = id, Role = Role.Agent, Parts = [Part.FromText(id)] };

    private static TimelineEntry E(long generation) =>
        TimelineEntry.FromStatus(new AgentTaskStatus { State = TaskState.Working }, generation);

    private static AgentTask NewTask() => new() { Id = "t", ContextId = "c", Status = new() { State = TaskState.Working } };

    private static List<string> Ids(List<Message>? history) => history?.Select(m => m.MessageId).ToList() ?? [];

    private static List<int> Items(AppendList<int> list) => Enumerable.Range(0, list.Count).Select(i => list[i]).ToList();

    // ---- AppendList ----

    [Fact]
    public void AppendList_versions_keep_their_items()
    {
        var v0 = AppendList<int>.Empty;
        var v1 = v0.Add(1);
        var v2 = v1.Add(2);
        var v3 = v2.AddRange([3, 4]);
        Assert.Empty(Items(v0));
        Assert.Equal([1], Items(v1));
        Assert.Equal([1, 2], Items(v2));
        Assert.Equal([1, 2, 3, 4], Items(v3));
        Assert.Same(v3, v3.AddRange([])); // nothing to add: the same version
        Assert.Throws<ArgumentOutOfRangeException>(() => v2[2]);
        Assert.Throws<ArgumentOutOfRangeException>(() => v2[-1]);
    }

    [Fact]
    public void AppendList_fork_from_an_old_version_copies_and_does_not_overwrite()
    {
        var shared = AppendList<int>.Empty.Add(1).Add(2);
        var a = shared.Add(3);  // the newest version: appends in place
        var b = shared.Add(30); // a fork: the slot after 2 is a's, so b copies
        var a2 = a.Add(4);
        var b2 = b.Add(40);
        var b3 = b.AddRange([50, 60]); // a second fork from b
        Assert.Equal([1, 2], Items(shared));
        Assert.Equal([1, 2, 3], Items(a));
        Assert.Equal([1, 2, 3, 4], Items(a2));
        Assert.Equal([1, 2, 30], Items(b));
        Assert.Equal([1, 2, 30, 40], Items(b2));
        Assert.Equal([1, 2, 30, 50, 60], Items(b3));
    }

    [Fact]
    public void AppendList_KeepLast_moves_the_start_and_forks_stay_intact()
    {
        var full = AppendList<int>.Empty;
        for (var i = 0; i < 10; i++) full = full.Add(i);
        Assert.Same(full, full.KeepLast(10));
        Assert.Same(full, full.KeepLast(0)); // zero keeps everything
        var trimmed = full.KeepLast(3);
        Assert.Equal([7, 8, 9], Items(trimmed));
        var next = trimmed.Add(10).KeepLast(3);
        Assert.Equal([8, 9, 10], Items(next));
        // The untrimmed version is unchanged, and a fork from it does not see the trimmed line's append.
        Assert.Equal(Enumerable.Range(0, 10).ToList(), Items(full));
        Assert.Equal([.. Enumerable.Range(0, 10), 99], Items(full.Add(99)));
        Assert.Equal([8, 9, 10], Items(next));
        Assert.Equal([8, 9, 10], next.ToList());
    }

    [Fact]
    public void AppendList_CopyOf_does_not_follow_the_list()
    {
        var list = new List<int> { 1, 2 };
        var v = AppendList<int>.CopyOf(list, 1);
        list.Add(3);
        list[0] = 100;
        Assert.Equal([1, 2], Items(v));
        Assert.Equal([1, 2, 4], Items(v.Add(4)));
    }

    /// <summary>
    /// Many threads append from one snapshot at the same time, and keep appending to their own line. One of them may win the
    /// shared buffer; the others copy. Every line ends with exactly its own items, and the snapshot is unchanged.
    /// </summary>
    [Fact]
    public void AppendList_concurrent_appenders_from_one_snapshot()
    {
        const int threads = 8, perThread = 10_000;
        for (var round = 0; round < 20; round++)
        {
            var snapshot = AppendList<int>.Empty;
            for (var i = 0; i < 100; i++) snapshot = snapshot.Add(i);
            var results = new AppendList<int>[threads];
            using var start = new Barrier(threads);
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                start.SignalAndWait();
                var mine = snapshot;
                for (var i = 0; i < perThread; i++)
                {
                    mine = i % 7 == 3 ? mine.AddRange([Value(t, i)]) : mine.Add(Value(t, i)); // the first append races on one slot
                    // Also race on one shared intermediate version now and then.
                    if (i == perThread / 2) _ = snapshot.Add(-1);
                }

                results[t] = mine;
            })).ToList();
            workers.ForEach(w => w.Start());
            workers.ForEach(w => w.Join());

            Assert.Equal(Enumerable.Range(0, 100).ToList(), Items(snapshot));
            for (var t = 0; t < threads; t++)
            {
                var items = Items(results[t]);
                Assert.Equal(100 + perThread, items.Count);
                Assert.Equal(Enumerable.Range(0, 100), items.Take(100));
                Assert.Equal(Enumerable.Range(0, perThread).Select(i => Value(t, i)), items.Skip(100));
            }
        }

        static int Value(int thread, int i) => (thread + 1) * 1_000_000 + i;
    }

    /// <summary>
    /// Threads race to append to one version that has room: at most one may take the free slot, so every result ends with
    /// its own item and the version is unchanged. Run many times, so the race is actually lost now and then.
    /// </summary>
    [Fact]
    public void AppendList_racing_appends_to_one_version_never_share_a_slot()
    {
        const int threads = 4, rounds = 5_000;
        var versions = new AppendList<int>[rounds];
        for (var r = 0; r < rounds; r++) versions[r] = AppendList<int>.Empty.Add(-1).Add(-2).Add(-3); // capacity 4: one free slot
        var results = new AppendList<int>[threads, rounds];
        using var step = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            for (var r = 0; r < rounds; r++)
            {
                step.SignalAndWait(); // every round starts together
                results[t, r] = versions[r].Add(t);
            }
        })).ToList();
        workers.ForEach(w => w.Start());
        workers.ForEach(w => w.Join());
        for (var r = 0; r < rounds; r++)
        {
            Assert.Equal([-1, -2, -3], Items(versions[r]));
            for (var t = 0; t < threads; t++) Assert.Equal([-1, -2, -3, t], Items(results[t, r]));
        }
    }

    // ---- The model properties ----

    [Fact]
    public void Concurrent_first_reads_get_the_same_list()
    {
        for (var round = 0; round < 200; round++)
        {
            var task = NewTask();
            task.History = [M("h0")];
            task.Timeline = [E(1)];
            var shared = task.CopyForUpdate();
            shared.AppendToHistory(M("h1"));
            shared.AppendToTimeline(E(2));
            var artifact = new Artifact { ArtifactId = "a", Parts = [Part.FromText("p0")] }.CopyWithParts([Part.FromText("p1")]);
            Assert.False(shared.HistoryStore.IsMaterialized);
            Assert.False(artifact.PartsStore.IsMaterialized);

            const int readers = 8;
            var histories = new List<Message>?[readers];
            var timelines = new List<TimelineEntry>?[readers];
            var parts = new List<Part>[readers];
            using var start = new Barrier(readers);
            var threads = Enumerable.Range(0, readers).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                histories[i] = shared.History;
                timelines[i] = shared.Timeline;
                parts[i] = artifact.Parts;
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());

            Assert.All(histories, h => Assert.Same(histories[0], h));
            Assert.All(timelines, h => Assert.Same(timelines[0], h));
            Assert.All(parts, h => Assert.Same(parts[0], h));
            Assert.Equal(["h0", "h1"], Ids(histories[0]));
            Assert.Equal([1L, 2L], timelines[0]!.Select(e => e.Generation));
            Assert.Equal("p0p1", artifact.GetText());
            Assert.Same(shared.History, shared.History);
        }
    }

    [Fact]
    public void Fork_from_an_old_version_leaves_it_and_the_other_branch_unchanged()
    {
        var v0 = NewTask();
        v0.AppendToHistory(M("a"));
        var v1 = v0.CopyForUpdate();
        v1.AppendToHistory(M("b"));
        var left = v1.CopyForUpdate();
        left.AppendToHistory(M("left"));
        var right = v1.CopyForUpdate(); // a fork from v1, after left appended in place
        right.AppendToHistory(M("right"));
        left.AppendToHistory(M("left2"));
        right.AppendToHistory(M("right2"));
        var leftNext = left.CopyForUpdate();
        leftNext.AppendToHistory(M("left3"));

        Assert.Equal(["a"], Ids(v0.History));
        Assert.Equal(["a", "b"], Ids(v1.History));
        Assert.Equal(["a", "b", "left", "left2"], Ids(left.History));
        Assert.Equal(["a", "b", "right", "right2"], Ids(right.History));
        Assert.Equal(["a", "b", "left", "left2", "left3"], Ids(leftNext.History));
    }

    [Fact]
    public void Set_after_read_replaces_the_content()
    {
        var task = NewTask();
        task.AppendToHistory(M("a"));
        var read = task.History!;
        Assert.Same(read, task.History);
        var replacement = new List<Message> { M("x"), M("y") };
        task.History = replacement;
        Assert.Same(replacement, task.History);
        task.AppendToHistory(M("z")); // appends to the set list, as History.Add would
        Assert.Equal(["x", "y", "z"], Ids(replacement));
        Assert.Equal(["a"], Ids(read));
        var copy = task.CopyForUpdate();
        replacement.Add(M("later"));
        Assert.Equal(["x", "y", "z"], Ids(copy.History));
        task.History = null;
        Assert.Null(task.History);
        Assert.Null(task.CopyForUpdate().History);
    }

    [Fact]
    public void Read_then_copy_then_append()
    {
        var original = NewTask();
        original.History = [M("a")];
        var copy = original.CopyForUpdate();
        var copyList = copy.History!; // the copy's own list, built on read
        copyList.Add(M("added-by-owner"));
        copy.AppendToHistory(M("appended")); // goes to the list that was read
        Assert.Same(copyList, copy.History);
        Assert.Equal(["a", "added-by-owner", "appended"], Ids(copy.History));
        Assert.Equal(["a"], Ids(original.History));

        var next = copy.CopyForUpdate(); // copies the read (and changed) list
        next.AppendToHistory(M("next"));
        copyList.Add(M("after-copy"));
        Assert.Equal(["a", "added-by-owner", "appended", "next"], Ids(next.History));
        Assert.Equal(["a", "added-by-owner", "appended", "after-copy"], Ids(copy.History));

        // Read the original after a copy shared its items: the copy is not affected by a change to that list.
        var shared = NewTask();
        shared.AppendToHistory(M("s"));
        var sharedCopy = shared.CopyForUpdate();
        shared.History!.Add(M("owner-change"));
        sharedCopy.AppendToHistory(M("copy-append"));
        Assert.Equal(["s", "owner-change"], Ids(shared.History));
        Assert.Equal(["s", "copy-append"], Ids(sharedCopy.History));
    }

    [Fact]
    public void Artifact_parts_fork_and_append()
    {
        var stored = new Artifact { ArtifactId = "a", Parts = [Part.FromText("0")] };
        var v1 = stored.CopyWithParts([Part.FromText("1")]);
        Assert.False(v1.PartsStore.IsMaterialized);
        var v2 = v1.CopyWithParts([Part.FromText("2")]);
        var fork = v1.CopyWithParts([Part.FromText("f")]);
        v2.AppendParts([Part.FromText("3"), Part.FromText("4")]);
        fork.AppendParts([Part.FromText("g")]);
        Assert.Equal("0", stored.GetText());
        Assert.Equal("01", v1.GetText());
        Assert.Equal("01234", v2.GetText());
        Assert.Equal("01fg", fork.GetText());
        var read = v2.Parts; // GetText read it already: the same list
        read.Add(Part.FromText("5"));
        v2.AppendParts([Part.FromText("6")]);
        Assert.Equal("0123456", v2.GetText());
        Assert.Equal("0123456", v2.CopyWithParts([]).GetText());
        Assert.Equal("01fg", fork.GetText());
    }

    [Fact]
    public void CountParts_and_CopyLastParts_read_without_building_the_list()
    {
        static string Text(List<Part> parts) => string.Concat(parts.OfType<TextPart>().Select(p => p.Text));

        var stored = new Artifact { ArtifactId = "a", Parts = [Part.FromText("0"), Part.FromText("1")] };
        var v1 = stored.CopyWithParts([Part.FromText("2")]);
        var v2 = v1.CopyWithParts([Part.FromText("3"), Part.FromText("4")]);
        var fork = v1.CopyWithParts([Part.FromText("f")]);

        Assert.Equal(3, v1.CountParts());
        Assert.Equal(5, v2.CountParts());
        Assert.Equal(4, fork.CountParts());
        Assert.Equal("34", Text(v2.CopyLastParts(2)));
        Assert.Equal("01234", Text(v2.CopyLastParts(5)));
        Assert.Empty(v2.CopyLastParts(0));
        Assert.Equal("2f", Text(fork.CopyLastParts(2)));
        Assert.Same(v2.CopyLastParts(1)[0], v2.CopyLastParts(1)[0]); // the parts are shared, the list is new
        Assert.NotSame(v2.CopyLastParts(1), v2.CopyLastParts(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => v2.CopyLastParts(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => v2.CopyLastParts(-1));
        Assert.False(v1.PartsStore.IsMaterialized);
        Assert.False(v2.PartsStore.IsMaterialized);
        Assert.False(fork.PartsStore.IsMaterialized);

        // A built (or set) list answers the same, and a change made to it counts.
        v2.Parts.Add(Part.FromText("5"));
        Assert.True(v2.PartsStore.IsMaterialized);
        Assert.Equal(6, v2.CountParts());
        Assert.Equal("45", Text(v2.CopyLastParts(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => v2.CopyLastParts(7));
        Assert.Equal(2, stored.CountParts());
        Assert.Equal("01", Text(stored.CopyLastParts(2)));

        // A null list counts as empty, as CopyWithParts treats it.
        var none = new Artifact { ArtifactId = "n", Parts = null! };
        Assert.Equal(0, none.CountParts());
        Assert.Empty(none.CopyLastParts(0));
    }

    [Fact]
    public void Timeline_trim_keeps_every_version_and_matches_the_list_path()
    {
        const int max = 1000;
        var shared = NewTask();
        var plain = NewTask(); // the same updates through the materialized lists
        plain.Timeline = [];
        var snapshots = new List<(AgentTask Task, List<long> Expected)>();
        var all = new List<long>();
        for (var g = 1L; g <= 2500; g++)
        {
            shared = shared.CopyForUpdate();
            shared.AppendToTimeline(E(g), max);
            all.Add(g);
            plain = plain.CopyForUpdate();
            _ = plain.Timeline; // read: this line goes through List.Add and RemoveRange
            plain.AppendToTimeline(E(g), max);
            if (g % 250 == 0 || g is 999 or 1000 or 1001)
                snapshots.Add((shared, all.Skip(Math.Max(0, all.Count - max)).ToList()));
        }

        Assert.False(shared.TimelineStore.IsMaterialized);
        foreach (var (task, expected) in snapshots)
            Assert.Equal(expected, task.Timeline!.Select(e => e.Generation));
        Assert.Equal(A2AJson.Serialize(plain), A2AJson.Serialize(shared));

        // A fork from a version before the trim keeps its entries, and does not see the newer line.
        var before = snapshots.First(s => s.Expected.Count == 999).Task;
        var fork = before.CopyForUpdate();
        fork.AppendToTimeline(E(-1), max);
        Assert.Equal([.. Enumerable.Range(1, 999).Select(i => (long)i), -1L], fork.Timeline!.Select(e => e.Generation));
        var fork2 = fork.CopyForUpdate();
        fork2.AppendToTimeline(E(-2), max);
        Assert.Equal(1000, fork2.Timeline!.Count);
        Assert.Equal(2L, fork2.Timeline[0].Generation);
        Assert.Equal(-2L, fork2.Timeline[^1].Generation);
        Assert.Equal(Enumerable.Range(1501, 1000).Select(i => (long)i), snapshots[^1].Task.Timeline!.Select(e => e.Generation));
    }

    [Fact]
    public void Engine_status_updates_do_not_build_the_lists()
    {
        var task = NewTask();
        task.Generation = 0;
        for (var i = 0; i < 3000; i++)
        {
            task = TaskExecutionEngine.ApplyToCopy(task, StreamResponse.Of(new TaskStatusUpdateEvent
            {
                TaskId = "t", ContextId = "c",
                Status = new AgentTaskStatus { State = TaskState.Working, Message = M($"m{i}") }
            }), 1000)!;
            Assert.False(task.HistoryStore.IsMaterialized);
            Assert.False(task.TimelineStore.IsMaterialized);
        }

        Assert.Equal(3000, task.History!.Count);
        Assert.Equal("m2999", task.History[^1].MessageId);
        Assert.Equal(1000, task.Timeline!.Count);
        Assert.Equal(2001L, task.Timeline[0].Generation);
    }

    // ---- JSON ----

    [Fact]
    public void Json_is_byte_identical_to_plain_lists_and_round_trips()
    {
        var plain = NewTask();
        plain.History = [M("a"), M("b")];
        plain.Timeline = [E(1), E(2)];
        plain.Artifacts = [new Artifact { ArtifactId = "x", Parts = [Part.FromText("1"), Part.FromText("2")] }];

        var shared = NewTask();
        shared.Artifacts = [new Artifact { ArtifactId = "x", Parts = [Part.FromText("1")] }];
        shared = shared.CopyForUpdate();
        shared.AppendToHistory(plain.History[0]);
        shared = shared.CopyForUpdate();
        shared.AppendToHistory(plain.History[1]);
        shared.AppendToTimeline(plain.Timeline[0]);
        shared.AppendToTimeline(plain.Timeline[1]);
        shared.Artifacts![0] = shared.Artifacts[0].CopyWithParts([plain.Artifacts[0].Parts[1]]);

        var json = A2AJson.Serialize(plain);
        Assert.Equal(json, A2AJson.Serialize(shared));
        Assert.Equal(A2AJson.SerializeToUtf8Bytes(plain), A2AJson.SerializeToUtf8Bytes(shared));
        // Reading it back sets plain lists, which serialize the same; a copy of it shares them again.
        var back = A2AJson.Deserialize<AgentTask>(json)!;
        Assert.Equal(json, A2AJson.Serialize(back));
        var copy = back.CopyForUpdate();
        Assert.Equal(json, A2AJson.Serialize(copy));
        copy.AppendToHistory(M("c"));
        Assert.Equal(json, A2AJson.Serialize(back));
        Assert.Equal(["a", "b", "c"], Ids(A2AJson.Deserialize<AgentTask>(A2AJson.Serialize(copy))!.History));
        Assert.Equal(A2AJson.Serialize(plain.DeepCopy()), A2AJson.Serialize(shared.DeepCopy()));
    }

    [Fact]
    public void Json_null_and_empty_lists_stay_as_they_were()
    {
        var none = NewTask();
        var empty = NewTask();
        empty.History = [];
        empty.Timeline = [];
        var noneJson = A2AJson.Serialize(none);
        var emptyJson = A2AJson.Serialize(empty);
        Assert.DoesNotContain("history", noneJson, StringComparison.Ordinal);
        Assert.Contains("\"history\":[]", emptyJson, StringComparison.Ordinal);
        // Copies keep null as null and empty as empty, shared or read.
        Assert.Equal(noneJson, A2AJson.Serialize(none.CopyForUpdate()));
        Assert.Equal(emptyJson, A2AJson.Serialize(empty.CopyForUpdate()));
        Assert.Equal(emptyJson, A2AJson.Serialize(empty.CopyForUpdate().CopyForUpdate().DeepCopy()));
        Assert.Null(none.CopyForUpdate().History);
        Assert.NotNull(empty.CopyForUpdate().History);
        Assert.Null(none.DeepCopy().Timeline);
        Assert.Empty(empty.DeepCopy().Timeline!);
        // Both directions.
        var noneBack = A2AJson.Deserialize<AgentTask>(noneJson)!;
        var emptyBack = A2AJson.Deserialize<AgentTask>(emptyJson)!;
        Assert.Null(noneBack.History);
        Assert.Null(noneBack.Timeline);
        Assert.Empty(emptyBack.History!);
        Assert.Empty(emptyBack.Timeline!);
        Assert.Equal(noneJson, A2AJson.Serialize(noneBack.CopyForUpdate()));
        Assert.Equal(emptyJson, A2AJson.Serialize(emptyBack.CopyForUpdate()));
        // An artifact without parts in the JSON reads as an empty list; a shared empty list writes as [].
        var artifact = A2AJson.Deserialize<Artifact>("""{"artifactId":"a"}""")!;
        Assert.Empty(artifact.Parts);
        var copied = artifact.CopyWithParts([]);
        Assert.Equal(A2AJson.Serialize(artifact), A2AJson.Serialize(copied));
        Assert.Contains("\"parts\":[]", A2AJson.Serialize(copied), StringComparison.Ordinal);
    }

    /// <summary>
    /// The shipped behaviour, as code: each copy builds new lists (the CopyForUpdate and CopyWithParts of before ADR-004).
    /// A random run of updates through both must give byte-identical JSON for every version, also the old ones, and the
    /// same for the deep copies.
    /// </summary>
    [Fact]
    public void Random_updates_serialize_as_the_list_copies_did()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            var shared = NewTask();
            var old = NewTask();
            var versions = new List<(AgentTask Shared, AgentTask Old)>();
            for (var step = 0; step < 400; step++)
            {
                // Sometimes continue from an older version (a fork), sometimes read the lists first (a reader).
                if (versions.Count > 0 && random.Next(10) == 0)
                    (shared, old) = versions[random.Next(versions.Count)];
                if (random.Next(8) == 0) _ = shared.History;
                if (random.Next(8) == 0) _ = shared.Timeline;
                if (random.Next(8) == 0) _ = shared.Artifacts?.FirstOrDefault()?.Parts;

                var nextShared = shared.CopyForUpdate();
                var nextOld = OldCopyForUpdate(old);
                switch (random.Next(4))
                {
                    case 0:
                        var m = M($"m{seed}-{step}");
                        nextShared.AppendToHistory(m);
                        (nextOld.History ??= []).Add(m);
                        break;
                    case 1:
                        var e = E(step);
                        nextShared.AppendToTimeline(e, 50);
                        var timeline = nextOld.Timeline ??= [];
                        timeline.Add(e);
                        if (timeline.Count > 50) timeline.RemoveRange(0, timeline.Count - 50);
                        break;
                    case 2:
                        var part = Part.FromText($"p{step}");
                        if (nextShared.Artifacts is not { Count: > 0 })
                        {
                            nextShared.Artifacts = [new Artifact { ArtifactId = "a", Parts = [part] }];
                            nextOld.Artifacts = [new Artifact { ArtifactId = "a", Parts = [part] }];
                        }
                        else
                        {
                            nextShared.Artifacts[0] = nextShared.Artifacts[0].CopyWithParts([part]);
                            nextOld.Artifacts![0] = OldCopyWithParts(nextOld.Artifacts[0], [part]);
                            if (random.Next(3) == 0)
                            {
                                // More parts into the copy this update owns (the batch path).
                                var more = Part.FromText($"q{step}");
                                nextShared.Artifacts[0].AppendParts([more, more]);
                                nextOld.Artifacts[0].Parts.AddRange([more, more]);
                            }
                        }

                        break;
                    default:
                        nextShared.History?.Add(M($"owner-{step}")); // the owner of a copy changes the read list
                        if (nextShared.History is not null) nextOld.History!.Add(nextShared.History[^1]);
                        break;
                }

                versions.Add((nextShared, nextOld));
                shared = nextShared;
                old = nextOld;
            }

            foreach (var (s, o) in versions)
            {
                Assert.Equal(A2AJson.SerializeToUtf8Bytes(o), A2AJson.SerializeToUtf8Bytes(s));
                Assert.Equal(A2AJson.Serialize(o.DeepCopy()), A2AJson.Serialize(s.DeepCopy()));
            }
        }
    }

    private static AgentTask OldCopyForUpdate(AgentTask task) => new()
    {
        Id = task.Id,
        ContextId = task.ContextId,
        Status = new AgentTaskStatus { State = task.Status.State, Message = task.Status.Message, Timestamp = task.Status.Timestamp },
        Artifacts = task.Artifacts is null ? null : [.. task.Artifacts],
        History = task.History is null ? null : [.. task.History],
        Metadata = task.Metadata is null ? null : new Dictionary<string, JsonElement>(task.Metadata),
        Generation = task.Generation,
        Timeline = task.Timeline is null ? null : [.. task.Timeline],
    };

    private static Artifact OldCopyWithParts(Artifact artifact, IEnumerable<Part> extra) => new()
    {
        ArtifactId = artifact.ArtifactId,
        Name = artifact.Name,
        Description = artifact.Description,
        Parts = [.. artifact.Parts, .. extra],
        Metadata = artifact.Metadata is null ? null : new Dictionary<string, JsonElement>(artifact.Metadata),
        Extensions = artifact.Extensions is null ? null : [.. artifact.Extensions],
        StartGeneration = artifact.StartGeneration,
        EndGeneration = artifact.EndGeneration,
        Role = artifact.Role,
    };
}
