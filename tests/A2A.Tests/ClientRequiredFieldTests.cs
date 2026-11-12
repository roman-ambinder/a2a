using A2A.Client;
using A2A.Client.Transports;

namespace A2A.Tests;

/// <summary>The client refuses a request with a missing required field before it reaches the transport.</summary>
public class ClientRequiredFieldTests
{
    private static readonly Uri Endpoint = new("http://localhost/a2a");

    private static (A2AClient Client, RecordingHandler Handler) Make(string binding)
    {
        var handler = new RecordingHandler();
        var http = new HttpClient(handler);
        IA2ATransport transport = binding == "jsonrpc" ? new JsonRpcTransport(http, Endpoint) : new HttpJsonTransport(http, Endpoint);
        return (new A2AClient(transport), handler);
    }

    private static async Task Rejects(RecordingHandler handler, Func<Task> act, string message)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(act);
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(message, ex.Message);
        Assert.Empty(handler.Calls);
    }

    private static Message UnspecifiedRole() => new() { MessageId = "m", Role = Role.Unspecified, Parts = [Part.FromText("a")] };

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public async Task A_dot_segment_id_or_tenant_is_refused_before_sending_over_http_json(string dots)
    {
        // System.Uri removes dot segments (also escaped as %2E%2E), so the request would reach another path of the same
        // origin, credentials and all.
        var (client, h) = Make("rest");

        await Rejects(h, () => client.GetTaskAsync(dots), "A path segment cannot be '.' or '..'.");
        await Rejects(h, () => client.CancelTaskAsync(dots), "A path segment cannot be '.' or '..'.");
        await Rejects(h, () => client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "t1", Id = dots }),
            "A path segment cannot be '.' or '..'.");
        await Rejects(h, () => client.GetTaskAsync(new GetTaskRequest { Id = "t1", Tenant = dots }), "A path segment cannot be '.' or '..'.");
    }

    [Theory]
    [InlineData("jsonrpc")]
    [InlineData("rest")]
    public async Task Missing_required_field_fails_before_sending(string binding)
    {
        var (client, h) = Make(binding);

        await Rejects(h, () => client.GetTaskAsync(""), "id is required");
        await Rejects(h, () => client.CancelTaskAsync(""), "id is required");
        await Rejects(h, async () => client.SubscribeToTaskAsync(""), "id is required");
        await Rejects(h, async () => client.ObserveTaskAsync(""), "id is required");
        await Rejects(h, () => client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = "t1", Url = "" }), "config.url is required");
        await Rejects(h, () => client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = "", Id = "c" }), "taskId is required");
        await Rejects(h, () => client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = "", Id = "c" }), "taskId is required");
        await Rejects(h, () => client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = "" }), "taskId is required");
        await Rejects(h, () => client.SendMessageAsync(UnspecifiedRole()), "message.role is required");
    }

    [Theory]
    [InlineData("jsonrpc")]
    [InlineData("rest")]
    public async Task Null_request_is_ArgumentNullException(string binding)
    {
        var (client, h) = Make(binding);

        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetTaskAsync((GetTaskRequest)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CancelTaskAsync((CancelTaskRequest)null!));
        Assert.Throws<ArgumentNullException>(() => client.SubscribeToTaskAsync((SubscribeToTaskRequest)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.CreateTaskPushNotificationConfigAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetTaskPushNotificationConfigAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ListTaskPushNotificationConfigsAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.DeleteTaskPushNotificationConfigAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.ListTasksAsync((ListTasksRequest)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => client.GetExtendedAgentCardAsync((GetExtendedAgentCardRequest)null!));
        Assert.Empty(h.Calls);
    }

    [Fact]
    public async Task Whitespace_id_is_refused()
    {
        var (client, h) = Make("jsonrpc");
        await Rejects(h, () => client.GetTaskAsync("  "), "id is required");
    }

    [Theory]
    [InlineData("jsonrpc")]
    [InlineData("rest")]
    public async Task Valid_request_reaches_the_transport(string binding)
    {
        var (client, h) = Make(binding);
        try { await client.GetTaskAsync("t1"); } catch (Exception) { /* the canned response may not parse */ }
        Assert.Single(h.Calls);
    }
}
