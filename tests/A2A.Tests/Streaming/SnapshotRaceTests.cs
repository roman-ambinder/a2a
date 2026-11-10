using A2A.Server;
using A2A.Server.Storage;
using A2A.Tests.Execution;

namespace A2A.Tests.Streaming;

/// <summary>
/// The task lock is local to one instance: another instance can commit between the read of the task and the read of the last
/// sequence. A snapshot must never be stamped with a sequence whose event it does not hold, or the live event is dropped as sent.
/// </summary>
public sealed class SnapshotRaceTests
{
    private static readonly TimeSpan Wait = StreamRig.Wait;
    private const string Marker = "from another instance";

    private static async Task Agent(AgentRequestContext context, TaskUpdater u, TaskCompletionSource gate, CancellationToken ct)
    {
        if (context.IsNewTask)
        {
            await u.StartWorkAsync(cancellationToken: ct);
            await gate.Task.WaitAsync(ct);
        }
    }

    private static bool Holds(StreamResponse evt, string text) =>
        evt is TaskEvent t && t.Task.Status.Message?.GetText() == text;

    [Fact]
    public async Task Subscribe_does_not_lose_an_event_committed_between_the_task_read_and_the_sequence_read()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = StreamRig.Create((c, u, ct) => Agent(c, u, gate, ct));
        try
        {
            var key = await rig.StartTaskAsync();
            await rig.WaitForSequenceAsync(key, 2); // task snapshot (1), WORKING (2)

            // After the stream subscribed, the next task read is followed by a commit of another instance (event 3).
            ((SignalBus)rig.Bus).OnSubscribe = () => rig.Tasks.AfterNextGet(async () =>
                Assert.True(await rig.B.CommitSystemStatusAsync(key, TaskState.Working, Marker)));
            await using var stream = rig.Subscribe(key).GetAsyncEnumerator();

            Assert.True(await stream.MoveNextAsync());
            var snapshot = stream.Current;
            Assert.Equal(3, await rig.Log.LastSequenceAsync(key)); // the other instance committed
            if (!Holds(snapshot.Event, Marker))
            {
                Assert.True(snapshot.Sequence < 3, $"The snapshot lacks event 3 but is stamped {snapshot.Sequence}: the live event 3 is dropped as already sent.");
                Assert.True(await stream.MoveNextAsync().AsTask().WaitAsync(Wait));
                Assert.Equal(3, stream.Current.Sequence);
            }
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Follow_up_initial_snapshot_holds_the_event_it_is_stamped_with()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = StreamRig.Create(async (c, u, ct) =>
        {
            if (c.IsNewTask)
            {
                await u.StartWorkAsync(cancellationToken: ct);
                await u.RequireInputAsync("more?", ct);
                return;
            }
            await gate.Task.WaitAsync(ct); // the follow-up waits
        });
        try
        {
            var first = StreamRig.NewTaskContext();
            var key = StreamRig.KeyOf(first);
            var run = await rig.A.StartForTestAsync(first);
            run.Detach();
            await run.Completion.WaitAsync(Wait);
            var current = (await rig.Inner.GetAsync(key))!.Task.DeepCopy();
            Assert.Equal(TaskState.InputRequired, current.Status.State);
            var lastBefore = await rig.Log.LastSequenceAsync(key);

            // The follow-up's snapshot read is followed by a commit of another instance.
            var commits = 0;
            rig.Tasks.AfterNextGet(async () =>
            {
                Assert.True(await rig.B.CommitSystemStatusAsync(key, TaskState.Working, Marker));
                commits++;
            });
            var followUp = new AgentRequestContext
            {
                Message = Message.FromUser("more"), TaskId = first.TaskId, ContextId = first.ContextId, Call = first.Call, Task = current,
            };
            var exec = await rig.A.StartForTestAsync(followUp);
            var snapshot = await exec.Outgoing.Reader.ReadAsync().AsTask().WaitAsync(Wait);

            Assert.Equal(1, commits);
            Assert.Equal(lastBefore + 1, await rig.Log.LastSequenceAsync(key));
            if (!Holds(snapshot.Event, Marker))
                Assert.True(snapshot.Sequence <= lastBefore, $"The snapshot lacks event {lastBefore + 1} but is stamped {snapshot.Sequence}.");
            exec.Detach();
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Stamp_is_the_sequence_read_before_the_last_task_read_when_the_sequence_changes_every_time()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = StreamRig.Create((c, u, ct) => Agent(c, u, gate, ct));
        try
        {
            var key = await rig.StartTaskAsync();
            await rig.WaitForSequenceAsync(key, 2);
            var start = await rig.Log.LastSequenceAsync(key);

            // Every task read is followed by a commit of another instance, so the sequence changes between the reads, every try.
            var n = 0;
            rig.Tasks.AfterEveryGet(async () => Assert.True(await rig.B.CommitSystemStatusAsync(key, TaskState.Working, $"n{n++}")));
            var getsBefore = rig.Tasks.Gets;
            var (record, stamp) = await rig.A.ReadTaskAtSequenceAsync(key, CancellationToken.None);

            Assert.Equal(5, rig.Tasks.Gets - getsBefore); // gives up after 5 tries
            Assert.Equal(start + 4, stamp); // read before the 5th task read, which holds the commits n0..n3
            Assert.Equal("n3", record!.Task.Status.Message!.GetText());
            Assert.Equal(start + 5, await rig.Log.LastSequenceAsync(key));
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task Stable_task_is_read_once()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = StreamRig.Create((c, u, ct) => Agent(c, u, gate, ct));
        try
        {
            var key = await rig.StartTaskAsync();
            await rig.WaitForSequenceAsync(key, 2);
            var getsBefore = rig.Tasks.Gets;
            var (record, stamp) = await rig.A.ReadTaskAtSequenceAsync(key, CancellationToken.None);

            Assert.Equal(1, rig.Tasks.Gets - getsBefore);
            Assert.Equal(2, stamp);
            Assert.Equal(TaskState.Working, record!.Task.Status.State);
            Assert.Null((await rig.A.ReadTaskAtSequenceAsync(new TaskKey("", "alice", "missing"), CancellationToken.None)).Record);
        }
        finally
        {
            gate.TrySetResult();
        }
    }
}
