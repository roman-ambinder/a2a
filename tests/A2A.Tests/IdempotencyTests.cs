using A2A.Client;
using System.Diagnostics.Metrics;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using A2A.Client.Sse;
using A2A.Json;
using A2A.Server;
using A2A.Server.Idempotency;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>Idempotent SendMessage on the server (spec design §7.3), over raw HTTP on both bindings.</summary>
public class IdempotencyTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    /// <summary>Generous: the suite runs in parallel and machines get loaded.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Retry_with_same_message_id_returns_same_task(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Body("task", "m-retry");

        var first = await SendTaskAsync(host, binding, body);
        var second = await SendTaskAsync(host, binding, body);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TaskState.Completed, second.Status.State);
        var list = await host.Client(binding).ListTasksAsync(new ListTasksRequest());
        Assert.Single(list.Tasks);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Idempotency_header_is_the_key_when_present(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();

        await SendTaskAsync(host, binding, Body("task", "m1"), idempotencyKey: "K");
        var (status, body) = await SendAsync(host, Send(binding, Body("task", "m2"), idempotencyKey: "K"));

        AssertMismatch(status, body);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Same_key_different_payload_is_mismatch(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();

        await SendTaskAsync(host, binding, Body("task", "m-same"));
        var (status, body) = await SendAsync(host, Send(binding, Body("task changed", "m-same")));

        AssertMismatch(status, body);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Concurrent_duplicates_create_one_task(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Body("slow", "m-concurrent", returnImmediately: true);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => SendTaskAsync(host, binding, body))).WaitAsync(Timeout);

        Assert.Single(results.Select(t => t.Id).Distinct());
        var list = await host.Client(binding).ListTasksAsync(new ListTasksRequest());
        Assert.Single(list.Tasks);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Keys_are_scoped_per_principal(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Body("task", "m-shared");

        var alice = await SendTaskAsync(host, binding, body, idempotencyKey: "K", user: "alice");
        var aliceAgain = await SendTaskAsync(host, binding, body, idempotencyKey: "K", user: "alice");
        var bob = await SendTaskAsync(host, binding, body, idempotencyKey: "K", user: "bob");

        Assert.Equal(alice.Id, aliceAgain.Id);
        Assert.NotEqual(alice.Id, bob.Id);
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, user: "bob").GetTaskAsync(alice.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Streaming_retry_replays_from_start(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Body("task", "m-stream");

        var first = await ReadStreamAsync(host, Send(binding, body, streaming: true));
        var second = await ReadStreamAsync(host, Send(binding, body, streaming: true));

        Assert.True(first.Count >= 5, $"expected the whole task stream, got {first.Count} frames");
        Assert.Equal(first, second);
        var list = await host.Client(binding).ListTasksAsync(new ListTasksRequest());
        Assert.Single(list.Tasks);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Direct_reply_is_replayed(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Body("hello", "m-hello");

        var first = await SendMessageAsync(host, binding, body);
        var second = await SendMessageAsync(host, binding, body);

        Assert.Equal("hi there", first.GetText());
        Assert.Equal(first.MessageId, second.MessageId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Entry_expires_after_retention(string binding)
    {
        var clock = new FakeTimeProvider();
        await using var host = await TestAgentHost.StartAsync(services: s => s.AddSingleton<TimeProvider>(clock));
        var body = Body("task", "m-expiry");

        var first = await SendTaskAsync(host, binding, body);
        var retried = await SendTaskAsync(host, binding, body);
        clock.Advance(TimeSpan.FromHours(25)); // default retention is 24 h
        var later = await SendTaskAsync(host, binding, body);

        Assert.Equal(first.Id, retried.Id);
        Assert.NotEqual(first.Id, later.Id);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Request_that_produced_nothing_can_be_retried(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Body("silent", "m-silent");

        var (status1, first) = await SendAsync(host, Send(binding, body));
        var (status2, second) = await SendAsync(host, Send(binding, body)); // abandoned, so it runs again instead of hanging

        Assert.Contains("INVALID_AGENT_RESPONSE", first.GetProperty("error").GetRawText());
        Assert.Contains("INVALID_AGENT_RESPONSE", second.GetProperty("error").GetRawText());
        Assert.Equal(status1, status2);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Follow_up_retry_does_not_append_twice(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var waiting = await SendTaskAsync(host, binding, Body("input", "m-input"));
        var followUp = $$$"""{"message":{"messageId":"m-follow","role":"ROLE_USER","taskId":"{{{waiting.Id}}}","parts":[{"text":"more"}]}}""";

        var first = await SendTaskAsync(host, binding, followUp);
        var retried = await SendTaskAsync(host, binding, followUp); // the task is terminal now: a second run would be refused

        Assert.Equal(waiting.Id, retried.Id);
        Assert.Equal(TaskState.Completed, first.Status.State);
        var task = await host.Client(binding).GetTaskAsync(waiting.Id);
        Assert.Single(task.History!, m => m.MessageId == "m-follow");
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Blocking_duplicate_waits_for_the_task_to_finish(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var text = $"gate {Guid.NewGuid():N}";
        var body = Body(text, "m-blocking");

        var original = SendTaskAsync(host, binding, body);
        var duplicate = SendTaskAsync(host, binding, body);
        await Task.Delay(300); // only gives a broken server time to answer early; a correct one cannot answer before the gate opens
        Assert.False(original.IsCompleted || duplicate.IsCompleted, "a blocking send returned before the task finished");
        ScenarioAgent.Gate(text).TrySetResult();
        var results = await Task.WhenAll(original, duplicate).WaitAsync(Timeout);

        Assert.Equal(results[0].Id, results[1].Id);
        Assert.All(results, t => Assert.Equal(TaskState.Completed, t.Status.State));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Blocking_retry_of_a_running_task_waits_for_it_to_finish(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var text = $"gate {Guid.NewGuid():N}";
        var body = Body(text, "m-running");

        // The first attempt is a stream (same message and configuration, so the same request); it has created the task.
        using var http = host.Http();
        using var cts = new CancellationTokenSource(Timeout);
        using var stream = await http.SendAsync(Send(binding, body, streaming: true), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using var frames = SseReader.ReadAsync(await stream.Content.ReadAsStreamAsync(cts.Token), cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await frames.MoveNextAsync());

        var retry = SendTaskAsync(host, binding, body);
        await Task.Delay(300); // as above: only a broken server answers in this window
        Assert.False(retry.IsCompleted, "the blocking retry returned before the task finished");
        ScenarioAgent.Gate(text).TrySetResult();
        var task = await retry.WaitAsync(Timeout);

        Assert.Equal(TaskState.Completed, task.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Blocking_duplicate_follow_up_waits_for_the_new_outcome(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var waiting = await SendTaskAsync(host, binding, Body("input", "m-input-gate"));
        var text = $"gate {Guid.NewGuid():N}";
        var followUp = $$$"""{"message":{"messageId":"m-follow-gate","role":"ROLE_USER","taskId":"{{{waiting.Id}}}","parts":[{"text":"{{{text}}}"}]}}""";

        var original = SendTaskAsync(host, binding, followUp);
        var duplicate = SendTaskAsync(host, binding, followUp);
        await Task.Delay(300); // only a broken server answers in this window: the task is still INPUT_REQUIRED from before
        Assert.False(original.IsCompleted || duplicate.IsCompleted, "a blocking follow-up returned the earlier interrupted state");
        ScenarioAgent.Gate(text).TrySetResult();
        var results = await Task.WhenAll(original, duplicate).WaitAsync(Timeout);

        Assert.All(results, t => Assert.Equal(waiting.Id, t.Id));
        Assert.All(results, t => Assert.Equal(TaskState.Completed, t.Status.State));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Blocking_retry_returns_when_no_execution_is_left(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Body("halfway", "m-halfway");

        var first = await SendTaskAsync(host, binding, body); // the agent returned after starting work
        await host.WaitIdleAsync();
        var retried = await SendTaskAsync(host, binding, body).WaitAsync(Timeout);

        Assert.Equal(TaskState.Working, first.Status.State);
        Assert.Equal(first.Id, retried.Id);
        Assert.Equal(TaskState.Working, retried.Status.State);
    }

    [Fact]
    public async Task In_process_retry_with_the_same_request_instance_returns_the_same_task()
    {
        await using var host = await TestAgentHost.StartAsync();
        var request = new SendMessageRequest { Message = new Message { MessageId = "m-instance", Role = Role.User, Parts = [Part.FromText("task")] } };

        var first = (await host.Handler.SendMessageAsync(request, AliceCall())).RequireTask();
        var second = (await host.Handler.SendMessageAsync(request, AliceCall())).RequireTask();

        Assert.Equal(first.Id, second.Id);
        Assert.Null(request.Message.ContextId); // the caller's message is not changed
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Refused_follow_up_leaves_the_task_unchanged_and_retries_the_same_way(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var waiting = await SendTaskAsync(host, binding, Body("input", "m-input-push"));
        var before = (await host.Client(binding).GetTaskAsync(waiting.Id)).History!.Count;
        var followUp = $$$"""{"message":{"messageId":"m-follow-push","role":"ROLE_USER","taskId":"{{{waiting.Id}}}","parts":[{"text":"more"}]},""" +
            "\"configuration\":{\"taskPushNotificationConfig\":{\"url\":\"http://user:pw@hooks.test/x\"}}}";

        var (status1, first) = await SendAsync(host, Send(binding, followUp));
        var afterFirst = (await host.Client(binding).GetTaskAsync(waiting.Id)).History!.Count;
        var (status2, second) = await SendAsync(host, Send(binding, followUp));
        var afterRetry = (await host.Client(binding).GetTaskAsync(waiting.Id)).History!.Count;

        Assert.Contains("credentials", first.GetProperty("error").GetRawText());
        Assert.Contains("credentials", second.GetProperty("error").GetRawText());
        Assert.Equal(status1, status2);
        Assert.Equal(before, afterFirst);
        Assert.Equal(before, afterRetry);
    }

    /// <summary>
    /// An event store that, once armed, cancels the request the moment it is asked for a task's last sequence with a token
    /// that can be cancelled, as if the client went away right then.
    /// </summary>
    private sealed class CancelOnLastSequenceStore : Server.Storage.EventStore
    {
        private readonly Server.Storage.InMemoryEventStore _inner = new(new Server.StreamingOptions());

        /// <summary>The wrapped store; the in-memory task store commits its events here.</summary>
        public Server.Storage.InMemoryEventStore Inner => _inner;

        public CancellationTokenSource? CancelWhenAsked;

        public override ValueTask<long> LastSequenceAsync(Server.Storage.TaskKey task, CancellationToken ct = default)
        {
            if (ct.CanBeCanceled && Interlocked.Exchange(ref CancelWhenAsked, null) is { } request)
            {
                request.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return _inner.LastSequenceAsync(task, ct);
        }

        public override ValueTask<long> AppendAsync(Server.Storage.TaskKey task, Server.Streaming.SequencedEvent evt, CancellationToken ct = default) => _inner.AppendAsync(task, evt, ct);

        public override IAsyncEnumerable<Server.Streaming.SequencedEvent> ReadAfterAsync(Server.Storage.TaskKey task, long afterSequence, CancellationToken ct = default) =>
            _inner.ReadAfterAsync(task, afterSequence, ct);

        public override ValueTask<long?> OldestRetainedAsync(Server.Storage.TaskKey task, CancellationToken ct = default) => _inner.OldestRetainedAsync(task, ct);

        public override ValueTask MarkTerminalAsync(Server.Storage.TaskKey task, CancellationToken ct = default) => _inner.MarkTerminalAsync(task, ct);

        public override ValueTask DeleteAsync(Server.Storage.TaskKey task, CancellationToken ct = default) => _inner.DeleteAsync(task, ct);
    }

    /// <summary>
    /// Once the follow-up message is committed to the task's history, a cancelled request must not abandon the idempotency
    /// entry: a retry would append the message a second time.
    /// </summary>
    [Fact]
    public async Task Follow_up_cancelled_after_its_message_is_stored_is_not_appended_again_on_retry()
    {
        var events = new CancelOnLastSequenceStore();
        await using var host = await TestAgentHost.StartAsync(services: s => s
            .AddSingleton<Server.Storage.EventStore>(events)
            .AddSingleton<Server.Storage.TaskStore>(new Server.Storage.InMemoryTaskStore(events.Inner)));
        var waiting = (await host.Handler.SendMessageAsync(new SendMessageRequest { Message = Message.FromUser("input") }, AliceCall())).RequireTask();
        SendMessageRequest FollowUp() => new()
        {
            Message = new Message { MessageId = "m-follow-cancel", Role = Role.User, TaskId = waiting.Id, Parts = [Part.FromText("more")] },
            Configuration = new SendMessageConfiguration { ReturnImmediately = true },
        };

        using (var request = new CancellationTokenSource())
        {
            events.CancelWhenAsked = request;
            try { await host.Handler.SendMessageAsync(FollowUp(), AliceCall(), request.Token); }
            catch (OperationCanceledException) { /* the client went away */ }
            events.CancelWhenAsked = null;
        }
        await host.Handler.SendMessageAsync(FollowUp(), AliceCall());
        await host.WaitIdleAsync();

        var task = await host.Handler.GetTaskAsync(new GetTaskRequest { Id = waiting.Id }, AliceCall());
        Assert.Single(task.History!, m => m.MessageId == "m-follow-cancel");
    }

    private static void AssertKeyTooLong(string binding, HttpStatusCode status, JsonElement body)
    {
        var error = body.GetProperty("error");
        if (binding == ProtocolBindings.JsonRpc) Assert.Equal((int)A2AErrorCode.InvalidParams, error.GetProperty("code").GetInt32());
        else Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Idempotency key too long.", error.GetProperty("message").GetString());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Idempotency_key_longer_than_256_characters_is_refused(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var tooLong = new string('k', 257);

        var (headerStatus, headerBody) = await SendAsync(host, Send(binding, Body("task", "m-short"), idempotencyKey: tooLong));
        AssertKeyTooLong(binding, headerStatus, headerBody);

        // Without the header the message id is the key, and the same cap applies, also to a stream.
        var (idStatus, idBody) = await SendAsync(host, Send(binding, Body("task", tooLong)));
        AssertKeyTooLong(binding, idStatus, idBody);
        var (streamStatus, streamBody) = await SendAsync(host, Send(binding, Body("task", tooLong), streaming: true));
        AssertKeyTooLong(binding, streamStatus, streamBody);

        Assert.Empty((await host.Client(binding).ListTasksAsync(new ListTasksRequest())).Tasks);
        await SendTaskAsync(host, binding, Body("task", new string('m', 256)), idempotencyKey: new string('k', 256)); // at the cap
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Keys_are_scoped_per_tenant(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        static string InTenant(string tenant) =>
            $$$"""{"tenant":"{{{tenant}}}","message":{"messageId":"m-tenant","role":"ROLE_USER","parts":[{"text":"task"}]}}""";

        var t1 = await SendTaskAsync(host, binding, InTenant("t1"), idempotencyKey: "K");
        var t1Again = await SendTaskAsync(host, binding, InTenant("t1"), idempotencyKey: "K");
        var t2 = await SendTaskAsync(host, binding, InTenant("t2"), idempotencyKey: "K");

        Assert.Equal(t1.Id, t1Again.Id);
        Assert.NotEqual(t1.Id, t2.Id);
    }

    private static ServerCallContext AliceCall() => new()
    {
        Principal = "alice",
        User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "Test")),
        ProtocolVersion = A2AProtocol.Version,
    };

    // ---------------------------------------------------------------- store and hash

    private static readonly IdempotencyScope ScopeA = new("", "alice", "a");
    private static readonly IdempotencyScope ScopeB = new("", "alice", "b");
    private static readonly IdempotencyScope ScopeC = new("", "alice", "c");
    private static readonly byte[] HashX = [1, 2, 3];

    [Fact]
    public async Task Store_evicts_least_recently_used_finished_entry_and_counts_it()
    {
        var store = new InMemoryIdempotencyStore(new IdempotencyOptions { MaxEntries = 2 });
        long evictions = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == InMemoryIdempotencyStore.MeterName && instrument.Name == InMemoryIdempotencyStore.EvictionsCounterName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref evictions, value));
        listener.Start();

        foreach (var scope in new[] { ScopeA, ScopeB })
        {
            var claim = Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(scope, HashX)).ClaimId;
            await store.CompleteAsync(scope, claim, new IdempotencyOutcome.TaskCreated(scope.Key));
        }
        Assert.IsType<IdempotencyResult.Completed>(await store.TryBeginAsync(ScopeA, HashX)); // A is now the most recent
        Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(ScopeC, HashX));       // over capacity: B goes

        Assert.Equal(1, Interlocked.Read(ref evictions));
        Assert.Equal(2, store.Count);
        Assert.IsType<IdempotencyResult.Completed>(await store.TryBeginAsync(ScopeA, HashX));
        Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(ScopeB, HashX));
    }

    [Fact]
    public async Task Store_never_evicts_a_running_entry()
    {
        var store = new InMemoryIdempotencyStore(new IdempotencyOptions { MaxEntries = 1 });
        Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(ScopeA, HashX));
        Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(ScopeB, HashX));

        Assert.IsType<IdempotencyResult.InProgress>(await store.TryBeginAsync(ScopeA, HashX));
    }

    [Fact]
    public async Task Abandoned_entry_releases_waiters_and_can_be_claimed_again()
    {
        var store = new InMemoryIdempotencyStore(new IdempotencyOptions());
        var claim = Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(ScopeA, HashX)).ClaimId;
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await store.TryBeginAsync(ScopeA, HashX));
        Assert.IsType<IdempotencyResult.Mismatch>(await store.TryBeginAsync(ScopeA, [9]));

        await store.AbandonAsync(ScopeA, claim);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.Completion.WaitAsync(Timeout));
        Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(ScopeA, [9]));
    }

    [Fact]
    public void Hash_covers_message_and_configuration_only()
    {
        var a = A2AJson.Deserialize<SendMessageRequest>(
            """{"tenant":"t1","metadata":{"x":1},"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hi"}]},"configuration":{"historyLength":2}}""");
        var reordered = A2AJson.Deserialize<SendMessageRequest>(
            """{"configuration":{"historyLength":2},"message":{"parts":[{"text":"hi"}],"role":"ROLE_USER","messageId":"m"}}""");
        var otherText = A2AJson.Deserialize<SendMessageRequest>(
            """{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"ho"}]},"configuration":{"historyLength":2}}""");
        var otherConfig = A2AJson.Deserialize<SendMessageRequest>(
            """{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"hi"}]},"configuration":{"historyLength":3}}""");

        Assert.Equal(RequestHasher.Hash(a), RequestHasher.Hash(reordered));
        Assert.NotEqual(RequestHasher.Hash(a), RequestHasher.Hash(otherText));
        Assert.NotEqual(RequestHasher.Hash(a), RequestHasher.Hash(otherConfig));
    }

    // ---------------------------------------------------------------- helpers

    private static string Body(string text, string messageId, bool returnImmediately = false)
    {
        var configuration = returnImmediately ? ""","configuration":{"returnImmediately":true}""" : "";
        return $$$"""{"message":{"messageId":"{{{messageId}}}","role":"ROLE_USER","parts":[{"text":"{{{text}}}"}]}{{{configuration}}}}""";
    }

    private static HttpRequestMessage Send(string binding, string paramsJson, bool streaming = false, string? idempotencyKey = null, string user = "alice")
    {
        var rpc = binding == ProtocolBindings.JsonRpc;
        var method = streaming ? A2AProtocol.Methods.SendStreamingMessage : A2AProtocol.Methods.SendMessage;
        var req = new HttpRequestMessage(HttpMethod.Post, rpc ? "/a2a" : streaming ? "/a2a/message:stream" : "/a2a/message:send")
        {
            Content = new StringContent(
                rpc ? $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{paramsJson}}}""" : paramsJson,
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add("X-Test-User", user);
        if (idempotencyKey is not null) req.Headers.Add("Idempotency-Key", idempotencyKey);
        return req;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(TestAgentHost host, HttpRequestMessage req)
    {
        using var client = host.Http();
        using var cts = new CancellationTokenSource(Timeout);
        using var resp = await client.SendAsync(req, cts.Token);
        var text = await resp.Content.ReadAsStringAsync(cts.Token);
        return (resp.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<SendMessageResponse> SendResponseAsync(TestAgentHost host, string binding, string body, string? idempotencyKey, string user)
    {
        var (status, json) = await SendAsync(host, Send(binding, body, idempotencyKey: idempotencyKey, user: user));
        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {json.GetRawText()}");
        if (json.TryGetProperty("error", out var error)) Assert.Fail(error.GetRawText());
        return A2AJson.Deserialize<SendMessageResponse>(binding == ProtocolBindings.JsonRpc ? json.GetProperty("result") : json);
    }

    private static async Task<AgentTask> SendTaskAsync(TestAgentHost host, string binding, string body, string? idempotencyKey = null, string user = "alice") =>
        (await SendResponseAsync(host, binding, body, idempotencyKey, user)).RequireTask();

    private static async Task<Message> SendMessageAsync(TestAgentHost host, string binding, string body) =>
        (await SendResponseAsync(host, binding, body, idempotencyKey: null, user: "alice")).RequireMessage();

    /// <summary>Every SSE frame of a stream, as its <c>id:</c> field and raw data.</summary>
    private static async Task<List<(string? Id, string Data)>> ReadStreamAsync(TestAgentHost host, HttpRequestMessage req)
    {
        using var client = host.Http();
        using var cts = new CancellationTokenSource(Timeout);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.True(resp.Content.Headers.ContentType?.MediaType == "text/event-stream",
            $"not an event stream: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(cts.Token)}");
        var frames = new List<(string? Id, string Data)>();
        await foreach (var e in SseReader.ReadAsync(await resp.Content.ReadAsStreamAsync(cts.Token), cts.Token)) frames.Add((e.Id, e.Data));
        return frames;
    }

    private static void AssertMismatch(HttpStatusCode status, JsonElement body)
    {
        Assert.Equal((HttpStatusCode)422, status);
        Assert.Contains(A2AErrors.IdempotencyKeyMismatchReason, body.GetProperty("error").GetRawText());
    }
}
