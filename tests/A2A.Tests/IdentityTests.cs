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
    {   // Task 6 will make anonymous opt-in; until then the host allows it.
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("task")).Task!;
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
        using var get = new HttpRequestMessage(HttpMethod.Get, $"/a2a/t1/tasks/{t}");
        get.Headers.Add("A2A-Version", "1.0");
        var resp = await http.SendAsync(get);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
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
