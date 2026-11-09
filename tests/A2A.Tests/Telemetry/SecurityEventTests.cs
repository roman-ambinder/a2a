using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using A2A.Diagnostics;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Security.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace A2A.Tests.Telemetry;

/// <summary>Security events (spec 13.4): <c>a2a.server.security.events</c> and one structured log line per event. Meter listeners are process-wide, so these tests run alone.</summary>
[Collection(TelemetryCollection.Name)]
public sealed class SecurityEventTests
{
    private sealed class Events : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentQueue<(string Event, string? Operation)> _seen = new();

        public Events()
        {
            _listener.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == A2ATelemetry.ServerMeterName && i.Name == A2ATelemetry.Metrics.SecurityEvents) l.EnableMeasurementEvents(i);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                string? name = null, operation = null;
                foreach (var t in tags)
                {
                    if (t.Key == A2ATelemetry.Tags.SecurityEvent) name = t.Value as string;
                    if (t.Key == A2ATelemetry.Tags.Operation) operation = t.Value as string;
                }
                for (var n = 0; n < value; n++) _seen.Enqueue((name ?? "?", operation));
            });
            _listener.Start();
        }

        public int Count(string name) => _seen.Count(e => e.Event == name);
        public int Total => _seen.Count;
        public string? OperationOf(string name) => _seen.FirstOrDefault(e => e.Event == name).Operation;
        public void Dispose() => _listener.Dispose();
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, int Id, string Message, string State)> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Sink(this);
        public void Dispose() { }

        private sealed class Sink(CaptureProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var pairs = state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(";", kv.Select(p => $"{p.Key}={p.Value}")) : "";
                owner.Entries.Enqueue((logLevel, eventId.Id, formatter(state, exception), pairs));
            }
        }
    }

    private sealed class DenyAll : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }

    private sealed class DenyGetTask : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.GetTask);
    }

    private static Action<IServiceCollection> With<T>(CaptureProvider log) where T : A2AAuthorizationHandler =>
        s =>
        {
            s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, T>());
            s.AddLogging(b => b.AddProvider(log));
        };

    private static Action<IServiceCollection> Logging(CaptureProvider log) => s => s.AddLogging(b => b.AddProvider(log));

    private static IEnumerable<(LogLevel Level, int Id, string Message, string State)> SecurityLines(CaptureProvider log) =>
        log.Entries.Where(e => e.Id == 1200 || e.Id == 1201);

    private static ServerCallContext Anonymous() => new() { ProtocolVersion = A2AProtocol.Version };

    [Fact]
    public async Task Unauthenticated_call_is_counted_once_and_logged_at_information()
    {
        var log = new CaptureProvider();
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync(services: Logging(log));

        var ex = await Assert.ThrowsAsync<A2AException>(async () => await host.Handler.GetTaskAsync(new GetTaskRequest { Id = "t1" }, Anonymous()));

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.Unauthenticated));
        var line = Assert.Single(SecurityLines(log));
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("unauthenticated", line.Message);
    }

    [Fact]
    public async Task Call_validation_of_an_anonymous_caller_is_counted()
    {
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync();

        Assert.Equal(A2AErrorCode.Unauthenticated, Assert.Throws<A2AException>(() => host.Handler.ValidateCall(Anonymous())).Code);

        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.Unauthenticated));
    }

    [Fact]
    public async Task A_denied_send_is_permission_denied_at_warning()
    {
        var log = new CaptureProvider();
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync(services: With<DenyAll>(log));

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson).SendMessageAsync("hello"));

        Assert.Equal(A2AErrorCode.PermissionDenied, ex.Code);
        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.PermissionDenied));
        Assert.Equal("SendMessage", events.OperationOf(A2ATelemetry.SecurityEventNames.PermissionDenied));
        Assert.Equal(LogLevel.Warning, Assert.Single(SecurityLines(log)).Level);
    }

    [Fact]
    public async Task A_denial_reported_as_task_not_found_still_counts_and_names_the_task()
    {
        var log = new CaptureProvider();
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync(services: With<DenyGetTask>(log));

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson, options: new A2A.Client.A2AClientOptions { MaxRetries = 0 }).GetTaskAsync("t-123"));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.PermissionDenied));
        Assert.Contains("t-123", Assert.Single(SecurityLines(log)).State);
    }

    [Fact]
    public async Task Reading_another_callers_task_is_not_detected()
    {
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(ProtocolBindings.HttpJson, "alice").SendMessageAsync("task")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson, "bob").GetTaskAsync(task.Id));

        // The store lookup is scoped by owner: a foreign task reads as "not found", so there is nothing to count.
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(0, events.Total);
    }

    [Fact]
    public async Task Using_another_callers_context_is_context_denied()
    {
        var log = new CaptureProvider();
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync(services: Logging(log));
        var task = (await host.Client(ProtocolBindings.HttpJson, "alice").SendMessageAsync("task")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson, "bob").SendMessageAsync("task", task.ContextId));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.ContextDenied));
        var line = Assert.Single(SecurityLines(log));
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains(task.ContextId, line.State);
    }

    [Fact]
    public async Task A_refused_webhook_url_is_counted_without_the_url_in_the_log()
    {
        var log = new CaptureProvider();
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync(o => o.PushNotifications.AllowPrivateNetworks = false, services: Logging(log));
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();

        await Assert.ThrowsAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(
            new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://127.0.0.1/secret-hook-path?token=abc" }));

        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.WebhookRefused));
        var line = Assert.Single(SecurityLines(log));
        Assert.DoesNotContain("secret-hook-path", line.Message + line.State);
        Assert.DoesNotContain("127.0.0.1", line.Message + line.State);
    }

    [Fact]
    public async Task A_refused_inline_webhook_url_is_counted()
    {
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync(o => o.PushNotifications.AllowPrivateNetworks = false);

        await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(Message.FromUser("x"),
            new SendMessageConfiguration { TaskPushNotificationConfig = new TaskPushNotificationConfig { Url = "http://127.0.0.1/hook" } }));

        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.WebhookRefused));
    }

    [Fact]
    public async Task A_webhook_blocked_at_delivery_is_counted()
    {
        using var events = new Events();
        var original = PrivateAddressGuard.Resolve;
        try
        {
            PrivateAddressGuard.Resolve = (_, _) => ValueTask.FromResult(new[] { IPAddress.Parse("10.0.0.5") });
            var hooks = new ScriptedHooks(HttpStatusCode.OK);
            var rig = new PushRig(hooks);
            rig.Options.PushNotifications.AllowPrivateNetworks = false;
            await rig.SaveConfigAsync(new TaskPushNotificationConfig { Url = "https://evil.example/h" });
            for (var i = 1; i < 7; i++) await rig.CommitAsync();
            await rig.CommitAsync("c1");
            using var service = rig.NewService();

            await rig.DrainAsync(service);

            Assert.Empty(hooks.Requests);
            Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.WebhookRefused));
        }
        finally { PrivateAddressGuard.Resolve = original; }
    }

    [Fact]
    public async Task A_reused_idempotency_key_with_another_request_is_counted()
    {
        var log = new CaptureProvider();
        using var events = new Events();
        await using var host = await TestAgentHost.StartAsync(services: Logging(log));
        var client = host.Client(ProtocolBindings.JsonRpc);
        var first = new Message { MessageId = "same-id", Role = Role.User, Parts = [Part.FromText("hello")] };
        var second = new Message { MessageId = "same-id", Role = Role.User, Parts = [Part.FromText("something else")] };
        await client.SendMessageAsync(first);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(second));

        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
        Assert.Equal(1, events.Count(A2ATelemetry.SecurityEventNames.IdempotencyKeyMismatch));
        Assert.Equal(LogLevel.Warning, Assert.Single(SecurityLines(log)).Level);
    }

    [Fact]
    public void Caller_supplied_ids_are_single_line_and_capped_in_the_log()
    {
        var log = new CaptureProvider();
        var longId = new string('x', 5000);

        A2A.Server.Telemetry.SecurityEventLog.Record(log.CreateLogger("test"), A2ATelemetry.SecurityEventNames.PermissionDenied, "op",
            "tenant\r\nFORGED tenant line", "p", longId + "\nFORGED resource line");

        var line = Assert.Single(SecurityLines(log));
        Assert.DoesNotContain('\n', line.Message);
        Assert.DoesNotContain('\r', line.Message);
        Assert.DoesNotContain(longId, line.Message);
    }

    [Fact]
    public async Task The_log_has_the_principal_hash_and_never_the_principal()
    {
        const string Principal = "alice@corp.example";
        var log = new CaptureProvider();
        await using var host = await TestAgentHost.StartAsync(services: With<DenyAll>(log));

        await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.HttpJson, Principal).SendMessageAsync("hello"));

        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Principal)))[..16].ToLowerInvariant();
        var line = Assert.Single(SecurityLines(log));
        Assert.Contains(expected, line.Message);
        Assert.Contains(expected, line.State);
        Assert.All(log.Entries, e => Assert.DoesNotContain(Principal, e.Message + e.State));
    }
}
