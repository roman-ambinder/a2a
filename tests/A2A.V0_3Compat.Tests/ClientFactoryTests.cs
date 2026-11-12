using System.Text;
using System.Text.Json.Nodes;
using A2A.Client;
using A2A.Client.V0_3Compat;
using A2A.Json;
using static A2A.V0_3Compat.Tests.ClientData;

namespace A2A.V0_3Compat.Tests;

/// <summary>Interface selection of <see cref="V03CompatClientFactory"/>: v0.3 only when the card offers no v1.x interface.</summary>
public class ClientFactoryTests
{
    private const string Rpc = ProtocolBindings.JsonRpc;
    private const string Rest = ProtocolBindings.HttpJson;

    private static FakeV03Server Server() => new(r =>
        r.PathAndQuery.EndsWith("agent-card.json", StringComparison.Ordinal) ? FakeV03Server.Json("{}")
        : r.Method == HttpMethod.Post && r.PathAndQuery.EndsWith("message:send", StringComparison.Ordinal) ? FakeV03Server.Json(new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson("TASK_STATE_COMPLETED")) })
        : r.Json?["method"] is JsonNode method && (string?)method == "SendMessage" ? FakeV03Server.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "1", ["result"] = new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson("TASK_STATE_COMPLETED")) } })
        : FakeV03Server.Rpc(JsonNode.Parse(TaskJson("completed"))));

    // ---- Create(card) ----

    [Fact]
    public async Task A_v03_only_card_uses_the_v03_json_rpc_transport()
    {
        var server = Server();
        var client = V03CompatClientFactory.Create(CardWith(Itf(Rpc, "http://agent.test/rpc", "0.3")), server.Client());
        await client.SendMessageAsync("hi");
        Assert.Equal("/rpc", server.Last.PathAndQuery);
        Assert.Equal("0.3", server.Last.Header("A2A-Version"));
        Assert.Equal("message/send", (string?)server.Last.Json!["method"]);
    }

    [Fact]
    public async Task A_v03_only_card_uses_the_v03_rest_transport()
    {
        var server = Server();
        var client = V03CompatClientFactory.Create(CardWith(Itf(Rest, "http://agent.test/api", "0.3.0")), server.Client());
        await client.SendMessageAsync("hi");
        Assert.Equal("/api/v1/message:send", server.Last.PathAndQuery);
        Assert.Equal("0.3", server.Last.Header("A2A-Version"));
    }

    [Fact]
    public async Task The_first_v03_interface_in_card_order_wins()
    {
        var server = Server();
        var card = CardWith(Itf(Rest, "http://agent.test/api", "0.3"), Itf(Rpc, "http://agent.test/rpc", "0.3"));
        await V03CompatClientFactory.Create(card, server.Client()).SendMessageAsync("hi");
        Assert.StartsWith("/api/v1/", server.Last.PathAndQuery);

        var reversed = CardWith(Itf(Rpc, "http://agent.test/rpc", "0.3"), Itf(Rest, "http://agent.test/api", "0.3"));
        await V03CompatClientFactory.Create(reversed, server.Client()).SendMessageAsync("hi");
        Assert.Equal("/rpc", server.Last.PathAndQuery);
    }

    [Fact]
    public async Task Supported_bindings_limit_the_v03_choice()
    {
        var server = Server();
        var card = CardWith(Itf(Rest, "http://agent.test/api", "0.3"), Itf(Rpc, "http://agent.test/rpc", "0.3"));
        var options = new A2AClientOptions { SupportedBindings = [Rpc] };
        await V03CompatClientFactory.Create(card, server.Client(), options).SendMessageAsync("hi");
        Assert.Equal("/rpc", server.Last.PathAndQuery);
    }

    [Fact]
    public async Task A_card_that_offers_v1_is_never_downgraded()
    {
        var server = Server();
        // The v0.3 interface comes first in card order. v1.0 still wins.
        var card = CardWith(Itf(Rpc, "http://agent.test/old", "0.3"), Itf(Rpc, "http://agent.test/new", "1.0"));
        var client = V03CompatClientFactory.Create(card, server.Client());
        await client.SendMessageAsync("hi");
        Assert.Equal("/new", server.Last.PathAndQuery);
        Assert.Equal("1.0", server.Last.Header("A2A-Version"));
        Assert.Equal("SendMessage", (string?)server.Last.Json!["method"]);
    }

    [Fact]
    public async Task A_card_with_both_versions_and_both_bindings_goes_down_the_v1_path()
    {
        var server = Server();
        var card = CardWith(Itf(Rest, "http://agent.test/a2a", "1.0"), Itf(Rpc, "http://agent.test/a2a", "1.0"),
            Itf(Rpc, "http://agent.test/a2a", "0.3"), Itf(Rest, "http://agent.test/a2a", "0.3"));
        // Round trip through the card JSON, as a client that fetched the card would hold it.
        var json = Encoding.UTF8.GetString(A2AJson.SerializeToUtf8Bytes(card));
        var parsed = A2AJson.Deserialize<AgentCard>(json);
        await V03CompatClientFactory.Create(parsed, server.Client()).SendMessageAsync("hi");
        Assert.Equal("1.0", server.Last.Header("A2A-Version"));
    }

    [Fact]
    public void A_card_with_no_usable_interface_is_refused()
    {
        var grpcOnly = CardWith(Itf(ProtocolBindings.Grpc, "http://agent.test:50051", "0.3"));
        var ex = Assert.Throws<A2AException>(() => V03CompatClientFactory.Create(grpcOnly, new HttpClient()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);

        var empty = CardWith();
        Assert.Throws<A2AException>(() => V03CompatClientFactory.Create(empty, new HttpClient()));

        var v02 = CardWith(Itf(Rpc, "http://agent.test/rpc", "0.2"));
        Assert.Throws<A2AException>(() => V03CompatClientFactory.Create(v02, new HttpClient()));
    }

    [Fact]
    public async Task The_interface_tenant_is_applied_to_the_copy_only()
    {
        var server = Server();
        var options = new A2AClientOptions();
        var client = V03CompatClientFactory.Create(CardWith(Itf(Rest, "http://agent.test/api", "0.3", tenant: "acme")), server.Client(), options);
        await client.SendMessageAsync("hi");
        Assert.Equal("/api/acme/v1/message:send", server.Last.PathAndQuery);
        Assert.Null(options.Tenant);
    }

    [Fact]
    public void The_callers_options_are_not_changed()
    {
        var options = new A2AClientOptions { ProtocolVersion = "1.0", Tenant = "mine", MaxRetries = 5 };
        options.Extensions.Add("urn:x");
        var bindings = options.SupportedBindings.ToList();
        var factories = options.TransportFactories.ToList();

        V03CompatClientFactory.Create(CardWith(Itf(Rpc, "http://agent.test/rpc", "0.3", tenant: "t")), new HttpClient(), options);
        V03CompatClientFactory.Create(CardWith(Itf(Rpc, "http://agent.test/rpc", "1.0")), new HttpClient(), options);

        Assert.Equal("1.0", options.ProtocolVersion);
        Assert.Equal("mine", options.Tenant);
        Assert.Equal(5, options.MaxRetries);
        Assert.Equal(["urn:x"], options.Extensions);
        Assert.Equal(bindings, options.SupportedBindings);
        Assert.Equal(factories, options.TransportFactories);
    }

    [Fact]
    public async Task The_client_keeps_the_card_and_checks_capabilities()
    {
        var server = Server();
        var card = CardWith(Itf(Rpc, "http://agent.test/rpc", "0.3"));
        card.Capabilities.Streaming = false;
        var client = V03CompatClientFactory.Create(card, server.Client());
        Assert.Same(card, client.AgentCard);
        var ex = await Assert.ThrowsAsync<A2AException>(async () =>
        {
            await foreach (var _ in client.SendStreamingMessageAsync(new Message { MessageId = "m", Role = Role.User, Parts = [Part.FromText("x")] })) { }
        });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Per_call_extensions_use_the_v03_header_through_the_client()
    {
        var server = Server();
        var options = new A2AClientOptions();
        options.Extensions.Add("urn:a");
        await V03CompatClientFactory.Create(CardWith(Itf(Rpc, "http://agent.test/rpc", "0.3")), server.Client(), options).SendMessageAsync("hi");
        Assert.Equal("urn:a", server.Last.Header("X-A2A-Extensions"));
    }

    // ---- CreateAsync (resolver) ----

    private static FakeV03Server CardServer(string cardJson) => new(r =>
        r.PathAndQuery.EndsWith("agent-card.json", StringComparison.Ordinal)
            ? FakeV03Server.Json(cardJson)
            : r.Json?["method"] is JsonNode method && (string?)method == "SendMessage"
                ? FakeV03Server.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "1", ["result"] = new JsonObject { ["task"] = JsonNode.Parse(RestTaskJson("TASK_STATE_COMPLETED")) } })
                : FakeV03Server.Rpc(JsonNode.Parse(TaskJson("completed"))));

    private static readonly Uri AgentUrl = new("https://agent.example/");

    [Fact]
    public async Task A_v03_card_is_fetched_converted_and_used()
    {
        var server = CardServer(Golden.Text("agent-card.json"));
        var client = await V03CompatClientFactory.CreateAsync(AgentUrl, server.Client());
        Assert.Equal("Golden Agent", client.AgentCard!.Name);
        Assert.All(client.AgentCard.SupportedInterfaces, i => Assert.Equal("0.3", i.ProtocolVersion));

        var response = await client.SendMessageAsync("hi");
        Assert.IsType<TaskResponse>(response);
        Assert.Equal("/a2a", server.Last.PathAndQuery);
        Assert.Equal("0.3", server.Last.Header("A2A-Version"));
        Assert.Equal("message/send", (string?)server.Last.Json!["method"]);
    }

    [Fact]
    public async Task A_signed_v03_card_is_refused_when_signatures_are_required()
    {
        var server = CardServer(Golden.Text("agent-card.json"));
        var resolverOptions = new A2ACardResolverOptions { Trust = { RequireSignature = true } };
        await Assert.ThrowsAsync<AgentCardVerificationException>(() => V03CompatClientFactory.CreateAsync(AgentUrl, server.Client(), resolverOptions: resolverOptions));
    }

    [Fact]
    public async Task A_signed_v03_card_reads_as_unsigned_when_signatures_are_not_required()
    {
        var server = CardServer(Golden.Text("agent-card.json"));
        var client = await V03CompatClientFactory.CreateAsync(AgentUrl, server.Client());
        Assert.Null(client.AgentCard!.Signatures);
    }

    [Fact]
    public async Task Resolver_options_are_not_changed()
    {
        var server = CardServer(Golden.Text("agent-card.json"));
        var resolverOptions = new A2ACardResolverOptions { MaxCardTextLength = 77 };
        await V03CompatClientFactory.CreateAsync(AgentUrl, server.Client(), resolverOptions: resolverOptions);
        Assert.Null(resolverOptions.CardNormalizer);
        Assert.Equal(77, resolverOptions.MaxCardTextLength);
    }

    [Fact]
    public async Task A_v02_card_is_used_over_json_rpc()
    {
        var card = Golden.Object("agent-card.json");
        card["protocolVersion"] = "0.2.6";
        card["preferredTransport"] = "HTTP+JSON"; // the v0.2 HTTP+JSON wire is not used
        var server = CardServer(card.ToJsonString());
        var client = await V03CompatClientFactory.CreateAsync(AgentUrl, server.Client());
        Assert.All(client.AgentCard!.SupportedInterfaces, i => Assert.Equal(ProtocolBindings.JsonRpc, i.ProtocolBinding));

        await client.SendMessageAsync("hi");
        Assert.Equal("/a2a", server.Last.PathAndQuery);
        Assert.Equal("message/send", (string?)server.Last.Json!["method"]);
    }

    [Fact]
    public async Task A_v01_card_is_refused()
    {
        var card = Golden.Object("agent-card.json");
        card["protocolVersion"] = "0.1.0";
        var server = CardServer(card.ToJsonString());
        var ex = await Assert.ThrowsAsync<A2AException>(() => V03CompatClientFactory.CreateAsync(AgentUrl, server.Client()));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    /// <summary>A v0.2 agent: 404 at agent-card.json, its card at agent.json, JSON-RPC elsewhere.</summary>
    private static FakeV03Server LegacyCardServer(string cardJson) => new(r =>
        r.PathAndQuery.EndsWith("agent-card.json", StringComparison.Ordinal)
            ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            : r.PathAndQuery.EndsWith("/.well-known/agent.json", StringComparison.Ordinal)
                ? FakeV03Server.Json(cardJson)
                : FakeV03Server.Rpc(JsonNode.Parse(TaskJson("completed"))));

    [Fact]
    public async Task A_v02_agent_is_found_at_the_legacy_card_path()
    {
        var card = Golden.Object("agent-card.json");
        card["protocolVersion"] = "0.2.5";
        var server = LegacyCardServer(card.ToJsonString());
        var client = await V03CompatClientFactory.CreateAsync(AgentUrl, server.Client());

        Assert.Equal("/.well-known/agent-card.json", server.Requests[0].PathAndQuery);
        Assert.Equal("/.well-known/agent.json", server.Requests[1].PathAndQuery);
        await client.SendMessageAsync("hi");
        Assert.Equal("message/send", (string?)server.Last.Json!["method"]);
    }

    [Fact]
    public async Task The_legacy_card_path_is_not_tried_for_an_explicit_card_url_or_another_status()
    {
        var server = LegacyCardServer(Golden.Text("agent-card.json"));
        var explicitUrl = new Uri("https://agent.example/.well-known/agent-card.json");
        var notFound = await Assert.ThrowsAsync<A2AException>(() => V03CompatClientFactory.CreateAsync(explicitUrl, server.Client()));
        Assert.Equal(404, notFound.HttpStatus);
        Assert.Single(server.Requests);

        var failing = new FakeV03Server(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var error = await Assert.ThrowsAsync<A2AException>(() => V03CompatClientFactory.CreateAsync(AgentUrl, failing.Client()));
        Assert.Equal(500, error.HttpStatus);
        Assert.Single(failing.Requests);
    }

    [Fact]
    public async Task A_v1_card_takes_the_v1_path_through_create_async()
    {
        var card = CardWith(Itf(Rpc, "https://agent.example/rpc", "1.0"));
        var server = CardServer(Encoding.UTF8.GetString(A2AJson.SerializeToUtf8Bytes(card)));
        var client = await V03CompatClientFactory.CreateAsync(AgentUrl, server.Client());
        await client.SendMessageAsync("hi");
        Assert.Equal("1.0", server.Last.Header("A2A-Version"));
        Assert.Equal("SendMessage", (string?)server.Last.Json!["method"]);
    }

    [Fact]
    public async Task Http_card_urls_still_need_https()
    {
        var server = CardServer(Golden.Text("agent-card.json"));
        var ex = await Assert.ThrowsAsync<A2AException>(() => V03CompatClientFactory.CreateAsync(new Uri("http://agent.example/"), server.Client()));
        Assert.Equal(A2AErrorCode.InvalidRequest, ex.Code);
    }
}
