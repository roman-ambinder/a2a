using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using A2A.Server;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Storage;
using A2A.Server.Streaming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Pipeline;

public sealed class OperationFilterTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // ---------------------------------------------------------------- test filters

    /// <summary>Counts calls and records what it saw. Each test registers its own instance.</summary>
    private class Recorder(List<string>? log = null) : IA2AOperationFilter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string? Tenant { get; private set; }
        public A2AOperationDescriptor? Operation { get; private set; }

        public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            Tenant = context.Call.Tenant;
            Operation = context.Operation;
            if (log is not null) lock (log) log.Add(GetType().Name);
            return next(context, request, cancellationToken);
        }
    }

    private sealed class FilterA(List<string> log) : Recorder(log);
    private sealed class FilterB(List<string> log) : Recorder(log);
    private sealed class FilterC(List<string> log) : Recorder(log);

    private sealed class Blocker : IA2AOperationFilter
    {
        public ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken) =>
            throw A2AException.InvalidParams("blocked by a filter");
    }

    /// <summary>Wraps a stream and counts the events that pass.</summary>
    private sealed class EventCounter : IA2AOperationFilter
    {
        private int _events;
        public int Events => Volatile.Read(ref _events);

        public async ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
        {
            var result = await next(context, request, cancellationToken);
            return result is IAsyncEnumerable<SequencedEvent> events ? Count(events) : result;
        }

        private async IAsyncEnumerable<SequencedEvent> Count(IAsyncEnumerable<SequencedEvent> events, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in events.WithCancellation(ct))
            {
                Interlocked.Increment(ref _events);
                yield return e;
            }
        }
    }

    /// <summary>Changes the status message text of every task and status event it sees, while <see cref="Enabled"/>.</summary>
    private sealed class Mutator : IA2AOperationFilter
    {
        public const string Text = "changed by a filter";
        public volatile bool Enabled = true;

        public async ValueTask<object?> InvokeAsync(A2AOperationContext context, object request, A2AOperationDelegate next, CancellationToken cancellationToken)
        {
            var result = await next(context, request, cancellationToken);
            if (!Enabled) return result;
            switch (result)
            {
                case AgentTask task:
                    Change(task.Status);
                    return task;
                case IAsyncEnumerable<SequencedEvent> events:
                    return ChangeAll(events);
                default:
                    return result;
            }
        }

        private static void Change(AgentTaskStatus status)
        {
            if (status.Message is { } message) message.Parts[0] = Part.FromText(Text);
        }

        private static async IAsyncEnumerable<SequencedEvent> ChangeAll(IAsyncEnumerable<SequencedEvent> events, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await foreach (var e in events.WithCancellation(ct))
            {
                switch (e.Event)
                {
                    case TaskEvent t: Change(t.Task.Status); break;
                    case StatusUpdateEvent s: Change(s.StatusUpdate.Status); break;
                }
                yield return e;
            }
        }
    }

    private sealed class DenyAll : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }

    /// <summary>Registers <paramref name="filter"/> as the instance of <typeparamref name="T"/> and adds it to the pipeline.</summary>
    private static Action<IServiceCollection> With<T>(T filter, A2AFilterStage stage = A2AFilterStage.AfterAuthorization,
        Action<IServiceCollection>? more = null) where T : class, IA2AOperationFilter =>
        s =>
        {
            s.AddSingleton(filter);
            s.AddA2AServer().AddOperationFilter<T>(stage);
            more?.Invoke(s);
        };

    private static void DenyEverything(IServiceCollection s) => s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyAll>());

    // ---------------------------------------------------------------- stages and order

    [Fact]
    public async Task Filter_after_authorization_sees_only_authorized_calls()
    {
        var filter = new Recorder();
        await using var host = await TestAgentHost.StartAsync(services: With(filter, more: DenyEverything));

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).GetTaskAsync("t-1"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(0, filter.Calls);
    }

    [Fact]
    public async Task Filter_before_authorization_sees_denied_calls()
    {
        var filter = new Recorder();
        await using var host = await TestAgentHost.StartAsync(services: With(filter, A2AFilterStage.BeforeAuthorization, DenyEverything));

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).GetTaskAsync("t-1"));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(1, filter.Calls);
        Assert.Equal(A2AOperationDescriptor.For(A2AOperation.GetTask), filter.Operation);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Filter_can_short_circuit_with_A2AException(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: With(new Blocker()));

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).GetTaskAsync("t-1"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Contains("blocked by a filter", ex.Message);
    }

    [Fact]
    public async Task Filter_sees_tenant_bound_from_body()
    {
        var filter = new Recorder();
        await using var host = await TestAgentHost.StartAsync(services: With(filter));
        using var http = host.Http("alice");
        using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(
                $$$"""{"jsonrpc":"2.0","id":1,"method":"{{{A2AProtocol.Methods.GetTask}}}","params":{"tenant":"t1","id":"t-1"}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.TryGetProperty("error", out _)); // the task does not exist; the filter still ran after authorization

        Assert.Equal(1, filter.Calls);
        Assert.Equal("t1", filter.Tenant);
    }

    [Fact]
    public async Task Filters_run_in_registration_order()
    {
        var log = new List<string>();
        await using var host = await TestAgentHost.StartAsync(services: s =>
        {
            s.AddSingleton(new FilterA(log));
            s.AddSingleton(new FilterB(log));
            s.AddSingleton(new FilterC(log));
            s.AddA2AServer()
                .AddOperationFilter<FilterA>()
                .AddOperationFilter<FilterC>(A2AFilterStage.BeforeAuthorization)
                .AddOperationFilter<FilterB>();
        });

        await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("hello");

        Assert.Equal([nameof(FilterC), nameof(FilterA), nameof(FilterB)], log);
    }

    // ---------------------------------------------------------------- streaming

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Streaming_filter_wraps_enumerable_and_counts_events(string binding)
    {
        var filter = new EventCounter();
        await using var host = await TestAgentHost.StartAsync(services: With(filter));

        var received = 0;
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var _ in host.Client(binding).SendStreamingMessageAsync(Message.FromUser("task"), cancellationToken: cts.Token)) received++;

        Assert.True(received >= 3, $"only {received} events");
        Assert.Equal(received, filter.Events);
    }

    [Fact]
    public async Task Streaming_errors_still_surface_before_the_first_event()
    {
        // Authorization stays lazy: the denial is the HTTP error of the stream request, not a broken event stream.
        var filter = new EventCounter();
        await using var host = await TestAgentHost.StartAsync(services: With(filter, more: DenyEverything));

        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in host.Client(ProtocolBindings.HttpJson).SubscribeToTaskAsync("t-1")) { }
        });
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(0, filter.Events);
    }

    // ---------------------------------------------------------------- detached results

    [Fact]
    public async Task Host_filter_receives_detached_task()
    {
        var filter = new Mutator { Enabled = false };
        await using var host = await TestAgentHost.StartAsync(services: With(filter));
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();

        filter.Enabled = true;
        var changed = await client.GetTaskAsync(task.Id);
        Assert.Equal(Mutator.Text, changed.Status.Message!.Parts[0].TextOf());

        filter.Enabled = false;
        var again = await client.GetTaskAsync(task.Id);
        Assert.Equal("need more", again.Status.Message!.Parts[0].TextOf());
        var stored = await host.App.Services.GetRequiredService<TaskStore>().GetAsync(new TaskKey("", "alice", task.Id));
        Assert.Equal("need more", stored!.Task.Status.Message!.Parts[0].TextOf());
    }

    [Fact]
    public async Task Streaming_host_filter_sees_detached_events()
    {
        var filter = new Mutator();
        await using var host = await TestAgentHost.StartAsync(services: With(filter));
        var client = host.Client(ProtocolBindings.JsonRpc);

        string? taskId = null;
        var changedTexts = new List<string?>();
        using var cts = new CancellationTokenSource(Timeout);
        await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("task"), cancellationToken: cts.Token))
        {
            taskId ??= e.TaskId;
            if (e is StatusUpdateEvent s) changedTexts.Add(s.StatusUpdate.Status.Message?.Parts[0].TextOf());
        }
        Assert.Contains(Mutator.Text, changedTexts);

        Assert.DoesNotContain("done", changedTexts);

        filter.Enabled = false;
        Assert.Equal("done", (await client.GetTaskAsync(taskId!)).Status.Message!.Parts[0].TextOf());
        var key = new TaskKey("", "alice", taskId!);
        var storedTexts = new List<string?>();
        await foreach (var stored in host.App.Services.GetRequiredService<EventStore>().ReadAfterAsync(key, 0))
            if (stored.Event is StatusUpdateEvent s) storedTexts.Add(s.StatusUpdate.Status.Message?.Parts[0].TextOf());
        Assert.Contains("done", storedTexts);
        Assert.DoesNotContain(Mutator.Text, storedTexts);
    }

    [Fact]
    public async Task No_host_filters_means_no_copy()
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input")).RequireTask();
        var stored = await host.App.Services.GetRequiredService<TaskStore>().GetAsync(new TaskKey("", "alice", task.Id));

        var result = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = task.Id }, AliceCall());

        Assert.Same(stored!.Task.Status, result.Status);
    }

    [Fact]
    public async Task Host_filter_means_a_copy_on_a_direct_call()
    {
        await using var host = await TestAgentHost.StartAsync(services: With(new Recorder()));
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input")).RequireTask();
        var stored = await host.App.Services.GetRequiredService<TaskStore>().GetAsync(new TaskKey("", "alice", task.Id));

        var result = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = task.Id }, AliceCall());

        Assert.NotSame(stored!.Task.Status, result.Status);
        Assert.Equal(stored.Task.Status.Message!.Parts[0].TextOf(), result.Status.Message!.Parts[0].TextOf());
    }

    // ---------------------------------------------------------------- descriptors

    [Fact]
    public void Descriptors_name_the_builtin_operations()
    {
        foreach (var kind in Enum.GetValues<A2AOperation>().Where(k => k != A2AOperation.Extension))
        {
            var op = A2AOperationDescriptor.For(kind);
            Assert.Equal(kind, op.Kind);
            Assert.Equal(kind.ToString(), op.Name);
            Assert.Same(op, A2AOperationDescriptor.For(kind));
        }
        Assert.Throws<ArgumentException>(() => A2AOperationDescriptor.For(A2AOperation.Extension));

        var ext = A2AOperationDescriptor.Extension("x.echo");
        Assert.Equal(A2AOperation.Extension, ext.Kind);
        Assert.Equal("x.echo", ext.Name);
        Assert.Throws<ArgumentException>(() => A2AOperationDescriptor.Extension(""));
    }

    [Fact]
    public async Task Default_authorization_denies_extension_operations()
    {
        var allowAll = new AllowAllAuthorizationHandler();
        Assert.True(await allowAll.AuthorizeAsync(AliceCall(), A2AOperationDescriptor.For(A2AOperation.GetTask), new GetTaskRequest(), default));
        Assert.False(await allowAll.AuthorizeAsync(AliceCall(), A2AOperationDescriptor.Extension("x.echo"), new object(), default));
    }

    private static ServerCallContext AliceCall() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
        ProtocolVersion = A2AProtocol.Version,
    };
}

internal static class PartTestExtensions
{
    public static string? TextOf(this Part part) => (part as TextPart)?.Text;
}
