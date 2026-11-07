using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Server;
using A2A.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

public class IdentityTests
{
    [Fact]
    public async Task Anonymous_callers_get_anonymous_principal()
    {   // Anonymous access is opt-in: no endpoint authorization and AllowAnonymous = true.
        await using var host = await TestAgentHost.StartAsync(o => o.AllowAnonymous = true, requireAuth: false);
        var task = (await host.Client(ProtocolBindings.JsonRpc, user: null).SendMessageAsync("task")).RequireTask();
        var rec = await host.App.Services.GetRequiredService<TaskStore>()
            .GetAsync(new TaskKey("", ServerCallContext.AnonymousPrincipal, task.Id));
        Assert.Equal(ServerCallContext.AnonymousPrincipal, rec!.Owner);
    }

    [Fact]
    public void Body_tenant_conflicting_with_route_tenant_is_rejected()
    {
        var call = new ServerCallContext { Tenant = "t1", TenantFromRoute = true, Principal = "p" };
        var ex = Assert.Throws<A2AException>(() => call.BindTenant("t2"));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_body_tenant_is_same_as_absent(string? body)
    {
        var call = new ServerCallContext { Tenant = "", Principal = "p" };
        call.BindTenant(body);
        Assert.Equal("", call.Tenant);
    }

    [Fact]
    public async Task Tenants_are_case_sensitive()
    {
        await using var host = await TestAgentHost.StartAsync();
        var http = host.Http("alice");
        var t = await CreateTaskViaRestAsync(http, "/a2a/T1");

        // Same case: found. So the 404 below comes from tenant scoping, not from a bad path.
        using var same = new HttpRequestMessage(HttpMethod.Get, $"/a2a/T1/tasks/{t}");
        same.Headers.Add("A2A-Version", "1.0");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(same)).StatusCode);

        using var get = new HttpRequestMessage(HttpMethod.Get, $"/a2a/t1/tasks/{t}");
        get.Headers.Add("A2A-Version", "1.0");
        var resp = await http.SendAsync(get);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task JsonRpc_tenant_route_scopes_tasks()
    {
        await using var host = await TestAgentHost.StartAsync();
        var http = host.Http("alice");
        var id = await CreateTaskViaJsonRpcAsync(http, "/a2a/acme");

        var found = await JsonRpcGetTaskAsync(http, "/a2a/acme", id);
        Assert.Contains($"\"id\":\"{id}\"", found);
        var missing = await JsonRpcGetTaskAsync(http, "/a2a", id);
        Assert.Contains("\"error\"", missing);
        Assert.Contains("-32001", missing);
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc)]
    [InlineData(ProtocolBindings.HttpJson)]
    public async Task Authenticated_user_without_an_id_claim_is_unauthenticated(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var http = host.Http("alice");
        http.DefaultRequestHeaders.Add("X-Test-NoId", "1");
        using var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a")
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"ListTasks","params":{}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks");
        req.Headers.Add("A2A-Version", "1.0");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Contains("UNAUTHENTICATED", await resp.Content.ReadAsStringAsync());
    }

    private static async Task<string> CreateTaskViaJsonRpcAsync(HttpClient http, string url)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"task"}]}}}""",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("A2A-Version", "1.0");
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetProperty("task").GetProperty("id").GetString()!;
    }

    private static async Task<string> JsonRpcGetTaskAsync(HttpClient http, string url, string id)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent($$$"""{"jsonrpc":"2.0","id":2,"method":"GetTask","params":{"id":"{{{id}}}"}}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("A2A-Version", "1.0");
        return await (await http.SendAsync(req)).Content.ReadAsStringAsync();
    }

    private static async Task<string> CreateTaskViaRestAsync(HttpClient http, string prefix)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{prefix}/message:send")
        {
            Content = new StringContent(
                """{"message":{"messageId":"m","role":"ROLE_USER","parts":[{"text":"task"}]}}""", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("A2A-Version", "1.0");
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("task").GetProperty("id").GetString()!;
    }
}
