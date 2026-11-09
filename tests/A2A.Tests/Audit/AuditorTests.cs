using System.Security.Claims;
using A2A.Server;
using A2A.Server.Audit;
using A2A.Server.Push;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Audit;

/// <summary>The auditor on its own: cleaning, the detached cap, DI wiring, and the default logger sink.</summary>
public class AuditorTests
{
    private static A2AAuditEvent Event(Func<A2AAuditEvent, A2AAuditEvent>? change = null)
    {
        var e = new A2AAuditEvent
        {
            Id = "id1", Time = DateTimeOffset.UtcNow, Action = A2AAuditAction.TaskCreated, Outcome = A2AAuditOutcome.Succeeded,
            Actor = "alice", Tenant = "",
        };
        return change is null ? e : change(e);
    }

    // ---- cleaning ----

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a\0b", "a_b")]
    [InlineData("line1\r\nline2", "line1__line2")]
    [InlineData("tab\there", "tab_here")]
    [InlineData("pair😀ok", "pair😀ok")]
    [InlineData("", "")]
    public void Clean_replaces_control_characters_and_lone_surrogates(string input, string expected) =>
        Assert.Equal(expected, A2AAuditor.Clean(input));

    [Fact]
    public void Clean_replaces_lone_surrogates()
    {
        // Built at run time: an attribute argument cannot hold a lone surrogate.
        Assert.Equal("lone_high", A2AAuditor.Clean("lone" + (char)0xD800 + "high"));
        Assert.Equal("lone_low", A2AAuditor.Clean("lone" + (char)0xDC00 + "low"));
        Assert.Equal("end_", A2AAuditor.Clean("end" + (char)0xD83D));
    }

    [Fact]
    public void Clean_keeps_null_and_cuts_at_256_characters()
    {
        Assert.Null(A2AAuditor.Clean(null));
        Assert.Equal(256, A2AAuditor.Clean(new string('x', 1000))!.Length);
        Assert.Equal(new string('x', 256), A2AAuditor.Clean(new string('x', 256)));
    }

    [Fact]
    public void Clean_never_cuts_a_surrogate_pair_in_two()
    {
        var split = new string('a', 255) + "😀" + "zzz"; // the pair would end at character 257
        var whole = new string('a', 254) + "😀" + "zzz"; // the pair ends exactly at 256

        Assert.Equal(new string('a', 255), A2AAuditor.Clean(split));
        Assert.Equal(new string('a', 254) + "😀", A2AAuditor.Clean(whole));
    }

    [Fact]
    public async Task The_sink_only_sees_cleaned_strings()
    {
        var sink = new RecordingAuditSink();
        var auditor = new A2AAuditor(sink, new A2AServerOptions());

        await auditor.RecordAsync(Event(e => e with
        {
            Actor = "al\0ice", TaskId = "t\r\n1", Resource = new string('r', 1000), Reason = "bad\ud800", Binding = "b\u0007",
        }));

        var seen = Assert.Single(sink.Events);
        Assert.Equal("al_ice", seen.Actor);
        Assert.Equal("t__1", seen.TaskId);
        Assert.Equal(256, seen.Resource!.Length);
        Assert.Equal("bad_", seen.Reason);
        Assert.Equal("b_", seen.Binding);
    }

    [Fact]
    public void Write_timeout_must_be_positive()
    {
        var options = new A2AServerOptions();
        options.Audit.WriteTimeout = TimeSpan.Zero;

        Assert.Throws<InvalidOperationException>(() => new A2AAuditor(new RecordingAuditSink(), options));
    }

    [Fact]
    public void Reason_never_holds_exception_text()
    {
        Assert.Equal("TaskNotFound", A2AAuditor.ReasonOf(A2AException.TaskNotFound("t1")));
        Assert.Equal("Canceled", A2AAuditor.ReasonOf(new OperationCanceledException("secret")));
        Assert.Equal("Error", A2AAuditor.ReasonOf(new InvalidOperationException("secret")));
    }

    // ---- detached writes ----

    private sealed class BlockedSink : A2AAuditSink
    {
        private int _entered;
        public TaskCompletionSource All { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _entered) == A2AAuditor.MaxDetached) All.TrySetResult();
            await Release.Task;
        }
    }

    [Fact]
    public async Task Detached_writes_are_capped_and_the_rest_are_dropped_with_a_log()
    {
        var sink = new BlockedSink();
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var auditor = new A2AAuditor(sink, new A2AServerOptions(), factory.CreateLogger<A2AAuditor>(), new FakeTimeProvider());

        for (var i = 0; i < A2AAuditor.MaxDetached + 76; i++) auditor.RecordDetached(Event());

        Assert.Equal(A2AAuditor.MaxDetached, auditor.DetachedInFlight);
        await sink.All.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(76, logs.Entries.Count(l => l.Id.Id == 273));
        sink.Release.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (auditor.DetachedInFlight != 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "detached writes never finished");
            await Task.Yield();
        }
    }

    // ---- DI ----

    [Fact]
    public async Task Container_gives_the_auditor_its_logger_and_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddSingleton<TimeProvider>(clock);
        services.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard()).AddAuditSink(new RecordingAuditSink { FailWhen = _ => true });
        await using var provider = services.BuildServiceProvider();
        var auditor = provider.GetRequiredService<A2AAuditor>();

        var e = auditor.For(A2AAuditAction.TaskCreated, new ServerCallContext(), "t1");
        await auditor.RecordAsync(e);

        Assert.Equal(clock.GetUtcNow(), e.Time);
        Assert.Contains(logs.Entries, l => l.Category == typeof(A2AAuditor).FullName && l.Level == LogLevel.Error);
    }

    private sealed class MySink : A2AAuditSink
    {
        public override ValueTask WriteAsync(A2AAuditEvent auditEvent, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    [Fact]
    public void Logger_sink_is_the_default_and_AddAuditSink_replaces_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var builder = services.AddA2AServer(o => o.AgentCard = TestAgentHost.DefaultCard());
        using (var p = services.BuildServiceProvider()) Assert.IsType<LoggerAuditSink>(p.GetRequiredService<A2AAuditSink>());

        builder.AddAuditSink<MySink>();
        using (var p = services.BuildServiceProvider()) Assert.IsType<MySink>(p.GetRequiredService<A2AAuditSink>());

        var instance = new RecordingAuditSink();
        builder.AddAuditSink(instance);
        using (var p = services.BuildServiceProvider()) Assert.Same(instance, p.GetRequiredService<A2AAuditSink>());
    }

    // ---- the logger sink ----

    [Fact]
    public async Task Logger_sink_writes_one_Information_log_with_the_fields()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var sink = new LoggerAuditSink(factory.CreateLogger<LoggerAuditSink>());

        await sink.WriteAsync(Event(e => e with { TaskId = "task-9", Resource = "res-1", Operation = "GetTask", Reason = "PermissionDenied", Binding = "GRPC", RemoteAddress = "203.0.113.9" }),
            CancellationToken.None);

        var log = Assert.Single(logs.Entries);
        Assert.Equal("A2A.Server.Audit.LoggerAuditSink", log.Category);
        Assert.Equal(LogLevel.Information, log.Level);
        var state = log.State.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("id1", state["AuditId"]);
        Assert.Equal(A2AAuditAction.TaskCreated, state["AuditAction"]);
        Assert.Equal(A2AAuditOutcome.Succeeded, state["AuditOutcome"]);
        // By default a log line holds the same hash as the security event log, and no address.
        Assert.Equal("sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("alice")))[..16].ToLowerInvariant(), state["AuditActor"]);
        Assert.Null(state["AuditRemoteAddress"]);
        Assert.Equal("task-9", state["AuditTaskId"]);
        Assert.Equal("res-1", state["AuditResource"]);
        Assert.Equal("GetTask", state["AuditOperation"]);
        Assert.Equal("PermissionDenied", state["AuditReason"]);
        Assert.Equal("GRPC", state["AuditBinding"]);
        Assert.Contains("TaskCreated", log.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logger_sink_does_nothing_when_logging_is_off()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Warning));

        await new LoggerAuditSink(factory.CreateLogger<LoggerAuditSink>()).WriteAsync(Event(), CancellationToken.None);
        await new LoggerAuditSink().WriteAsync(Event(), CancellationToken.None);

        Assert.Empty(logs.Entries);
    }

    [Fact]
    public async Task LogIdentity_writes_the_actor_and_the_address_in_clear()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var options = new A2AServerOptions();
        options.Audit.LogIdentity = true;

        await new LoggerAuditSink(factory.CreateLogger<LoggerAuditSink>(), options).WriteAsync(Event(e => e with { RemoteAddress = "203.0.113.9" }), CancellationToken.None);

        var state = Assert.Single(logs.Entries).State.ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal("alice", state["AuditActor"]);
        Assert.Equal("203.0.113.9", state["AuditRemoteAddress"]);
    }
}
