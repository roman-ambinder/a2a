using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using A2A.Diagnostics;
using A2A.Server;
using A2A.Server.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Telemetry;

/// <summary>
///     The opt-in anomaly detector (spec 13.4): rapid task creation and excessive cancellations by one caller are reported
///     once per caller and one-minute window, as an <c>anomaly</c> security event (log 1202) and on
///     <c>a2a.server.anomalies</c>. Meter listeners are process-wide, so these tests run alone.
/// </summary>
[Collection(TelemetryCollection.Name)]
public sealed class AnomalyDetectionTests
{
    private const int AnomalyEventId = 1202;

    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, int Id, string Message)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(CaptureProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner.Entries.Enqueue((logLevel, eventId.Id, formatter(state, exception)));
        }

        public List<string> Anomalies() => Entries.Where(e => e.Id == AnomalyEventId).Select(e => e.Message).ToList();
    }

    /// <summary>Anomaly measurements: <c>a2a.server.anomalies</c> by kind, and <c>anomaly</c> security events by operation.</summary>
    private sealed class Counters : IDisposable
    {
        private readonly MeterListener _listener = new();
        public ConcurrentQueue<string> Kinds { get; } = new();
        public ConcurrentQueue<string> SecurityOperations { get; } = new();

        public Counters()
        {
            _listener.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == A2ATelemetry.ServerMeterName &&
                    i.Name is "a2a.server.anomalies" or A2ATelemetry.Metrics.SecurityEvents)
                    l.EnableMeasurementEvents(i);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            {
                string? kind = null, securityEvent = null, operation = null;
                foreach (var t in tags)
                {
                    if (t.Key == "a2a.anomaly.kind") kind = t.Value as string;
                    if (t.Key == A2ATelemetry.Tags.SecurityEvent) securityEvent = t.Value as string;
                    if (t.Key == A2ATelemetry.Tags.Operation) operation = t.Value as string;
                }

                if (instrument.Name == "a2a.server.anomalies") Kinds.Enqueue(kind ?? "?");
                else if (securityEvent == A2ATelemetry.SecurityEventNames.Anomaly) SecurityOperations.Enqueue(operation ?? "?");
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    private static ServerCallContext Call(string user = "alice", string tenant = "") => new()
    {
        Principal = user,
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", user)], "Test")),
        ProtocolVersion = A2AProtocol.Version,
        Tenant = tenant
    };

    private static SendMessageRequest Req(string text, string? taskId = null) =>
        new() { Message = Message.FromUser(text, taskId: taskId) };

    private static Task<TestAgentHost> Start(CaptureProvider log, FakeTimeProvider clock,
        Action<A2AAnomalyOptions>? configure) =>
        TestAgentHost.StartAsync(o =>
        {
            if (configure is null) return;
            o.Anomalies.Enabled = true;
            configure(o.Anomalies);
        }, services: s =>
        {
            s.AddSingleton<TimeProvider>(clock);
            s.AddLogging(b => b.AddProvider(log));
        });

    private static async Task CreateTasks(TestAgentHost host, int count, string user = "alice", string tenant = "")
    {
        for (var i = 0; i < count; i++)
            Assert.IsType<TaskResponse>(await host.Handler.SendMessageAsync(Req($"task {i}"), Call(user, tenant)));
    }

    private static async Task CancelMissing(TestAgentHost host, int count, string user = "alice")
    {
        for (var i = 0; i < count; i++)
        {
            var ex = await Assert.ThrowsAsync<A2AException>(() =>
                host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = $"missing-{i}" }, Call(user)));
            Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        }
    }

    [Fact]
    public async Task Off_by_default_nothing_is_reported()
    {
        var log = new CaptureProvider();
        using var counters = new Counters();
        await using var host = await Start(log, new FakeTimeProvider(), null);

        Assert.False(host.App.Services.GetRequiredService<A2AServerOptions>().Anomalies.Enabled);
        await CreateTasks(host, 65);
        await CancelMissing(host, 25);

        Assert.Empty(log.Anomalies());
        Assert.Empty(counters.Kinds);
    }

    [Fact]
    public async Task Creations_over_the_threshold_are_reported_once_per_window()
    {
        var log = new CaptureProvider();
        using var counters = new Counters();
        var clock = new FakeTimeProvider();
        await using var host = await Start(log, clock, a => a.TaskCreationsPerMinute = 3);

        await CreateTasks(host, 3);
        Assert.Empty(log.Anomalies()); // at the threshold, not over it

        await CreateTasks(host, 5);
        var line = Assert.Single(log.Anomalies());
        Assert.Contains("task_creation", line, StringComparison.Ordinal);
        Assert.Contains("SendMessage", line, StringComparison.Ordinal);
        Assert.Contains("4 within one minute", line, StringComparison.Ordinal);
        Assert.Contains("threshold of 3", line, StringComparison.Ordinal);
        Assert.Contains(SecurityEventLog.HashPrincipal("alice"), line, StringComparison.Ordinal);
        Assert.DoesNotContain("alice", line, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, log.Entries.Single(e => e.Id == AnomalyEventId).Level);
        Assert.Equal([AnomalyDetector.TaskCreation], counters.Kinds);
        Assert.Equal(["SendMessage"], counters.SecurityOperations);

        // A new window counts from zero, and reports again once over the threshold.
        clock.Advance(TimeSpan.FromMinutes(1));
        await CreateTasks(host, 3);
        Assert.Single(log.Anomalies());
        await CreateTasks(host, 1);
        Assert.Equal(2, log.Anomalies().Count);
        Assert.Equal(2, counters.Kinds.Count);
    }

    [Fact]
    public async Task Streaming_creations_count_too()
    {
        var log = new CaptureProvider();
        await using var host = await Start(log, new FakeTimeProvider(), a => a.TaskCreationsPerMinute = 1);

        for (var i = 0; i < 2; i++)
            await foreach (var _ in host.Handler.SendStreamingMessageAsync(Req($"task s{i}"), Call()))
            {
            }

        var line = Assert.Single(log.Anomalies());
        Assert.Contains("SendStreamingMessage", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_to_an_existing_task_is_not_a_creation()
    {
        var log = new CaptureProvider();
        await using var host = await Start(log, new FakeTimeProvider(), a => a.TaskCreationsPerMinute = 1);

        var task = Assert.IsType<TaskResponse>(await host.Handler.SendMessageAsync(Req("input"), Call())).Task;
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        for (var i = 0; i < 3; i++)
        {
            var next = Assert.IsType<TaskResponse>(
                await host.Handler.SendMessageAsync(Req("ask-again", task.Id), Call())).Task;
            Assert.Equal(task.Id, next.Id);
        }

        Assert.Empty(log.Anomalies());
    }

    [Fact]
    public async Task Cancellations_over_the_threshold_are_reported_once_per_window()
    {
        var log = new CaptureProvider();
        using var counters = new Counters();
        var clock = new FakeTimeProvider();
        await using var host = await Start(log, clock, a => a.CancellationsPerMinute = 2);

        // A cancel that works counts like one that fails.
        var task = Assert.IsType<TaskResponse>(await host.Handler.SendMessageAsync(Req("input"), Call())).Task;
        var canceled = await host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = task.Id }, Call());
        Assert.Equal(TaskState.Canceled, canceled.Status.State);
        await CancelMissing(host, 1);
        Assert.Empty(log.Anomalies());

        await CancelMissing(host, 4);
        var line = Assert.Single(log.Anomalies());
        Assert.Contains("cancellation", line, StringComparison.Ordinal);
        Assert.Contains("CancelTask", line, StringComparison.Ordinal);
        Assert.Equal([AnomalyDetector.Cancellation], counters.Kinds);
        Assert.Equal(["CancelTask"], counters.SecurityOperations);

        clock.Advance(TimeSpan.FromMinutes(1));
        await CancelMissing(host, 3);
        Assert.Equal(2, log.Anomalies().Count);
    }

    [Fact]
    public async Task Cancels_in_tenants_no_agent_serves_do_not_fill_the_callers_table()
    {
        // Only "acme" and "globex" have agents. Mallory names a fresh made-up tenant in each CancelTask: each call fails,
        // and must not take a tracked entry, or the table fills and no other caller is watched.
        var log = new CaptureProvider();
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.Anomalies.Enabled = true;
            o.Anomalies.CancellationsPerMinute = 2;
            o.Anomalies.MaxTrackedCallers = 2;
        }, services: s =>
        {
            s.AddA2AServer().AddTenantCardProvider<TenantCards>();
            s.AddSingleton<TimeProvider>(new FakeTimeProvider());
            s.AddLogging(b => b.AddProvider(log));
        });

        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<A2AException>(() =>
                host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = "x" }, Call("mallory", $"made-up-{i}")));

        // Alice, in a real tenant, goes over the threshold: she is still tracked and reported.
        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<A2AException>(() =>
                host.Handler.CancelTaskAsync(new CancelTaskRequest { Id = $"missing-{i}" }, Call("alice", "acme")));

        Assert.Contains(SecurityEventLog.HashPrincipal("alice"), Assert.Single(log.Anomalies()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Creations_and_cancellations_have_separate_counts()
    {
        var log = new CaptureProvider();
        await using var host = await Start(log, new FakeTimeProvider(), a =>
        {
            a.TaskCreationsPerMinute = 2;
            a.CancellationsPerMinute = 2;
        });

        await CreateTasks(host, 2);
        await CancelMissing(host, 2);

        Assert.Empty(log.Anomalies());
    }

    [Fact]
    public async Task Callers_are_counted_separately_by_principal_and_tenant()
    {
        var log = new CaptureProvider();
        await using var host = await Start(log, new FakeTimeProvider(), a => a.TaskCreationsPerMinute = 2);

        await CreateTasks(host, 2, "alice");
        await CreateTasks(host, 2, "bob");
        await CreateTasks(host, 2, "alice", "other");
        Assert.Empty(log.Anomalies());

        await CreateTasks(host, 1, "bob");
        var line = Assert.Single(log.Anomalies());
        Assert.Contains(SecurityEventLog.HashPrincipal("bob"), line, StringComparison.Ordinal);

        await CreateTasks(host, 1, "alice");
        Assert.Equal(2, log.Anomalies().Count);
        Assert.Contains(SecurityEventLog.HashPrincipal("alice"), log.Anomalies()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_cap_stops_tracking_new_callers_until_a_window_ends()
    {
        var log = new CaptureProvider();
        var clock = new FakeTimeProvider();
        await using var host = await Start(log, clock, a =>
        {
            a.TaskCreationsPerMinute = 1;
            a.MaxTrackedCallers = 2;
        });

        await CreateTasks(host, 1, "alice");
        await CreateTasks(host, 1, "bob");
        await CreateTasks(host, 3, "carol"); // the table is full: carol is not tracked
        Assert.Empty(log.Anomalies());

        // Tracked callers keep being counted.
        await CreateTasks(host, 1, "alice");
        Assert.Contains(SecurityEventLog.HashPrincipal("alice"), Assert.Single(log.Anomalies()), StringComparison.Ordinal);

        // Once their windows end, their entries make room.
        clock.Advance(TimeSpan.FromMinutes(1));
        await CreateTasks(host, 2, "carol");
        Assert.Equal(2, log.Anomalies().Count);
        Assert.Contains(SecurityEventLog.HashPrincipal("carol"), log.Anomalies()[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Memory_stays_bounded_under_many_callers()
    {
        var options = new A2AServerOptions();
        options.Anomalies.Enabled = true;
        options.Anomalies.MaxTrackedCallers = 100;
        var clock = new FakeTimeProvider();
        var detector = AnomalyDetector.Create(options, clock)!;

        Parallel.For(0, 10_000, i => detector.TaskCreated(Call($"caller-{i}"), "SendMessage"));
        Assert.Equal(100, detector.TrackedCallers);

        // Ended windows are dropped when room is needed; the table never grows past the cap. It may end below the cap:
        // one thread scans, and callers that arrive while it removes entries are not tracked (the clock is frozen, so no
        // second scan runs). The scanning thread then adds its own caller, so at least one is tracked.
        clock.Advance(TimeSpan.FromMinutes(1));
        // One caller first, alone: it runs the one scan that is due and frees the 100 ended entries. In parallel, that scan
        // can still be running while most of the other calls find the table full and are not tracked (they do not wait
        // for it), so how many of them then fit depended on thread timing.
        detector.TaskCreated(Call("new-first"), "SendMessage");
        Assert.Equal(1, detector.TrackedCallers);
        Parallel.For(0, 10_000, i => detector.TaskCreated(Call($"new-{i}"), "SendMessage"));
        Assert.InRange(detector.TrackedCallers, 1, 100);
    }

    [Fact]
    public void Disabled_detector_is_not_built()
    {
        Assert.Null(AnomalyDetector.Create(new A2AServerOptions(), TimeProvider.System));
    }

    [Theory]
    [InlineData(0, 20, 10)]
    [InlineData(60, 0, 10)]
    [InlineData(60, 20, 0)]
    public void Settings_out_of_range_are_refused(int creations, int cancellations, int callers)
    {
        var options = new A2AServerOptions();
        options.Anomalies.Enabled = true;
        options.Anomalies.TaskCreationsPerMinute = creations;
        options.Anomalies.CancellationsPerMinute = cancellations;
        options.Anomalies.MaxTrackedCallers = callers;

        Assert.Throws<InvalidOperationException>(() => AnomalyDetector.Create(options, TimeProvider.System));
    }
}
