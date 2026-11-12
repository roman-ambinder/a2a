using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.V0_3Compat;
using A2A.Server.Push;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.V0_3Compat.Tests;

/// <summary>
/// v0.3 push notifications: the four <c>tasks/pushNotificationConfig/*</c> methods, their HTTP+JSON routes, a config in a
/// send, and the webhook body, which is the task as a v0.3 JSON-RPC <c>Task</c> (what the v0.3 Python SDK sends).
/// </summary>
public class ServerPushTests
{
    private const string Hook = "http://hooks.test/v03";

    private sealed class Webhooks : HttpMessageHandler
    {
        public ConcurrentQueue<(HttpRequestMessage Request, string Body)> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Enqueue((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static async Task<(V03Host Host, Webhooks Hooks)> StartAsync(bool compat = true)
    {
        var hooks = new Webhooks();
        var host = await V03Host.StartAsync(compat: compat, configure: o =>
            {
                o.PushNotifications.AllowInsecureUrls = true;
                o.PushNotifications.AllowPrivateNetworks = true;
                o.PushNotifications.CatchUpOnRegistration = false;
                o.PushDelivery.InitialBackoff = TimeSpan.FromMilliseconds(10);
                o.PushDelivery.PollInterval = TimeSpan.FromMilliseconds(20);
            },
            services: s => s.AddKeyedSingleton(PushDeliveryService.InvokerServiceKey, new HttpMessageInvoker(hooks, disposeHandler: false)));
        return (host, hooks);
    }

    /// <summary>An INPUT_REQUIRED task: it changes only when a follow-up arrives.</summary>
    private static async Task<string> InputTaskAsync(V03Host host)
    {
        var (_, body) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("input"));
        return body["result"]!["id"]!.GetValue<string>();
    }

    private static async Task FinishAsync(V03Host host, string taskId)
    {
        var p = V03Host.SendParams("finished");
        p["message"]!["taskId"] = taskId;
        var (_, body) = await V03Host.RpcAsync(host.Http(), "message/send", p);
        Assert.Equal("completed", body["result"]!["status"]!["state"]!.GetValue<string>());
    }

    private static async Task<List<(HttpRequestMessage Request, string Body)>> WaitForAsync(Webhooks hooks, string url,
        Func<JsonNode, bool> until)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var calls = hooks.Calls.Where(c => c.Request.RequestUri!.AbsoluteUri == url).ToList();
            if (calls.Any(c => until(JsonNode.Parse(c.Body)!))) return calls;
            await Task.Delay(20);
        }

        throw new TimeoutException($"No matching webhook call to {url}; got {hooks.Calls.Count} calls.");
    }

    private static JsonObject Config(string taskId, string? id = null, string? token = null)
    {
        var inner = new JsonObject { ["url"] = Hook };
        if (id is not null) inner["id"] = id;
        if (token is not null) inner["token"] = token;
        return new JsonObject { ["taskId"] = taskId, ["pushNotificationConfig"] = inner };
    }

    [Fact]
    public async Task JsonRpc_set_get_list_and_delete_a_config()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var taskId = await InputTaskAsync(host);

        var (_, set) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/set", Config(taskId, "c1", "tok"));
        var created = set["result"]!;
        Assert.Equal(taskId, created["taskId"]!.GetValue<string>());
        Assert.Equal("c1", created["pushNotificationConfig"]!["id"]!.GetValue<string>());
        Assert.Equal(Hook, created["pushNotificationConfig"]!["url"]!.GetValue<string>());

        var (_, get) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/get",
            new JsonObject { ["id"] = taskId, ["pushNotificationConfigId"] = "c1" });
        Assert.Equal("c1", get["result"]!["pushNotificationConfig"]!["id"]!.GetValue<string>());
        // Without pushNotificationConfigId: the task's first config, as the v0.3 Python SDK answers.
        var (_, first) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/get", new JsonObject { ["id"] = taskId });
        Assert.Equal("c1", first["result"]!["pushNotificationConfig"]!["id"]!.GetValue<string>());

        await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/set", Config(taskId, "c2"));
        var (_, list) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/list", new JsonObject { ["id"] = taskId });
        Assert.Equal(["c1", "c2"], list["result"]!.AsArray().Select(c => c!["pushNotificationConfig"]!["id"]!.GetValue<string>()).Order());

        var (_, deleted) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/delete",
            new JsonObject { ["id"] = taskId, ["pushNotificationConfigId"] = "c1" });
        Assert.True(deleted.ContainsKey("result"));
        Assert.Null(deleted["result"]); // the schema says result: null
        var (_, after) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/list", new JsonObject { ["id"] = taskId });
        Assert.Equal("c2", Assert.Single(after["result"]!.AsArray())!["pushNotificationConfig"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task JsonRpc_delete_needs_a_config_id_and_another_callers_task_is_not_found()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var taskId = await InputTaskAsync(host);

        var (_, noId) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/delete", new JsonObject { ["id"] = taskId });
        Assert.Equal(-32602, noId["error"]!["code"]!.GetValue<int>());

        var (_, other) = await V03Host.RpcAsync(host.Http("bob"), "tasks/pushNotificationConfig/set", Config(taskId));
        Assert.Equal(-32001, other["error"]!["code"]!.GetValue<int>());
        var (_, none) = await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/get", new JsonObject { ["id"] = taskId });
        Assert.True(none.ContainsKey("error")); // no config yet
    }

    [Fact]
    public async Task Rest_create_get_list_and_delete_a_config()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var taskId = await InputTaskAsync(host);
        var http = host.Http();

        var body = new JsonObject
        {
            ["name"] = $"tasks/{taskId}/pushNotificationConfigs/r1",
            ["pushNotificationConfig"] = new JsonObject { ["url"] = Hook, ["token"] = "tok" },
        };
        using var create = await http.PostAsync($"/a2a/v1/tasks/{taskId}/pushNotificationConfigs", V03Host.Json(body.ToJsonString()));
        var created = JsonNode.Parse(await create.Content.ReadAsStringAsync())!;
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Equal($"tasks/{taskId}/pushNotificationConfigs/r1", created["name"]!.GetValue<string>());

        using var get = await http.GetAsync($"/a2a/v1/tasks/{taskId}/pushNotificationConfigs/r1");
        Assert.Equal(Hook, JsonNode.Parse(await get.Content.ReadAsStringAsync())!["pushNotificationConfig"]!["url"]!.GetValue<string>());

        using var list = await http.GetAsync($"/a2a/v1/tasks/{taskId}/pushNotificationConfigs");
        var configs = JsonNode.Parse(await list.Content.ReadAsStringAsync())!["configs"]!.AsArray();
        Assert.Equal($"tasks/{taskId}/pushNotificationConfigs/r1", Assert.Single(configs)!["name"]!.GetValue<string>());

        using var delete = await http.DeleteAsync($"/a2a/v1/tasks/{taskId}/pushNotificationConfigs/r1");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        using var gone = await http.GetAsync($"/a2a/v1/tasks/{taskId}/pushNotificationConfigs");
        Assert.Empty(JsonNode.Parse(await gone.Content.ReadAsStringAsync())!["configs"]!.AsArray());

        var wrongTask = new JsonObject
        {
            ["name"] = "tasks/other/pushNotificationConfigs/r2", ["pushNotificationConfig"] = new JsonObject { ["url"] = Hook },
        };
        using var mismatch = await http.PostAsync($"/a2a/v1/tasks/{taskId}/pushNotificationConfigs", V03Host.Json(wrongTask.ToJsonString()));
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
    }

    [Fact]
    public async Task Rest_create_also_reads_the_whole_request_as_the_v03_python_client_sends_it()
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var taskId = await InputTaskAsync(host);
        // a2a-sdk 0.3: {parent, configId, config}, the CreateTaskPushNotificationConfigRequest (not only the config).
        var body = new JsonObject
        {
            ["parent"] = $"tasks/{taskId}", ["configId"] = "py1",
            ["config"] = new JsonObject
            {
                ["name"] = $"tasks/{taskId}/pushNotificationConfigs/py1",
                ["pushNotificationConfig"] = new JsonObject { ["id"] = "py1", ["url"] = Hook, ["token"] = "tok" },
            },
        };
        using var create = await host.Http().PostAsync($"/a2a/v1/tasks/{taskId}/pushNotificationConfigs", V03Host.Json(body.ToJsonString()));
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        Assert.Equal($"tasks/{taskId}/pushNotificationConfigs/py1",
            JsonNode.Parse(await create.Content.ReadAsStringAsync())!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_v03_webhook_gets_the_task_as_a_v03_json_rpc_Task()
    {
        var (host, hooks) = await StartAsync();
        await using var _host = host;
        var taskId = await InputTaskAsync(host);
        await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/set", Config(taskId, "c1", "secret-token"));

        await FinishAsync(host, taskId);
        var calls = await WaitForAsync(hooks, Hook, b => b["status"]?["state"]?.GetValue<string>() == "completed");

        Assert.All(calls, c =>
        {
            var task = JsonNode.Parse(c.Body)!;
            Assert.Equal("task", task["kind"]!.GetValue<string>()); // the v0.3 JSON-RPC Task, not a v1.0 StreamResponse
            Assert.Equal(taskId, task["id"]!.GetValue<string>());
            Assert.Null(task["statusUpdate"]);
            Assert.Equal("application/json", c.Request.Content!.Headers.ContentType!.MediaType);
            Assert.Equal("0.3", Assert.Single(c.Request.Headers.GetValues("A2A-Version")));
            Assert.Equal("secret-token", Assert.Single(c.Request.Headers.GetValues("X-A2A-Notification-Token")));
        });
        var last = JsonNode.Parse(calls[^1].Body)!;
        Assert.Equal("completed", last["status"]!["state"]!.GetValue<string>());
        Assert.Contains(last["history"]!.AsArray(), m => m!["parts"]![0]!["text"]!.GetValue<string>() == "finished");
    }

    [Fact]
    public async Task A_config_in_a_v03_send_is_registered_and_gets_v03_bodies()
    {
        var (host, hooks) = await StartAsync();
        await using var _host = host;
        var extra = new JsonObject
        {
            ["configuration"] = new JsonObject { ["blocking"] = true, ["pushNotificationConfig"] = new JsonObject { ["url"] = Hook } },
        };
        var (_, body) = await V03Host.RpcAsync(host.Http(), "message/send", V03Host.SendParams("task", extra: extra));
        var taskId = body["result"]!["id"]!.GetValue<string>();

        var calls = await WaitForAsync(hooks, Hook, b => b["status"]?["state"]?.GetValue<string>() == "completed");
        Assert.All(calls, c => Assert.Equal("task", JsonNode.Parse(c.Body)!["kind"]!.GetValue<string>()));
        Assert.All(calls, c => Assert.Equal(taskId, JsonNode.Parse(c.Body)!["id"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_v1_config_on_the_same_task_still_gets_v1_bodies()
    {
        var (host, hooks) = await StartAsync();
        await using var _host = host;
        var taskId = await InputTaskAsync(host);
        await V03Host.RpcAsync(host.Http(), "tasks/pushNotificationConfig/set", Config(taskId));
        const string v1Hook = "http://hooks.test/v10";
        var (_, v1) = await V03Host.RpcAsync(host.Http(), "CreateTaskPushNotificationConfig",
            new JsonObject { ["taskId"] = taskId, ["url"] = v1Hook }, version: "1.0");
        Assert.False(v1.ContainsKey("error"), v1.ToJsonString());

        await FinishAsync(host, taskId);
        var v1Calls = await WaitForAsync(hooks, v1Hook, b => b["statusUpdate"]?["status"]?["state"]?.GetValue<string>() == "TASK_STATE_COMPLETED");
        Assert.All(v1Calls, c => Assert.Null(JsonNode.Parse(c.Body)!["kind"]));
        Assert.All(v1Calls, c => Assert.Equal("1.0", Assert.Single(c.Request.Headers.GetValues("A2A-Version"))));
        await WaitForAsync(hooks, Hook, b => b["status"]?["state"]?.GetValue<string>() == "completed");
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task The_v03_client_manages_configs_on_the_v03_server(string binding)
    {
        var (host, _) = await StartAsync();
        await using var _host = host;
        var taskId = await InputTaskAsync(host);
        var http = new HttpClient(host.App.GetTestServer().CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
        http.DefaultRequestHeaders.Add("X-Test-User", "alice");
        var card = V03Host.DefaultCard();
        card.SupportedInterfaces = [new AgentInterface { Url = V03Host.BaseUrl, ProtocolBinding = binding, ProtocolVersion = "0.3" }];
        var client = V03CompatClientFactory.Create(card, http, new A2AClientOptions { SupportedBindings = [binding], MaxRetries = 0 });

        var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = taskId, Id = "k1", Url = Hook });
        Assert.Equal("k1", created.Id);
        Assert.Equal(Hook, (await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = taskId, Id = "k1" })).Url);
        var listed = await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = taskId });
        Assert.Equal("k1", Assert.Single(listed.Configs).Id);
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = taskId, Id = "k1" });
        Assert.Empty((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = taskId })).Configs);
    }
}
