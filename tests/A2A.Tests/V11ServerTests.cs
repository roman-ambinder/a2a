using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A.Json;
using A2A.Server;

namespace A2A.Tests;

/// <summary>
/// The server side of A2A 1.1 (release candidate): generation, timeline, message events and streams that stay open, for 1.1
/// callers; and the unchanged 1.0 wire for 1.0 callers.
/// </summary>
public sealed class V11ServerTests
{
    private static Task<TestAgentHost> StartAsync(Action<A2AServerOptions>? configure = null) =>
        TestAgentHost.StartAsync(o =>
        {
            o.EnableProtocolV11();
            configure?.Invoke(o);
        });

    private static async Task<JsonElement> RpcAsync(TestAgentHost host, string method, object @params, string version, string user = "alice")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, version);
        using var response = await host.Http(user).SendAsync(request);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        if (body.TryGetProperty("error", out var error)) throw new Xunit.Sdk.XunitException($"{method} failed: {error}");
        return body.GetProperty("result");
    }

    private static object Send(string text, string? taskId = null) => new
    {
        message = new { messageId = Guid.NewGuid().ToString(), role = "ROLE_USER", parts = new[] { new { text } }, taskId },
    };

    private static async Task<string> CreateAsync(TestAgentHost host, string text, string version = "1.1")
    {
        var result = await RpcAsync(host, "SendMessage", Send(text), version);
        return result.GetProperty("task").GetProperty("id").GetString()!;
    }

    private static Task<JsonElement> GetTaskAsync(TestAgentHost host, string id, string version, object? extra = null) =>
        RpcAsync(host, "GetTask", extra ?? new { id }, version);

    private static long Gen(JsonElement e) => long.Parse(e.GetProperty("generation").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Reads the SSE frames of a JSON-RPC stream as the <c>result</c> of each frame.</summary>
    private static async IAsyncEnumerable<JsonElement> FramesAsync(HttpResponseMessage response)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            yield return JsonDocument.Parse(line["data:".Length..]).RootElement.GetProperty("result").Clone();
        }
    }

    private static async Task<HttpResponseMessage> OpenStreamAsync(TestAgentHost host, string method, object @params, string version)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, version);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.EventStreamMediaType));
        var response = await host.Http("alice").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        return response;
    }

    [Fact]
    public async Task Generation_counts_every_change_and_the_timeline_orders_the_interaction()
    {
        await using var host = await StartAsync();
        var id = await CreateAsync(host, "task");
        await host.WaitIdleAsync();

        var task = await GetTaskAsync(host, id, "1.1");
        // snapshot (1), WORKING (2), artifact (3), last chunk (4), COMPLETED (5)
        Assert.Equal(5, Gen(task));
        var timeline = task.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Equal(3, timeline.Count);
        Assert.Equal("task", timeline[0].GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(1, Gen(timeline[0]));
        Assert.Equal("TASK_STATE_WORKING", timeline[1].GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(2, Gen(timeline[1]));
        Assert.Equal("TASK_STATE_COMPLETED", timeline[2].GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(5, Gen(timeline[2]));
        var artifact = task.GetProperty("artifacts")[0];
        Assert.Equal("3", artifact.GetProperty("startGeneration").GetString());
        Assert.Equal("4", artifact.GetProperty("endGeneration").GetString());
    }

    [Fact]
    public async Task A_1_0_caller_gets_the_1_0_shape()
    {
        await using var host = await StartAsync();
        var id = await CreateAsync(host, "task", version: "1.0");
        await host.WaitIdleAsync();

        var raw = (await GetTaskAsync(host, id, "1.0")).GetRawText();
        Assert.DoesNotContain("generation", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timeline", raw, StringComparison.Ordinal);

        using var rest = new HttpRequestMessage(HttpMethod.Get, $"/a2a/tasks/{id}");
        rest.Headers.Add(A2AProtocol.Headers.Version, "1.0");
        using var response = await host.Http("alice").SendAsync(rest);
        Assert.DoesNotContain("generation", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_follow_up_is_a_timeline_entry_and_a_change()
    {
        await using var host = await StartAsync();
        var id = await CreateAsync(host, "input");
        await host.WaitIdleAsync();
        Assert.Equal(2, Gen(await GetTaskAsync(host, id, "1.1"))); // snapshot, INPUT_REQUIRED

        var reply = await RpcAsync(host, "SendMessage", Send("more", id), "1.1");
        Assert.Equal("TASK_STATE_COMPLETED", reply.GetProperty("task").GetProperty("status").GetProperty("state").GetString());
        await host.WaitIdleAsync();

        var task = await GetTaskAsync(host, id, "1.1");
        Assert.Equal(4, Gen(task)); // + the message (3) + COMPLETED (4)
        var timeline = task.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Equal(4, timeline.Count);
        Assert.Equal("more", timeline[2].GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(3, Gen(timeline[2]));
        // The history is still filled for 1.0 readers.
        Assert.Equal(4, task.GetProperty("history").GetArrayLength());
    }

    [Fact]
    public async Task Timeline_length_keeps_the_newest_entries()
    {
        await using var host = await StartAsync();
        var id = await CreateAsync(host, "task");
        await host.WaitIdleAsync();

        var two = await GetTaskAsync(host, id, "1.1", new { id, timelineLength = 2 });
        var timeline = two.GetProperty("timeline").EnumerateArray().ToList();
        Assert.Equal(2, timeline.Count);
        Assert.Equal(5, Gen(timeline[1]));
        Assert.False((await GetTaskAsync(host, id, "1.1", new { id, timelineLength = 0 })).TryGetProperty("timeline", out _));
        Assert.Equal(5, Gen(two)); // the generation is not cut
    }

    [Fact]
    public async Task Subscribers_see_a_follow_up_message_only_on_1_1()
    {
        await using var host = await StartAsync();
        var id = await CreateAsync(host, "input");
        await host.WaitIdleAsync();

        using var v11 = await OpenStreamAsync(host, "SubscribeToTask", new { id }, "1.1");
        using var v10 = await OpenStreamAsync(host, "SubscribeToTask", new { id }, "1.0");
        var v11Frames = FramesAsync(v11).GetAsyncEnumerator();
        var v10Frames = FramesAsync(v10).GetAsyncEnumerator();
        Assert.True(await v11Frames.MoveNextAsync()); // the task snapshot
        Assert.True(await v10Frames.MoveNextAsync());
        Assert.Equal(2, Gen(v11Frames.Current.GetProperty("task")));

        await RpcAsync(host, "SendMessage", Send("more", id), "1.1");

        Assert.True(await v11Frames.MoveNextAsync());
        var update = v11Frames.Current.GetProperty("messageUpdate");
        Assert.Equal("more", update.GetProperty("message").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(3, Gen(update));
        Assert.True(await v11Frames.MoveNextAsync());
        Assert.Equal(4, Gen(v11Frames.Current.GetProperty("statusUpdate")));

        Assert.True(await v10Frames.MoveNextAsync());
        var status = v10Frames.Current.GetProperty("statusUpdate"); // no message update in between
        Assert.Equal("TASK_STATE_COMPLETED", status.GetProperty("status").GetProperty("state").GetString());
        Assert.False(status.TryGetProperty("generation", out _));
    }

    [Fact]
    public async Task A_1_1_stream_stays_open_on_input_required_until_the_task_ends()
    {
        await using var host = await StartAsync();
        using var stream = await OpenStreamAsync(host, "SendStreamingMessage", Send("input"), "1.1");
        var frames = FramesAsync(stream).GetAsyncEnumerator();
        Assert.True(await frames.MoveNextAsync());
        var id = frames.Current.GetProperty("task").GetProperty("id").GetString()!;
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", frames.Current.GetProperty("statusUpdate").GetProperty("status").GetProperty("state").GetString());

        await host.WaitIdleAsync();
        await RpcAsync(host, "SendMessage", Send("more", id), "1.1");

        // The same stream carries the client's message and the outcome, then ends.
        Assert.True(await frames.MoveNextAsync());
        Assert.True(frames.Current.TryGetProperty("messageUpdate", out _));
        Assert.True(await frames.MoveNextAsync());
        Assert.Equal("TASK_STATE_COMPLETED", frames.Current.GetProperty("statusUpdate").GetProperty("status").GetProperty("state").GetString());
        Assert.False(await frames.MoveNextAsync());
    }

    [Fact]
    public async Task A_1_0_stream_still_ends_on_input_required()
    {
        await using var host = await StartAsync();
        using var stream = await OpenStreamAsync(host, "SendStreamingMessage", Send("input"), "1.0");
        var frames = new List<JsonElement>();
        await foreach (var f in FramesAsync(stream)) frames.Add(f);
        Assert.Equal(2, frames.Count);
        Assert.Equal("TASK_STATE_INPUT_REQUIRED", frames[1].GetProperty("statusUpdate").GetProperty("status").GetProperty("state").GetString());
    }

    [Fact]
    public async Task Stream_events_of_a_1_1_caller_carry_their_generation()
    {
        await using var host = await StartAsync();
        using var stream = await OpenStreamAsync(host, "SendStreamingMessage", Send("task"), "1.1");
        var generations = new List<long>();
        await foreach (var f in FramesAsync(stream))
        {
            var payload = f.EnumerateObject().Single().Value;
            generations.Add(Gen(payload));
        }
        Assert.Equal([1, 2, 3, 4, 5], generations);
    }

    [Fact]
    public async Task Version_1_1_is_refused_unless_enabled()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/a2a")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "GetTask", @params = new { id = "x" } }), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, "1.1");
        using var response = await host.Http("alice").SendAsync(request);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal((int)A2AErrorCode.VersionNotSupported, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_completed_card_lists_the_1_1_interfaces_first()
    {
        await using var host = await StartAsync(o => o.AgentCard.SupportedInterfaces = []);
        using var response = await host.Http().GetAsync(A2AProtocol.WellKnownAgentCardPath);
        var card = A2AJson.Deserialize<AgentCard>(await response.Content.ReadAsStringAsync());
        Assert.Equal(4, card.SupportedInterfaces.Count);
        Assert.All(card.SupportedInterfaces.Take(2), i => Assert.Equal(A2AProtocol.Version11, i.ProtocolVersion));
        Assert.All(card.SupportedInterfaces.Skip(2), i => Assert.Equal(A2AProtocol.Version, i.ProtocolVersion));
    }

    [Fact]
    public async Task Negative_timeline_length_is_invalid()
    {
        await using var host = await StartAsync();
        var id = await CreateAsync(host, "task");
        var ex = await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => GetTaskAsync(host, id, "1.1", new { id, timelineLength = -1 }));
        Assert.Contains("timelineLength", ex.Message, StringComparison.Ordinal);
    }
}
