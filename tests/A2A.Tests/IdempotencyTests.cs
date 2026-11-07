using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client.Sse;
using A2A.Json;
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
            Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(scope, HashX));
            await store.CompleteAsync(scope, new IdempotencyOutcome.TaskCreated(scope.Key));
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
        Assert.IsType<IdempotencyResult.New>(await store.TryBeginAsync(ScopeA, HashX));
        var waiting = Assert.IsType<IdempotencyResult.InProgress>(await store.TryBeginAsync(ScopeA, HashX));
        Assert.IsType<IdempotencyResult.Mismatch>(await store.TryBeginAsync(ScopeA, [9]));

        await store.AbandonAsync(ScopeA);

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
