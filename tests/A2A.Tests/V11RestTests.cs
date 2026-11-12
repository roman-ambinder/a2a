using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Server;

namespace A2A.Tests;

/// <summary>
/// The A2A 1.1 (release candidate) request fields on the HTTP+JSON binding: a POST subscribe carries
/// <c>lastSeenGeneration</c> in its body and a GET in its query (§11.5), and the SDK client sends every GET field
/// (<c>timelineLength</c>, <c>currentGeneration</c>, <c>lastSeenGeneration</c>) in the query.
/// </summary>
public sealed class V11RestTests
{
    private static Task<TestAgentHost> StartAsync(Action<A2AServerOptions>? configure = null) =>
        TestAgentHost.StartAsync(o =>
        {
            o.EnableProtocolV11();
            configure?.Invoke(o);
        });

    private static A2AClient RestClient(TestAgentHost host, HttpMethod? subscribeMethod = null) =>
        host.Client(ProtocolBindings.HttpJson, options: new A2AClientOptions
        {
            ProtocolVersion = A2AProtocol.Version11,
            RestSubscribeMethod = subscribeMethod ?? HttpMethod.Post,
        });

    /// <summary>A completed "task" scenario: generation 5, with an artifact chunk at 4 and the final status at 5.</summary>
    private static async Task<string> CompletedTaskAsync(TestAgentHost host)
    {
        var task = (await RestClient(host).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();
        return task.Id;
    }

    /// <summary>The SSE frames of an HTTP+JSON stream, each a <c>StreamResponse</c>.</summary>
    private static async Task<List<JsonElement>> FramesAsync(HttpResponseMessage response)
    {
        var frames = new List<JsonElement>();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        while (await reader.ReadLineAsync() is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal))
                frames.Add(JsonDocument.Parse(line["data:".Length..]).RootElement.Clone());
        return frames;
    }

    private static long Gen(JsonElement e) =>
        long.Parse(e.GetProperty("generation").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

    private static void AssertResumedAfterGeneration3(List<JsonElement> frames)
    {
        Assert.Equal(2, frames.Count);
        Assert.False(frames[0].TryGetProperty("task", out _)); // no snapshot first
        Assert.Equal(4, Gen(frames[0].GetProperty("artifactUpdate")));
        Assert.Equal(5, Gen(frames[1].GetProperty("statusUpdate")));
    }

    [Fact]
    public async Task A_POST_subscribe_reads_lastSeenGeneration_from_its_body()
    {
        await using var host = await StartAsync();
        var id = await CompletedTaskAsync(host);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/a2a/tasks/{id}:subscribe")
        {
            Content = new StringContent("""{"lastSeenGeneration":"3"}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version11);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.EventStreamMediaType));
        using var response = await host.Http("alice").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        AssertResumedAfterGeneration3(await FramesAsync(response));
    }

    [Fact]
    public async Task A_GET_subscribe_reads_lastSeenGeneration_from_its_query()
    {
        await using var host = await StartAsync();
        var id = await CompletedTaskAsync(host);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/a2a/tasks/{id}:subscribe?lastSeenGeneration=3");
        request.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version11);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.EventStreamMediaType));
        using var response = await host.Http("alice").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        AssertResumedAfterGeneration3(await FramesAsync(response));
    }

    [Fact]
    public async Task A_POST_subscribe_with_an_empty_body_still_opens_with_a_snapshot()
    {
        await using var host = await StartAsync();
        var task = (await RestClient(host).SendMessageAsync("input")).RequireTask(); // INPUT_REQUIRED: the stream stays open
        await host.WaitIdleAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/a2a/tasks/{task.Id}:subscribe");
        request.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version11);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(A2AProtocol.EventStreamMediaType));
        using var response = await host.Http("alice").SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.IsSuccessStatusCode, await ErrorAsync(response));

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        string? line;
        do line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        while (line is not null && !line.StartsWith("data:", StringComparison.Ordinal));
        Assert.NotNull(line);
        Assert.True(JsonDocument.Parse(line!["data:".Length..]).RootElement.TryGetProperty("task", out _));
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync();

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task The_client_resumes_after_lastSeenGeneration_over_HTTP_JSON(string method)
    {
        await using var host = await StartAsync();
        var id = await CompletedTaskAsync(host);
        var client = RestClient(host, new HttpMethod(method));

        var events = new List<StreamResponse>();
        await foreach (var e in client.SubscribeToTaskAsync(new SubscribeToTaskRequest { Id = id, LastSeenGeneration = 3 }))
            events.Add(e);

        Assert.DoesNotContain(events, e => e is TaskEvent);
        Assert.Equal((long?)4, Assert.IsType<ArtifactUpdateEvent>(events[0]).ArtifactUpdate.Generation);
        Assert.Equal((long?)5, Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate.Generation);
    }

    [Fact]
    public async Task The_client_sends_timelineLength_on_GetTask_and_ListTasks()
    {
        await using var host = await StartAsync();
        var id = await CompletedTaskAsync(host);
        var client = RestClient(host);

        var all = await client.GetTaskAsync(new GetTaskRequest { Id = id });
        Assert.True(all.Timeline!.Count > 1);
        var one = await client.GetTaskAsync(new GetTaskRequest { Id = id, TimelineLength = 1 });
        Assert.Equal(5L, Assert.Single(one.Timeline!).Generation);

        var listed = await client.ListTasksAsync(new ListTasksRequest { TimelineLength = 1 });
        Assert.Single(Assert.Single(listed.Tasks, t => t.Id == id).Timeline!);
    }

    [Fact]
    public async Task The_client_sends_currentGeneration_so_GetTask_long_polls()
    {
        await using var host = await StartAsync(o => o.LongPollTimeout = TimeSpan.FromMilliseconds(300));
        var task = (await RestClient(host).SendMessageAsync("input")).RequireTask();
        await host.WaitIdleAsync();
        var generation = (await RestClient(host).GetTaskAsync(new GetTaskRequest { Id = task.Id })).Generation!.Value;

        var started = DateTime.UtcNow;
        var polled = await RestClient(host).GetTaskAsync(new GetTaskRequest { Id = task.Id, CurrentGeneration = generation })
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(generation, polled.Generation);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(250)); // it waited: the field reached the server
    }
}
