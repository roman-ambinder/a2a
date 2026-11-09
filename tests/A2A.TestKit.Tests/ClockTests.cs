namespace A2A.TestKit.Tests;

public sealed class ClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Starts_at_a_fixed_time_and_moves_only_when_told()
    {
        var clock = new TestClock();
        Assert.Equal(Start, clock.GetUtcNow());
        Assert.Equal(TimeZoneInfo.Utc, clock.LocalTimeZone);
        Assert.Equal(Start, clock.GetLocalNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(Start.AddMinutes(5), clock.GetUtcNow());
        var custom = new TestClock(new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero));
        Assert.Equal(2030, custom.GetUtcNow().Year);
    }

    [Fact]
    public void Advance_rejects_a_negative_span_and_SetUtcNow_rejects_going_back()
    {
        var clock = new TestClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetUtcNow(Start));
        clock.SetUtcNow(Start.AddHours(1)); // the same time is fine
        clock.SetUtcNow(Start.AddHours(3));
        Assert.Equal(Start.AddHours(3), clock.GetUtcNow());
    }

    [Fact]
    public void Timestamps_follow_the_fake_time()
    {
        var clock = new TestClock();
        var before = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(90), clock.GetElapsedTime(before));
        Assert.Equal(TimeSpan.FromSeconds(90), clock.GetElapsedTime(before, clock.GetTimestamp()));
    }

    [Fact]
    public void Advance_fires_due_timers_in_due_order_with_the_time_set_to_each_due_time()
    {
        var clock = new TestClock();
        var log = new List<string>();
        using var late = clock.CreateTimer(_ => log.Add($"late@{(clock.GetUtcNow() - Start).TotalSeconds}"), null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
        using var early = clock.CreateTimer(_ => log.Add($"early@{(clock.GetUtcNow() - Start).TotalSeconds}"), null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        using var never = clock.CreateTimer(_ => log.Add("never"), null, TimeSpan.FromSeconds(500), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(["early@10", "late@30"], log);
        Assert.Equal(Start.AddSeconds(60), clock.GetUtcNow());
        clock.Advance(TimeSpan.FromSeconds(440));
        Assert.Equal(["early@10", "late@30", "never"], log);
    }

    [Fact]
    public void Timers_with_the_same_due_time_fire_in_the_order_they_were_armed()
    {
        var clock = new TestClock();
        var log = new List<int>();
        var timers = Enumerable.Range(0, 4).Select(i => clock.CreateTimer(_ => log.Add(i), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan)).ToList();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal([0, 1, 2, 3], log);
        timers.ForEach(t => t.Dispose());
    }

    [Fact]
    public void Callbacks_run_on_the_calling_thread()
    {
        var clock = new TestClock();
        int? thread = null;
        using var timer = clock.CreateTimer(_ => thread = Environment.CurrentManagedThreadId, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Environment.CurrentManagedThreadId, thread);
    }

    [Fact]
    public void A_periodic_timer_fires_again_for_every_period()
    {
        var clock = new TestClock();
        var times = new List<double>();
        using var timer = clock.CreateTimer(_ => times.Add((clock.GetUtcNow() - Start).TotalSeconds), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty(times);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal([5.0, 15.0, 25.0], times);
        clock.Advance(TimeSpan.FromSeconds(1)); // now 35
        Assert.Equal([5.0, 15.0, 25.0, 35.0], times);
    }

    [Fact]
    public void Change_re_arms_disables_and_reports_disposal()
    {
        var clock = new TestClock();
        var fired = 0;
        var timer = clock.CreateTimer(_ => fired++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, fired); // an infinite due time means never

        Assert.True(timer.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, fired);
        Assert.True(timer.Change(TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan)); // pushed back: due at +5 from now
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(0, fired);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, fired); // one-shot

        Assert.True(timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(4, fired);
        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan)); // stop the periodic timer
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(4, fired);

        timer.Dispose();
        Assert.False(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(4, fired);
    }

    [Fact]
    public async Task A_disposed_timer_does_not_fire_and_DisposeAsync_works()
    {
        var clock = new TestClock();
        var fired = 0;
        var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        await timer.DisposeAsync();
        timer.Dispose(); // twice is fine
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, fired);
    }

    [Fact]
    public async Task A_zero_due_time_fires_at_once_without_advancing()
    {
        var clock = new TestClock();
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = clock.CreateTimer(_ => fired.TrySetResult(), null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(Start, clock.GetUtcNow()); // the time did not move

        var again = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var other = clock.CreateTimer(_ => again.TrySetResult(), null, TimeSpan.FromSeconds(100), Timeout.InfiniteTimeSpan);
        other.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        await again.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Invalid_intervals_are_refused()
    {
        var clock = new TestClock();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.CreateTimer(_ => { }, null, TimeSpan.FromSeconds(-2), Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.CreateTimer(_ => { }, null, TimeSpan.Zero, TimeSpan.FromSeconds(-2)));
        Assert.Throws<ArgumentNullException>(() => clock.CreateTimer(null!, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan));
        using var timer = clock.CreateTimer(_ => { }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Assert.Throws<ArgumentOutOfRangeException>(() => timer.Change(TimeSpan.FromSeconds(-5), Timeout.InfiniteTimeSpan));
    }

    [Fact]
    public async Task Task_Delay_ends_when_the_clock_reaches_its_time()
    {
        var clock = new TestClock();
        var delay = Task.Delay(TimeSpan.FromMinutes(10), clock);
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));
        await delay.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Task_Delay_can_be_canceled()
    {
        var clock = new TestClock();
        using var cts = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromMinutes(10), clock, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => delay);
    }

    [Fact]
    public void A_CancellationTokenSource_with_a_delay_cancels_on_the_clock()
    {
        var clock = new TestClock();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.False(cts.IsCancellationRequested);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(cts.IsCancellationRequested);
    }

    [Fact]
    public void A_callback_may_create_and_change_timers_and_read_the_time()
    {
        var clock = new TestClock();
        var log = new List<string>();
        ITimer? second = null;
        using var first = clock.CreateTimer(_ =>
        {
            log.Add($"first@{(clock.GetUtcNow() - Start).TotalSeconds}");
            second = clock.CreateTimer(_ => log.Add($"second@{(clock.GetUtcNow() - Start).TotalSeconds}"), null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
        }, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(20)); // the new timer is due inside this advance, at 10 + 5

        Assert.Equal(["first@10", "second@15"], log);
        second!.Dispose();
    }

    [Fact]
    public async Task A_callback_can_create_a_timer_from_another_thread_while_the_clock_advances()
    {
        var clock = new TestClock();
        var created = new TaskCompletionSource<ITimer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var first = clock.CreateTimer(_ =>
        {
            // The callback waits for another thread that creates a timer and reads the time: the clock must not hold its lock here.
            Task.Run(() =>
            {
                var t = clock.CreateTimer(_ => fired.TrySetResult(), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
                _ = clock.GetUtcNow();
                created.SetResult(t);
            }).Wait(TimeSpan.FromSeconds(30));
        }, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(1));
        var second = await created.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(fired.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await fired.Task.WaitAsync(TimeSpan.FromSeconds(30));
        second.Dispose();
    }

    [Fact]
    public void Advance_keeps_firing_after_a_callback_throws_and_then_reports_it()
    {
        var clock = new TestClock();
        var fired = 0;
        using var bad = clock.CreateTimer(_ => throw new InvalidOperationException("bad callback"), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        using var good = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        var ex = Assert.Throws<AggregateException>(() => clock.Advance(TimeSpan.FromSeconds(5)));
        Assert.IsType<InvalidOperationException>(Assert.Single(ex.InnerExceptions));
        Assert.Equal(1, fired);
        Assert.Equal(Start.AddSeconds(5), clock.GetUtcNow());
    }

    [Fact]
    public async Task Concurrent_advances_add_up()
    {
        var clock = new TestClock();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++) clock.Advance(TimeSpan.FromSeconds(1));
        })));
        Assert.Equal(Start.AddSeconds(800), clock.GetUtcNow());
    }
}
