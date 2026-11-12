using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A.Grpc;
using A2A.Server.AspNetCore;
using Grpc.Core;
using A2aPb = Lf.A2a.V1;

namespace A2A.Tests;

/// <summary>A2A 1.1 SendLiveMessage (§3.1.12): the HTTP downlink and uplink calls, and the gRPC two-way stream.</summary>
public sealed class V11LiveTests
{
    private static Task<TestAgentHost> StartAsync(bool grpc = false) =>
        TestAgentHost.StartAsync(o => o.EnableProtocolV11(), grpc: grpc);

    private static object Send(string text, string? taskId = null, string? messageId = null) => new
    {
        sendMessage = new { message = new { messageId = messageId ?? Guid.NewGuid().ToString(), role = "ROLE_USER", parts = new[] { new { text } }, taskId } },
    };

    private static HttpRequestMessage Rpc(object @params, string version, bool downlink)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "SendLiveMessage", @params }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, version);
        if (downlink) request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.EventStreamMediaType));
        return request;
    }

    private static async IAsyncEnumerable<JsonElement> FramesAsync(HttpResponseMessage response)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            yield return JsonDocument.Parse(line["data:".Length..]).RootElement.GetProperty("result").Clone();
        }
    }

    private static async Task<JsonElement> UplinkAsync(TestAgentHost host, object @params, string user = "alice")
    {
        using var response = await host.Http(user).SendAsync(Rpc(@params, "1.1", downlink: false));
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string State(JsonElement statusUpdate) => statusUpdate.GetProperty("status").GetProperty("state").GetString()!;

    [Fact]
    public async Task Http_downlink_stays_open_and_takes_input_from_uplink_calls()
    {
        await using var host = await StartAsync();
        using var downlink = await host.Http("alice").SendAsync(Rpc(Send("input"), "1.1", downlink: true), HttpCompletionOption.ResponseHeadersRead);
        downlink.EnsureSuccessStatusCode();
        var frames = FramesAsync(downlink).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        var id = frames.Current.GetProperty("task").GetProperty("id").GetString()!;
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", State(frames.Current.GetProperty("statusUpdate")));

        // The uplink call gets only the task (an acknowledgement); the events come on the downlink.
        var ack = await UplinkAsync(host, Send("more", id));
        Assert.Equal(id, ack.GetProperty("result").GetProperty("id").GetString());

        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("more", frames.Current.GetProperty("messageUpdate").GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("TASK_STATE_COMPLETED", State(frames.Current.GetProperty("statusUpdate")));
        Assert.False(await frames.MoveNextAsync());
    }

    [Fact]
    public async Task A_refused_uplink_call_fails_and_shows_on_the_downlink()
    {
        await using var host = await StartAsync();
        using var downlink = await host.Http("alice").SendAsync(Rpc(Send("input"), "1.1", downlink: true), HttpCompletionOption.ResponseHeadersRead);
        var frames = FramesAsync(downlink).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        var id = frames.Current.GetProperty("task").GetProperty("id").GetString()!;
        Assert.True(await frames.MoveNextAsync());

        // A message without parts: refused on the uplink call and reported on the downlink, which stays open.
        var bad = new { sendMessage = new { message = new { messageId = "m-bad", role = "ROLE_USER", parts = Array.Empty<object>(), taskId = id } } };
        var error = await UplinkAsync(host, bad);
        Assert.Equal((int)A2AErrorCode.InvalidParams, error.GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(await frames.MoveNextAsync());
        var refused = frames.Current.GetProperty("error");
        Assert.Equal("INVALID_ARGUMENT", refused.GetProperty("code").GetString());
        Assert.Equal("m-bad", refused.GetProperty("referenceId").GetString());

        await UplinkAsync(host, Send("more", id));
        Assert.True(await frames.MoveNextAsync()); // the message update
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("TASK_STATE_COMPLETED", State(frames.Current.GetProperty("statusUpdate")));
    }

    [Fact]
    public async Task A_refused_uplink_call_of_another_caller_does_not_reach_the_owners_downlinks()
    {
        await using var host = await StartAsync();
        using var downlink = await host.Http("alice").SendAsync(Rpc(Send("input"), "1.1", downlink: true), HttpCompletionOption.ResponseHeadersRead);
        var frames = FramesAsync(downlink).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        var id = frames.Current.GetProperty("task").GetProperty("id").GetString()!;
        Assert.True(await frames.MoveNextAsync());
        // A second downlink of the owner on the same task (opened by subscribing).
        using var second = await host.Http("alice").SendAsync(Rpc(new { subscribe = new { id } }, "1.1", downlink: true), HttpCompletionOption.ResponseHeadersRead);
        var secondFrames = FramesAsync(second).GetAsyncEnumerator();
        Assert.True(await secondFrames.MoveNextAsync());
        Assert.Equal(id, secondFrames.Current.GetProperty("task").GetProperty("id").GetString());

        // Bob knows the task id but does not own the task: his call fails, and nothing of it reaches Alice's streams.
        var foreign = await UplinkAsync(host, Send("x", id, "m-bob"), user: "bob");
        Assert.Equal((int)A2AErrorCode.TaskNotFound, foreign.GetProperty("error").GetProperty("code").GetInt32());

        // The owner's own refusal still reaches each of her downlinks, and it is the next frame on both.
        var bad = new { sendMessage = new { message = new { messageId = "m-bad", role = "ROLE_USER", parts = Array.Empty<object>(), taskId = id } } };
        await UplinkAsync(host, bad);
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("m-bad", frames.Current.GetProperty("error").GetProperty("referenceId").GetString());
        Assert.True(await secondFrames.MoveNextAsync());
        Assert.Equal("m-bad", secondFrames.Current.GetProperty("error").GetProperty("referenceId").GetString());
    }

    [Fact]
    public async Task An_uplink_call_must_name_its_task_and_cannot_subscribe()
    {
        await using var host = await StartAsync();
        var unnamed = await UplinkAsync(host, Send("more"));
        Assert.Equal((int)A2AErrorCode.InvalidParams, unnamed.GetProperty("error").GetProperty("code").GetInt32());
        var subscribe = await UplinkAsync(host, new { subscribe = new { id = "t" } });
        Assert.Equal((int)A2AErrorCode.InvalidParams, subscribe.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_1_0_caller_cannot_open_a_live_exchange()
    {
        await using var host = await StartAsync();
        using var response = await host.Http("alice").SendAsync(Rpc(Send("input"), "1.0", downlink: false));
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal((int)A2AErrorCode.MethodNotFound, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Rest_live_route_serves_the_downlink()
    {
        await using var host = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a/message:live")
        {
            Content = new StringContent(JsonSerializer.Serialize(Send("task")), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, "1.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.EventStreamMediaType));
        using var response = await host.Http("alice").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("TASK_STATE_COMPLETED", body, StringComparison.Ordinal);
    }

    private static A2aPb.StreamRequest Grpc(object request) => ProtoMapper.ToProto<A2aPb.StreamRequest, StreamRequest>(
        A2A.Json.A2AJson.Deserialize<StreamRequest>(JsonSerializer.Serialize(request)));

    [Fact]
    public async Task Grpc_live_stream_binds_a_task_takes_input_and_refuses_what_it_cannot_take()
    {
        await using var host = await StartAsync(grpc: true);
        var client = new A2aPb.A2AService.A2AServiceClient(host.GrpcChannel("alice"));
        using var call = client.SendLiveMessage(new Metadata { { "a2a-version", "1.1" } });
        await call.RequestStream.WriteAsync(Grpc(Send("input")));
        var events = call.ResponseStream;

        Assert.True(await events.MoveNext());
        var id = events.Current.Task.Id;
        Assert.Equal(1, events.Current.Task.Generation);
        Assert.True(await events.MoveNext());
        Assert.Equal(A2aPb.TaskState.InputRequired, events.Current.StatusUpdate.Status.State);

        // A late subscribe and a message for another task: refused one by one, the stream stays open.
        await call.RequestStream.WriteAsync(Grpc(new { subscribe = new { id } }));
        Assert.True(await events.MoveNext());
        Assert.Equal("INVALID_ARGUMENT", events.Current.Error.Code);
        await call.RequestStream.WriteAsync(Grpc(Send("x", "other-task", "m-other")));
        Assert.True(await events.MoveNext());
        Assert.Equal("m-other", events.Current.Error.ReferenceId);

        // A message for the bound task (its id may be left out) continues it.
        await call.RequestStream.WriteAsync(Grpc(Send("more")));
        Assert.True(await events.MoveNext());
        Assert.Equal("more", events.Current.MessageUpdate.Message.Parts[0].Text);
        Assert.True(await events.MoveNext());
        Assert.Equal(A2aPb.TaskState.Completed, events.Current.StatusUpdate.Status.State);
        Assert.Equal(4, events.Current.StatusUpdate.Generation);
        Assert.Equal(id, events.Current.StatusUpdate.TaskId);
        Assert.False(await events.MoveNext()); // the task ended, so did the stream
    }

    [Fact]
    public async Task Grpc_live_stream_can_open_by_subscribing()
    {
        await using var host = await StartAsync(grpc: true);
        var created = await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync(Message.FromUser("input"));
        var id = created.RequireTask().Id;
        await host.WaitIdleAsync();

        var client = new A2aPb.A2AService.A2AServiceClient(host.GrpcChannel("alice"));
        using var call = client.SendLiveMessage(new Metadata { { "a2a-version", "1.1" } });
        await call.RequestStream.WriteAsync(Grpc(new { subscribe = new { id, lastSeenGeneration = "1" } }));
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(A2aPb.TaskState.InputRequired, call.ResponseStream.Current.StatusUpdate.Status.State); // no snapshot
        await call.RequestStream.WriteAsync(Grpc(Send("more")));
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.True(await call.ResponseStream.MoveNext());
        Assert.Equal(A2aPb.TaskState.Completed, call.ResponseStream.Current.StatusUpdate.Status.State);
    }

    [Fact]
    public async Task A_running_agent_that_reads_live_input_takes_messages_while_it_works()
    {
        await using var host = await StartAsync(grpc: true);
        var client = new A2aPb.A2AService.A2AServiceClient(host.GrpcChannel("alice"));
        using var call = client.SendLiveMessage(new Metadata { { "a2a-version", "1.1" } });
        await call.RequestStream.WriteAsync(Grpc(Send("listen")));
        var events = call.ResponseStream;
        Assert.True(await events.MoveNext()); // the task
        Assert.True(await events.MoveNext());
        Assert.Equal(A2aPb.TaskState.Working, events.Current.StatusUpdate.Status.State);

        // WORKING, and the default follow-up rule refuses messages while working: the running agent takes them instead.
        await call.RequestStream.WriteAsync(Grpc(Send("one")));
        Assert.True(await events.MoveNext());
        Assert.Equal("one", events.Current.MessageUpdate.Message.Parts[0].Text);
        Assert.True(await events.MoveNext());
        Assert.Equal("heard: one +0", events.Current.StatusUpdate.Status.Message.Parts[0].Text);

        await call.RequestStream.WriteAsync(Grpc(Send("done")));
        Assert.True(await events.MoveNext()); // the message update
        Assert.True(await events.MoveNext());
        Assert.Equal(A2aPb.TaskState.Completed, events.Current.StatusUpdate.Status.State);
        Assert.False(await events.MoveNext());
    }

    [Fact]
    public async Task Without_a_listening_agent_a_message_to_a_working_task_is_refused_on_the_stream()
    {
        await using var host = await StartAsync(grpc: true);
        var client = new A2aPb.A2AService.A2AServiceClient(host.GrpcChannel("alice"));
        using var call = client.SendLiveMessage(new Metadata { { "a2a-version", "1.1" } });
        await call.RequestStream.WriteAsync(Grpc(Send("gate live-refused")));
        var events = call.ResponseStream;
        Assert.True(await events.MoveNext());
        Assert.True(await events.MoveNext()); // WORKING, at the gate
        await call.RequestStream.WriteAsync(Grpc(Send("hello?", messageId: "m-busy")));
        Assert.True(await events.MoveNext());
        Assert.Equal("m-busy", events.Current.Error.ReferenceId);
        Assert.Equal("FAILED_PRECONDITION", events.Current.Error.Code);
        ScenarioAgent.Gate("gate live-refused").TrySetResult();
        Assert.True(await events.MoveNext());
        Assert.Equal(A2aPb.TaskState.Completed, events.Current.StatusUpdate.Status.State);
    }

    [Fact]
    public async Task Each_later_request_of_a_grpc_live_stream_takes_a_token_from_the_callers_rate_limit()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.EnableProtocolV11(), grpc: true,
            services: s => s.AddA2ARateLimiting(o =>
            {
                o.TokenLimit = 3;
                o.TokensPerPeriod = 1;
                o.ReplenishmentPeriod = TimeSpan.FromMinutes(10);
            }));
        var client = new A2aPb.A2AService.A2AServiceClient(host.GrpcChannel("alice"));
        using var call = client.SendLiveMessage(new Metadata { { "a2a-version", "1.1" } });
        await call.RequestStream.WriteAsync(Grpc(Send("listen"))); // the call itself: one token
        var events = call.ResponseStream;
        Assert.True(await events.MoveNext()); // the task
        Assert.True(await events.MoveNext()); // WORKING

        // Two more tokens: two later requests are taken.
        foreach (var text in new[] { "one", "two" })
        {
            await call.RequestStream.WriteAsync(Grpc(Send(text)));
            Assert.True(await events.MoveNext());
            Assert.Equal(text, events.Current.MessageUpdate.Message.Parts[0].Text);
            Assert.True(await events.MoveNext());
            Assert.StartsWith("heard: " + text, events.Current.StatusUpdate.Status.Message.Parts[0].Text, StringComparison.Ordinal);
        }

        // The bucket is empty: the next request is refused as a unary call would be, and the stream stays open.
        await call.RequestStream.WriteAsync(Grpc(Send("three", messageId: "m-three")));
        Assert.True(await events.MoveNext());
        Assert.Equal("RESOURCE_EXHAUSTED", events.Current.Error.Code);
        Assert.Equal("m-three", events.Current.Error.ReferenceId);

        // The stream and the unary calls draw from the same bucket.
        var unary = await Assert.ThrowsAsync<RpcException>(() => client.GetTaskAsync(new A2aPb.GetTaskRequest { Id = "t" }).ResponseAsync);
        Assert.Equal(StatusCode.ResourceExhausted, unary.StatusCode);
        // Another caller has its own bucket.
        var bob = new A2aPb.A2AService.A2AServiceClient(host.GrpcChannel("bob"));
        var other = await Assert.ThrowsAsync<RpcException>(() => bob.GetTaskAsync(new A2aPb.GetTaskRequest { Id = "t" }).ResponseAsync);
        Assert.NotEqual(StatusCode.ResourceExhausted, other.StatusCode);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    [InlineData(ProtocolBindings.Grpc)]
    public async Task Client_opens_a_live_exchange_on_every_binding(string binding)
    {
        await using var host = await StartAsync(grpc: binding == ProtocolBindings.Grpc);
        var client = host.Client(binding);
        await using var live = await client.OpenLiveAsync(Message.FromUser("input"));
        var events = live.Events.GetAsyncEnumerator();

        Assert.True(await events.MoveNextAsync());
        var task = Assert.IsType<TaskEvent>(events.Current).Task;
        Assert.Equal(1, task.Generation);
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(TaskState.InputRequired, Assert.IsType<StatusUpdateEvent>(events.Current).StatusUpdate.Status.State);

        // A message without parts is refused: a StreamError on the events (HTTP also fails the uplink call); the exchange stays open.
        var bad = new Message { MessageId = "m-bad", Role = Role.User, Parts = [] };
        if (binding == ProtocolBindings.Grpc) await live.SendAsync(bad);
        else await Assert.ThrowsAsync<A2AException>(() => live.SendAsync(bad));
        Assert.True(await events.MoveNextAsync());
        var refused = Assert.IsType<StreamErrorEvent>(events.Current).Error;
        Assert.Equal("INVALID_ARGUMENT", refused.Code);
        Assert.Equal("m-bad", refused.ReferenceId);

        await live.SendAsync(Message.FromUser("more")); // no task id: the exchange's task
        Assert.True(await events.MoveNextAsync());
        Assert.Equal("more", Assert.IsType<MessageUpdateEvent>(events.Current).MessageUpdate.Message.GetText());
        Assert.True(await events.MoveNextAsync());
        var done = Assert.IsType<StatusUpdateEvent>(events.Current).StatusUpdate;
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal(4, done.Generation);
        Assert.False(await events.MoveNextAsync());
        await events.DisposeAsync();
    }
}
