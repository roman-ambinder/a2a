using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Server;
using A2A.Server.Agents;
using A2A.Server.AspNetCore;
using A2A.Server.AspNetCore.Operations;
using A2A.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Operations;

/// <summary>
/// Custom (extension) operations: activation by the <c>A2A-Extensions</c> header, per-tenant cards, rate limiting, reserved
/// names and the typed overload.
/// </summary>
public sealed partial class CustomOperationTests
{
    private const string Method = "x.count";
    private const string Uri = "urn:test:x-count";
    private const string RequiredUri = "urn:test:tenant-required";

    /// <summary>Counts how often the handler ran, and echoes the call's tenant and owner.</summary>
    private sealed class Counter
    {
        public int Calls;
        public ValueTask<JsonElement> Handle(A2AOperationContext context, JsonElement parameters, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            var json = $$"""{"tenant":"{{context.Call.Tenant}}","principal":"{{context.Call.Principal}}"}""";
            return ValueTask.FromResult(JsonDocument.Parse(json).RootElement.Clone());
        }
    }

    private static Action<IServiceCollection> WithCounter(Counter counter, bool allow = true, Action<IServiceCollection>? more = null) => s =>
    {
        s.AddA2AServer().AddExtensionOperation(Method, Uri, counter.Handle, allow: allow);
        more?.Invoke(s);
    };

    private static HttpRequestMessage Call(string binding, string? extensions, string tenant = "")
    {
        var prefix = tenant.Length == 0 ? "/a2a" : "/a2a/" + tenant;
        var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, prefix)
            {
                Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"{{{Method}}}","params":{"say":"hi"}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Post, $"{prefix}/ext/{Method}") { Content = new StringContent("""{"say":"hi"}""", Encoding.UTF8, "application/json") };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        if (extensions is not null) req.Headers.Add(A2AProtocol.Headers.Extensions, extensions);
        return req;
    }

    private static async Task<(HttpResponseMessage Response, JsonElement Body)> SendAsync(TestAgentHost host, HttpRequestMessage req, string? user = "alice")
    {
        using (req)
        {
            var resp = await host.Http(user).SendAsync(req);
            var text = await resp.Content.ReadAsStringAsync();
            return (resp, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
        }
    }

    private static JsonElement ResultOf(string binding, JsonElement body) => binding == ProtocolBindings.JsonRpc ? body.GetProperty("result") : body;

    /// <summary>Asserts the body is the refusal <paramref name="code"/> in the format of the binding (its ErrorInfo reason is in the body).</summary>
    private static void AssertRefused(string binding, JsonElement body, A2AErrorCode code)
    {
        var error = body.GetProperty("error");
        if (binding == ProtocolBindings.JsonRpc) Assert.Equal((int)code, error.GetProperty("code").GetInt32());
        Assert.Contains(A2AErrors.Reason(code), error.GetRawText());
    }

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    // ---------------------------------------------------------------- activation

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_call_without_the_extension_header_is_refused_and_the_handler_does_not_run(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter));

        var (resp, body) = await SendAsync(host, Call(binding, extensions: null));

        Assert.Equal(binding == ProtocolBindings.JsonRpc ? HttpStatusCode.OK : HttpStatusCode.BadRequest, resp.StatusCode);
        AssertRefused(binding, body, A2AErrorCode.ExtensionSupportRequired);
        Assert.Contains(Uri, body.GetRawText());
        Assert.Equal(0, counter.Calls);

        // Another extension in the header does not activate this one.
        var (_, other) = await SendAsync(host, Call(binding, TestAgentHost.OptionalExtension));
        AssertRefused(binding, other, A2AErrorCode.ExtensionSupportRequired);
        Assert.Equal(0, counter.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_call_with_the_extension_header_runs_and_echoes_the_activated_extension(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter));

        var (resp, body) = await SendAsync(host, Call(binding, Uri));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, counter.Calls);
        Assert.Contains(Uri, string.Join(",", resp.Headers.GetValues(A2AProtocol.Headers.Extensions)).Split(',').Select(u => u.Trim()));
        // The context carries the tenant and the principal: both parts of a task key.
        Assert.Equal("alice", ResultOf(binding, body).GetProperty("principal").GetString());
        Assert.Equal("", ResultOf(binding, body).GetProperty("tenant").GetString());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_denied_caller_gets_the_denial_not_the_extension_error(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter, allow: false));

        var (resp, body) = await SendAsync(host, Call(binding, extensions: null));

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        Assert.Contains("PERMISSION_DENIED", body.GetRawText());
        Assert.DoesNotContain("EXTENSION_SUPPORT_REQUIRED", body.GetRawText());
        Assert.Equal(0, counter.Calls);
    }

    // ---------------------------------------------------------------- per-tenant cards

    private sealed class Tenants : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            if (scope.Tenant == "none") return ValueTask.FromResult<AgentCard?>(null);
            var card = TestAgentHost.DefaultCard();
            if (scope.Tenant == "plain") card.Capabilities.Extensions = [new AgentExtension { Uri = Uri }];
            if (scope.Tenant == "required") card.Capabilities.Extensions = [new AgentExtension { Uri = Uri }, new AgentExtension { Uri = RequiredUri, Required = true }];
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task With_per_tenant_cards_the_tenants_card_activates_the_extension(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter, more: s => s.AddA2AServer().AddTenantCardProvider<Tenants>()));

        var (ok, body) = await SendAsync(host, Call(binding, Uri, tenant: "plain"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, counter.Calls);
        Assert.Equal("plain", ResultOf(binding, body).GetProperty("tenant").GetString());
    }

    /// <summary>A host card provider of its own: it does not add the URIs of custom operations to its card.</summary>
    private sealed class OwnCards : IAgentCardProvider
    {
        public AgentCard Get() => TestAgentHost.DefaultCard();
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_card_that_does_not_list_the_extension_cannot_activate_it(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter, more: s =>
            s.Replace(ServiceDescriptor.Singleton<IAgentCardProvider>(new OwnCards()))));

        // The caller asks for the extension, but the card of the host does not declare it.
        var (_, body) = await SendAsync(host, Call(binding, Uri));

        AssertRefused(binding, body, A2AErrorCode.ExtensionSupportRequired);
        Assert.Equal(0, counter.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task With_per_tenant_cards_an_unserved_tenant_is_UnsupportedOperation_and_the_handler_does_not_run(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter, more: s => s.AddA2AServer().AddTenantCardProvider<Tenants>()));

        var (_, body) = await SendAsync(host, Call(binding, Uri, tenant: "none"));

        AssertRefused(binding, body, A2AErrorCode.UnsupportedOperation);
        Assert.Equal(0, counter.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task With_per_tenant_cards_a_required_extension_of_the_tenant_is_enforced(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter, more: s => s.AddA2AServer().AddTenantCardProvider<Tenants>()));

        var (_, refused) = await SendAsync(host, Call(binding, Uri, tenant: "required"));
        AssertRefused(binding, refused, A2AErrorCode.ExtensionSupportRequired);
        Assert.Contains(RequiredUri, refused.GetRawText());
        Assert.Equal(0, counter.Calls);

        var (ok, _) = await SendAsync(host, Call(binding, $"{Uri}, {RequiredUri}", tenant: "required"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(1, counter.Calls);
    }

    // ---------------------------------------------------------------- rate limit

    [Theory, MemberData(nameof(Bindings))]
    public async Task Over_the_rate_limit_a_custom_operation_is_refused_in_the_format_of_its_binding(string binding)
    {
        var counter = new Counter();
        await using var host = await TestAgentHost.StartAsync(services: WithCounter(counter, more: s =>
            s.AddA2ARateLimiting(o => { o.TokenLimit = 2; o.TokensPerPeriod = 1; o.ReplenishmentPeriod = TimeSpan.FromHours(1); })));

        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.OK, (await SendAsync(host, Call(binding, Uri))).Response.StatusCode);

        var (resp, body) = await SendAsync(host, Call(binding, Uri));

        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        Assert.Contains("RATE_LIMITED", body.GetRawText());
        Assert.Equal(JsonValueKind.Object, body.GetProperty("error").ValueKind);
        Assert.Equal(2, counter.Calls);
    }

    // ---------------------------------------------------------------- names

    [Theory]
    [InlineData("rpc.x")]
    [InlineData("RPC.x")]
    [InlineData("rpc.")]
    [InlineData("SendMessage")]
    [InlineData("sendmessage")]
    [InlineData("GetExtendedAgentCard")]
    public void The_builder_and_the_table_refuse_reserved_names(string method)
    {
        var builder = new ServiceCollection().AddA2AServer();
        Assert.Throws<ArgumentException>(() => builder.AddExtensionOperation(method, Uri, (_, p, _) => ValueTask.FromResult(p), allow: true));

        var record = new A2AExtensionOperation(method, Uri, (_, p, _) => ValueTask.FromResult(p), Allow: true);
        var ex = Assert.Throws<InvalidOperationException>(() => new A2AOperationTable([record]));
        Assert.Contains(method, ex.Message);
    }

    [Theory]
    [InlineData(A2AProtocol.Methods.SendMessage, true)]
    [InlineData(A2AProtocol.Methods.SendStreamingMessage, true)]
    [InlineData(A2AProtocol.Methods.GetTask, true)]
    [InlineData(A2AProtocol.Methods.ListTasks, true)]
    [InlineData(A2AProtocol.Methods.CancelTask, true)]
    [InlineData(A2AProtocol.Methods.SubscribeToTask, true)]
    [InlineData(A2AProtocol.Methods.CreateTaskPushNotificationConfig, true)]
    [InlineData(A2AProtocol.Methods.GetTaskPushNotificationConfig, true)]
    [InlineData(A2AProtocol.Methods.ListTaskPushNotificationConfigs, true)]
    [InlineData(A2AProtocol.Methods.DeleteTaskPushNotificationConfig, true)]
    [InlineData(A2AProtocol.Methods.GetExtendedAgentCard, true)]
    [InlineData("SENDMESSAGE", true)]
    [InlineData("rpc.anything", true)]
    [InlineData("Rpc.Anything", true)]
    [InlineData("rpc", false)]
    [InlineData("rpcx", false)]
    [InlineData("x.rpc.y", false)]
    [InlineData("x.count", false)]
    [InlineData("", false)]
    public void IsReserved_matches_built_in_and_rpc_names_ignoring_case(string method, bool reserved) =>
        Assert.Equal(reserved, A2AProtocol.Methods.IsReserved(method));

    // ---------------------------------------------------------------- typed overload

    public sealed record Sum(int A, int B);
    public sealed record Total(int Value, string Who);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(Sum))]
    [JsonSerializable(typeof(Total))]
    private sealed partial class CustomOperationJson : JsonSerializerContext;

    private static Action<IServiceCollection> Typed() => s =>
        s.AddA2AServer().AddExtensionOperation("x.sum", Uri, CustomOperationJson.Default.Sum, CustomOperationJson.Default.Total,
            (context, request, _) => ValueTask.FromResult(new Total(request.A + request.B, context.Call.Principal)), allow: true);

    private static HttpRequestMessage TypedCall(string binding, string @params)
    {
        var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a")
            {
                Content = new StringContent($$$"""{"jsonrpc":"2.0","id":1,"method":"x.sum","params":{{{@params}}}}""", Encoding.UTF8, "application/json"),
            }
            : new HttpRequestMessage(HttpMethod.Post, "/a2a/ext/x.sum") { Content = new StringContent(@params, Encoding.UTF8, "application/json") };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add(A2AProtocol.Headers.Extensions, Uri);
        return req;
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_typed_overload_round_trips(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(services: Typed());

        var (resp, body) = await SendAsync(host, TypedCall(binding, """{"a":2,"b":3}"""));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(5, ResultOf(binding, body).GetProperty("value").GetInt32());
        Assert.Equal("alice", ResultOf(binding, body).GetProperty("who").GetString());
    }

    [Theory]
    [InlineData(ProtocolBindings.JsonRpc, """{"a":"x","b":3}""")]
    [InlineData(ProtocolBindings.JsonRpc, "[1]")]
    [InlineData(ProtocolBindings.HttpJson, """{"a":"x","b":3}""")]
    [InlineData(ProtocolBindings.HttpJson, "null")]
    public async Task The_typed_overload_refuses_params_that_do_not_bind_with_InvalidParams(string binding, string @params)
    {
        await using var host = await TestAgentHost.StartAsync(services: Typed());

        var (_, body) = await SendAsync(host, TypedCall(binding, @params));

        AssertRefused(binding, body, A2AErrorCode.InvalidParams);
    }
}
