using A2A.Server;
using A2A.Server.Execution;

namespace A2A.Tests;

/// <summary>
/// <see cref="ExecutionAdmission"/> on its own: the instance and tenant caps, the FIFO queue, timeouts on a fake clock,
/// cancellation, shutdown and a stress run. Tests wait on signals and the fake clock, never on a delay.
/// </summary>
public sealed class ExecutionAdmissionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static A2ALimits Limits(int max = 2, int perTenant = 0, int queue = 10, double waitSeconds = 10) => new()
    {
        MaxConcurrentExecutions = max,
        MaxConcurrentExecutionsPerTenant = perTenant,
        MaxQueuedExecutions = queue,
        MaxExecutionQueueWait = TimeSpan.FromSeconds(waitSeconds),
    };

    private static string Reason(Exception ex) => (string)ex.Data[ExecutionAdmission.ReasonDataKey]!;

    private static void AssertEmpty(ExecutionAdmission admission)
    {
        Assert.Equal(0, admission.Running);
        Assert.Equal(0, admission.Queued);
        Assert.Equal(0, admission.TrackedTenants);
    }

    private static async Task<A2AException> RefusedAsync(ExecutionAdmission admission, string tenant = "t")
    {
        var ex = await Assert.ThrowsAsync<A2AException>(async () => (await admission.EnterAsync(tenant, null, default)).Dispose());
        Assert.Equal(A2AErrorCode.RateLimited, ex.Code);
        Assert.Equal(429, ex.HttpStatus);
        Assert.True(ex.Retryable);
        Assert.Equal(ExecutionAdmission.CapacityReason, ex.Metadata!["reason"]);
        return ex;
    }

    [Fact]
    public async Task Instance_cap_queues_the_next_start_until_a_slot_frees()
    {
        var admission = new ExecutionAdmission(Limits(max: 2), new FakeTimeProvider());
        var first = await admission.EnterAsync("t", null, default);
        var second = await admission.EnterAsync("t", null, default);
        var third = admission.EnterAsync("t", null, default).AsTask();

        Assert.False(third.IsCompleted);
        Assert.Equal(2, admission.Running);
        Assert.Equal(1, admission.Queued);

        first.Dispose();
        var granted = await third.WaitAsync(Wait);
        Assert.Equal(2, admission.Running);
        Assert.Equal(0, admission.Queued);

        second.Dispose();
        granted.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Waiters_are_granted_in_arrival_order()
    {
        var admission = new ExecutionAdmission(Limits(max: 1), new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);
        var waiters = Enumerable.Range(0, 5).Select(_ => admission.EnterAsync("t", null, default).AsTask()).ToArray();
        Assert.Equal(5, admission.Queued);

        var current = holder;
        for (var i = 0; i < waiters.Length; i++)
        {
            current.Dispose();
            current = await waiters[i].WaitAsync(Wait);
            // Only the waiter at the head got the slot: the ones behind it still wait.
            for (var j = i + 1; j < waiters.Length; j++) Assert.False(waiters[j].IsCompleted);
        }
        current.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Tenant_cap_queues_the_tenant_and_leaves_other_tenants_alone()
    {
        var admission = new ExecutionAdmission(Limits(max: 10, perTenant: 1), new FakeTimeProvider());
        var a1 = await admission.EnterAsync("a", null, default);
        var a2 = admission.EnterAsync("a", null, default);
        var b1 = admission.EnterAsync("b", null, default);

        Assert.False(a2.IsCompleted);
        Assert.True(b1.IsCompletedSuccessfully); // another tenant takes a free slot at once

        a1.Dispose();
        var a2Slot = await a2.AsTask().WaitAsync(Wait);
        a2Slot.Dispose();
        (await b1).Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Waiter_blocked_by_its_tenant_does_not_block_another_tenant()
    {
        var admission = new ExecutionAdmission(Limits(max: 2, perTenant: 1), new FakeTimeProvider());
        var a1 = await admission.EnterAsync("a", null, default);
        var b1 = await admission.EnterAsync("b", null, default);
        var a2 = admission.EnterAsync("a", null, default).AsTask(); // first in the queue, blocked by tenant a
        var b2 = admission.EnterAsync("b", null, default).AsTask();
        Assert.Equal(2, admission.Queued);

        b1.Dispose(); // the instance has room again; a2 may not use it, b2 may
        var b2Slot = await b2.WaitAsync(Wait);
        Assert.False(a2.IsCompleted);
        Assert.Equal(1, admission.Queued);

        a1.Dispose();
        var a2Slot = await a2.WaitAsync(Wait);
        a2Slot.Dispose();
        b2Slot.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Full_queue_refuses_with_queue_full()
    {
        var admission = new ExecutionAdmission(Limits(max: 1, queue: 1, waitSeconds: 10), new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);
        var queued = admission.EnterAsync("t", null, default).AsTask();

        var refused = await RefusedAsync(admission);
        Assert.Equal(ExecutionAdmission.QueueFull, Reason(refused));
        Assert.Equal(TimeSpan.FromSeconds(10), refused.RetryAfter);
        Assert.Equal(1, admission.Queued);

        holder.Dispose();
        (await queued.WaitAsync(Wait)).Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task One_tenant_may_queue_at_most_its_own_limit()
    {
        var admission = new ExecutionAdmission(Limits(max: 1, perTenant: 1, queue: 10), new FakeTimeProvider());
        var holder = await admission.EnterAsync("a", null, default);
        var a2 = admission.EnterAsync("a", null, default).AsTask();

        var refused = await RefusedAsync(admission, "a"); // tenant a already has one queued start
        Assert.Equal(ExecutionAdmission.QueueFull, Reason(refused));
        var b1 = admission.EnterAsync("b", null, default).AsTask(); // another tenant still queues
        Assert.Equal(2, admission.Queued);

        holder.Dispose();
        var a2Slot = await a2.WaitAsync(Wait);
        a2Slot.Dispose();
        (await b1.WaitAsync(Wait)).Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Wait_ends_on_the_fake_clock_with_queue_timeout()
    {
        var time = new FakeTimeProvider();
        var admission = new ExecutionAdmission(Limits(max: 1, waitSeconds: 10), time);
        var holder = await admission.EnterAsync("t", null, default);
        var waiter = admission.EnterAsync("t", null, default).AsTask();

        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(waiter.IsCompleted);
        Assert.Equal(1, admission.Queued);

        time.Advance(TimeSpan.FromSeconds(1));
        var ex = await Assert.ThrowsAsync<A2AException>(() => waiter.WaitAsync(Wait));
        Assert.Equal(ExecutionAdmission.QueueTimeout, Reason(ex));
        Assert.Equal(A2AErrorCode.RateLimited, ex.Code);
        Assert.Equal(0, admission.Queued);
        Assert.Equal(0, time.PendingTimers);

        holder.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Cancel_while_queued_leaves_no_count_behind()
    {
        var admission = new ExecutionAdmission(Limits(max: 1, perTenant: 1), new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);
        using var cts = new CancellationTokenSource();
        var waiter = admission.EnterAsync("t", null, cts.Token).AsTask();
        Assert.Equal(1, admission.Queued);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(Wait));
        Assert.Equal(0, admission.Queued);

        holder.Dispose();
        AssertEmpty(admission);
        // The tenant's queue count is gone too: the tenant may queue again.
        var next = await admission.EnterAsync("t", null, default);
        next.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Already_cancelled_token_throws_before_anything_is_counted()
    {
        var admission = new ExecutionAdmission(Limits(), new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await admission.EnterAsync("t", null, cts.Token));
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Grant_and_cancel_racing_never_lose_a_slot()
    {
        var admission = new ExecutionAdmission(Limits(max: 1, perTenant: 1), new FakeTimeProvider());
        for (var i = 0; i < 300; i++)
        {
            var holder = await admission.EnterAsync("t", null, default);
            using var cts = new CancellationTokenSource();
            var waiter = admission.EnterAsync("t", null, cts.Token).AsTask();
            var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = Task.Run(async () => { await go.Task; holder.Dispose(); });
            var cancel = Task.Run(async () => { await go.Task; cts.Cancel(); });
            go.SetResult();
            await Task.WhenAll(release, cancel).WaitAsync(Wait);

            try
            {
                // The grant won: the caller owns the slot and gives it back.
                (await waiter.WaitAsync(Wait)).Dispose();
            }
            catch (OperationCanceledException)
            {
                // The cancel won: nothing is held.
            }
            AssertEmpty(admission);
        }
    }

    [Fact]
    public async Task Close_refuses_every_waiter_and_turns_the_queue_off()
    {
        var admission = new ExecutionAdmission(Limits(max: 1), new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);
        var w1 = admission.EnterAsync("t", null, default).AsTask();
        var w2 = admission.EnterAsync("u", null, default).AsTask();

        admission.Close();

        foreach (var w in new[] { w1, w2 })
        {
            var ex = await Assert.ThrowsAsync<A2AException>(() => w.WaitAsync(Wait));
            Assert.Equal(ExecutionAdmission.Shutdown, Reason(ex));
        }
        Assert.Equal(0, admission.Queued);
        Assert.Equal(ExecutionAdmission.Shutdown, Reason(await RefusedAsync(admission))); // no queue any more

        holder.Dispose();
        AssertEmpty(admission);
        // With a free slot a start still proceeds, as it does today during shutdown.
        (await admission.EnterAsync("t", null, default)).Dispose();
        admission.Close(); // twice is fine
        AssertEmpty(admission);
    }

    [Fact]
    public async Task Limit_off_means_no_gating()
    {
        var admission = new ExecutionAdmission(Limits(max: 0, perTenant: 0, queue: 0), new FakeTimeProvider());
        var slots = new List<ExecutionSlot>();
        for (var i = 0; i < 200; i++)
        {
            var entered = admission.EnterAsync(i % 3 == 0 ? "a" : "b", null, default);
            Assert.True(entered.IsCompletedSuccessfully);
            slots.Add(await entered);
        }
        Assert.Equal(200, admission.Running);
        foreach (var slot in slots) slot.Dispose();
        AssertEmpty(admission);

        // A negative limit is off too.
        var negative = new ExecutionAdmission(Limits(max: -1, perTenant: -5), new FakeTimeProvider());
        (await negative.EnterAsync("t", null, default)).Dispose();
        AssertEmpty(negative);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    public async Task Without_a_queue_a_start_over_the_limit_is_refused_at_once(int queue, double waitSeconds)
    {
        var admission = new ExecutionAdmission(Limits(max: 1, queue: queue, waitSeconds: waitSeconds), new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);

        var refused = await RefusedAsync(admission);

        Assert.Equal(ExecutionAdmission.QueueFull, Reason(refused));
        Assert.Equal(TimeSpan.FromSeconds(1), refused.RetryAfter);
        Assert.Equal(0, admission.Queued);
        holder.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task An_unbounded_wait_is_no_queue()
    {
        var limits = Limits(max: 1);
        limits.MaxExecutionQueueWait = Timeout.InfiniteTimeSpan;
        var admission = new ExecutionAdmission(limits, new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);
        await RefusedAsync(admission);
        holder.Dispose();
        AssertEmpty(admission);
    }

    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(5, 5)]
    [InlineData(300, 30)]
    public async Task Retry_after_is_the_queue_wait_clamped_to_one_to_thirty_seconds(double waitSeconds, double expectedSeconds)
    {
        var admission = new ExecutionAdmission(Limits(max: 1, queue: 1, waitSeconds: waitSeconds), new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);
        var queued = admission.EnterAsync("t", null, default).AsTask();

        var refused = await RefusedAsync(admission);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), refused.RetryAfter);
        holder.Dispose();
        (await queued.WaitAsync(Wait)).Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task The_queued_callback_runs_only_for_a_start_that_waits_and_is_disposed_when_the_wait_ends()
    {
        var time = new FakeTimeProvider();
        var admission = new ExecutionAdmission(Limits(max: 1, waitSeconds: 10), time);
        var calls = 0;
        var disposed = 0;
        Func<IDisposable?> callback = () =>
        {
            calls++;
            return new Probe(() => Interlocked.Increment(ref disposed));
        };

        var holder = await admission.EnterAsync("t", callback, default); // a free slot: no callback
        Assert.Equal(0, calls);

        var granted = admission.EnterAsync("t", callback, default).AsTask();
        Assert.Equal(1, calls);
        Assert.Equal(0, disposed);
        holder.Dispose();
        var slot = await granted.WaitAsync(Wait);
        Assert.Equal(1, Volatile.Read(ref disposed));

        var timedOut = admission.EnterAsync("t", callback, default).AsTask();
        time.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<A2AException>(() => timedOut.WaitAsync(Wait));
        Assert.Equal(2, Volatile.Read(ref disposed));

        using var cts = new CancellationTokenSource();
        var canceled = admission.EnterAsync("t", callback, cts.Token).AsTask();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(Wait));
        Assert.Equal(3, Volatile.Read(ref disposed));
        Assert.Equal(3, calls);

        slot.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task A_slot_is_given_back_once()
    {
        var admission = new ExecutionAdmission(Limits(max: 1), new FakeTimeProvider());
        var slot = await admission.EnterAsync("t", null, default);
        slot.Dispose();
        slot.Dispose();
        var next = await admission.EnterAsync("t", null, default);
        Assert.Equal(1, admission.Running);
        next.Dispose();
        AssertEmpty(admission);
    }

    [Fact]
    public async Task WhenAsync_completes_after_the_change_that_makes_the_condition_hold()
    {
        var admission = new ExecutionAdmission(Limits(max: 1), new FakeTimeProvider());
        var holder = await admission.EnterAsync("t", null, default);
        var queuedSignal = admission.WhenAsync((_, queued) => queued >= 1);
        Assert.False(queuedSignal.IsCompleted);
        var waiter = admission.EnterAsync("t", null, default).AsTask();
        await queuedSignal.WaitAsync(Wait);

        var idle = admission.WhenAsync((running, queued) => running == 0 && queued == 0);
        holder.Dispose();
        (await waiter.WaitAsync(Wait)).Dispose();
        await idle.WaitAsync(Wait);
    }

    [Fact]
#pragma warning disable CA5394 // a stress test's randomness is not security
    public async Task Stress_running_never_exceeds_the_caps_and_every_count_ends_at_zero()
    {
        const int Cap = 4;
        const int TenantCap = 2;
        var admission = new ExecutionAdmission(Limits(max: Cap, perTenant: TenantCap, queue: 10_000, waitSeconds: 120), TimeProvider.System);
        var tenants = new[] { "a", "b", "c", "d", "e" };
        var running = 0;
        var perTenant = new int[tenants.Length];
        var maxRunning = 0;
        var maxPerTenant = 0;
        var violations = 0;
        var entered = 0;
        var canceled = 0;
        var refused = 0;

        async Task Worker(int seed)
        {
            var rnd = new Random(seed);
            for (var i = 0; i < 400; i++)
            {
                var t = rnd.Next(tenants.Length);
                using var cts = new CancellationTokenSource();
                var mode = rnd.Next(4);
                if (mode == 0) cts.Cancel(); // before
                else if (mode == 1) _ = Task.Run(cts.Cancel); // racing
                ExecutionSlot slot;
                try
                {
                    slot = await admission.EnterAsync(tenants[t], null, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref canceled);
                    continue;
                }
                catch (A2AException)
                {
                    Interlocked.Increment(ref refused); // the tenant's queue is full: allowed, and counted
                    continue;
                }
                var now = Interlocked.Increment(ref running);
                var tenantNow = Interlocked.Increment(ref perTenant[t]);
                if (now > Cap || tenantNow > TenantCap) Interlocked.Increment(ref violations);
                InterlockedMax(ref maxRunning, now);
                InterlockedMax(ref maxPerTenant, tenantNow);
                Interlocked.Increment(ref entered);
                if (rnd.Next(3) == 0) await Task.Yield();
                Interlocked.Decrement(ref perTenant[t]);
                Interlocked.Decrement(ref running);
                slot.Dispose();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 24).Select(n => Task.Run(() => Worker(n)))).WaitAsync(TimeSpan.FromMinutes(2));

        Assert.Equal(0, violations);
        Assert.True(maxRunning <= Cap);
        Assert.True(maxPerTenant <= TenantCap);
        Assert.True(entered > 0);
        Assert.Equal(24 * 400, entered + canceled + refused);
        AssertEmpty(admission);
    }
#pragma warning restore CA5394

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)))
            if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
    }

    private sealed class Probe(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
