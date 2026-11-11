using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Peers;
using A2A.Json;
using A2A.Signing;

namespace A2A.Tests;

public class BindingTests : IAsyncLifetime
{
    private TestAgentHost _host = null!;

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    public async ValueTask InitializeAsync() => _host = await TestAgentHost.StartAsync();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Direct_reply_returns_message_without_task(string binding)
    {
        var client = _host.Client(binding);
        var resp = await client.SendMessageAsync("hello");
        var message = Assert.IsType<MessageResponse>(resp).Message;
        Assert.Equal("hi there", message.GetText());
        Assert.Equal(Role.Agent, message.Role);
        Assert.False(string.IsNullOrEmpty(message.ContextId));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Blocking_send_returns_completed_task_with_merged_artifact(string binding)
    {
        var client = _host.Client(binding);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
        var artifact = Assert.Single(task.Artifacts!);
        Assert.Equal(["part1", "part2"], artifact.Parts.OfType<TextPart>().Select(p => p.Text));
        Assert.Equal("result", artifact.Name);
        Assert.Equal(Role.User, task.History![0].Role);
        Assert.Equal(task.Id, task.History[0].TaskId);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Streaming_emits_task_first_then_ordered_events_and_closes(string binding)
    {
        var client = _host.Client(binding);
        var events = new List<StreamResponse>();
        await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("task"))) events.Add(e);

        Assert.Equal(TaskState.Submitted, Assert.IsType<TaskEvent>(events[0]).Task.Status.State);
        Assert.Equal(TaskState.Working, Assert.IsType<StatusUpdateEvent>(events[1]).StatusUpdate.Status.State);
        Assert.False(Assert.IsType<ArtifactUpdateEvent>(events[2]).ArtifactUpdate.Append);
        Assert.True(Assert.IsType<ArtifactUpdateEvent>(events[3]).ArtifactUpdate.Append);
        Assert.True(Assert.IsType<ArtifactUpdateEvent>(events[3]).ArtifactUpdate.LastChunk);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(events[^1]).StatusUpdate.Status.State);
        Assert.Equal(5, events.Count);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Multi_turn_input_required_then_terminal_rejects_more_messages(string binding)
    {
        var client = _host.Client(binding);
        var first = (await client.SendMessageAsync("input")).RequireTask();
        Assert.Equal(TaskState.InputRequired, first.Status.State);

        var second = (await client.SendMessageAsync("more", first.ContextId, first.Id)).RequireTask();
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(TaskState.Completed, second.Status.State);
        Assert.Equal("got: more", second.Status.Message!.GetText());
        Assert.Equal(4, second.History!.Count); // user, agent(need more), user, agent(got)

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("again", null, first.Id));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);

        var mismatch = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync("x", "other-context", first.Id));
        Assert.Contains(mismatch.Code, new[] { A2AErrorCode.InvalidParams, A2AErrorCode.UnsupportedOperation });
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Get_task_applies_history_length(string binding)
    {
        var client = _host.Client(binding);
        var task = (await client.SendMessageAsync("task")).RequireTask();
        Assert.Null((await client.GetTaskAsync(task.Id, 0)).History);
        Assert.Single((await client.GetTaskAsync(task.Id, 1)).History!);
        var missing = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync("nope"));
        Assert.Equal(A2AErrorCode.TaskNotFound, missing.Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Return_immediately_then_cancel_running_task(string binding)
    {
        var client = _host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();
        Assert.False(task.Status.State.IsTerminal());

        var canceled = await client.CancelTaskAsync(task.Id);
        Assert.Equal(TaskState.Canceled, canceled.Status.State);

        var again = await client.CancelTaskAsync(task.Id); // the client turns the repeat cancel into the canceled task
        Assert.Equal(TaskState.Canceled, again.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Subscribe_gets_snapshot_then_live_events_until_terminal(string binding)
    {
        var client = _host.Client(binding);
        var task = (await client.SendMessageAsync(Message.FromUser("slow"), new SendMessageConfiguration { ReturnImmediately = true })).RequireTask();

        var received = new List<StreamResponse>();
        var subscribed = new TaskCompletionSource();
        var reader = Task.Run(async () =>
        {
            await foreach (var e in client.SubscribeToTaskAsync(task.Id))
            {
                received.Add(e);
                subscribed.TrySetResult();
            }
        });
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await client.CancelTaskAsync(task.Id);
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(task.Id, Assert.IsType<TaskEvent>(received[0]).Task.Id);
        Assert.Equal(TaskState.Canceled, Assert.IsType<StatusUpdateEvent>(received[^1]).StatusUpdate.Status.State);

        var terminal = await Assert.ThrowsAsync<A2AException>(async () => { await foreach (var _ in client.SubscribeToTaskAsync(task.Id)) { } });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, terminal.Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Auth_required_keeps_stream_open_until_out_of_band_completion(string binding)
    {
        var client = _host.Client(binding);
        var states = new List<TaskState>();
        await foreach (var e in client.SendStreamingMessageAsync(Message.FromUser("auth")))
        {
            if (e is StatusUpdateEvent { StatusUpdate: var su })
            {
                states.Add(su.Status.State);
                if (su.Status.State == TaskState.AuthRequired)
                    ScenarioAgent.AuthSignals.GetOrAdd(su.TaskId, _ => new TaskCompletionSource()).TrySetResult();
            }
        }
        Assert.Equal([TaskState.AuthRequired, TaskState.Completed], states);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Executor_exception_becomes_failed_task_without_leaking_details(string binding)
    {
        var task = (await _host.Client(binding).SendMessageAsync("fail")).RequireTask();
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.DoesNotContain("boom", task.Status.Message!.GetText());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Agent_that_does_nothing_yields_invalid_agent_response(string binding)
    {
        var ex = await Assert.ThrowsAsync<A2AException>(() => _host.Client(binding).SendMessageAsync("silent"));
        Assert.Equal(A2AErrorCode.InvalidAgentResponse, ex.Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Tasks_are_scoped_to_their_owner(string binding)
    {
        var alice = _host.Client(binding, "alice");
        var bob = _host.Client(binding, "bob");
        var task = (await alice.SendMessageAsync("task")).RequireTask();

        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => bob.GetTaskAsync(task.Id))).Code);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => bob.CancelTaskAsync(task.Id))).Code);
        Assert.Empty((await bob.ListTasksAsync(new ListTasksRequest())).Tasks);
        var mine = await alice.ListTasksAsync(new ListTasksRequest());
        Assert.Equal(task.Id, Assert.Single(mine.Tasks).Id);
        Assert.Equal(1, mine.TotalSize);
        Assert.Null(mine.Tasks[0].Artifacts); // includeArtifacts defaults to false
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task List_tasks_paginates_and_filters(string binding)
    {
        var client = _host.Client(binding, "pager-" + binding);
        var ids = new List<string>();
        for (var i = 0; i < 5; i++) ids.Add((await client.SendMessageAsync("task")).RequireTask().Id);
        var input = (await client.SendMessageAsync("input")).RequireTask();

        var all = new List<AgentTask>();
        await foreach (var t in client.ListAllTasksAsync(new ListTasksRequest { PageSize = 2, IncludeArtifacts = true })) all.Add(t);
        Assert.Equal(6, all.Count);
        Assert.Equal(6, all.Select(t => t.Id).Distinct().Count());
        Assert.NotNull(all.First(t => t.Id == ids[0]).Artifacts);

        var filtered = await client.ListTasksAsync(new ListTasksRequest { Status = TaskState.InputRequired });
        Assert.Equal(input.Id, Assert.Single(filtered.Tasks).Id);

        var byContext = await client.ListTasksAsync(new ListTasksRequest { ContextId = input.ContextId });
        Assert.Single(byContext.Tasks);

        var bad = await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest { PageToken = "garbage!" }));
        Assert.Equal(A2AErrorCode.InvalidParams, bad.Code);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Push_config_crud(string binding)
    {
        var client = _host.Client(binding, "carol");
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://hooks.example/cb", Token = "t1" });
        Assert.False(string.IsNullOrEmpty(created.Id));

        var got = await client.GetTaskPushNotificationConfigAsync(new GetTaskPushNotificationConfigRequest { TaskId = task.Id, Id = created.Id! });
        Assert.Equal("https://hooks.example/cb", got.Url);
        Assert.Single((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);

        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = task.Id, Id = created.Id! });
        Assert.Empty((await client.ListTaskPushNotificationConfigsAsync(new ListTaskPushNotificationConfigsRequest { TaskId = task.Id })).Configs);

        var other = _host.Client(binding, "mallory");
        await Assert.ThrowsAsync<A2AException>(() => other.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://evil.example/cb" }));
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Push_notifications_delivered_in_order_with_token_auth_and_sequence(string binding)
    {
        var client = _host.Client(binding);
        var config = new SendMessageConfiguration
        {
            TaskPushNotificationConfig = new TaskPushNotificationConfig
            {
                Url = $"http://hooks.test/{binding}", Token = "secret-token",
                Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = "hook-cred" },
            },
        };
        var task = (await client.SendMessageAsync(Message.FromUser("task"), config)).RequireTask();
        await _host.WaitIdleAsync();

        var calls = _host.Webhooks.Calls.Where(c => c.Request.RequestUri!.AbsolutePath == $"/{binding}").ToList();
        Assert.Equal(5, calls.Count);
        Assert.All(calls, c =>
        {
            Assert.Equal("secret-token", c.Request.Headers.GetValues(A2AProtocol.Headers.NotificationToken).Single());
            Assert.Equal("Bearer hook-cred", c.Request.Headers.Authorization!.ToString());
        });
        var seqs = calls.Select(c => long.Parse(c.Request.Headers.GetValues(A2AProtocol.Headers.NotificationSequence).Single())).ToList();
        Assert.Equal(seqs.OrderBy(s => s), seqs);
        var first = Json.A2AJson.Deserialize<StreamResponse>(calls[0].Body);
        Assert.Equal(task.Id, Assert.IsType<TaskEvent>(first).Task.Id);
        var last = Json.A2AJson.Deserialize<StreamResponse>(calls[^1].Body);
        Assert.Equal(TaskState.Completed, Assert.IsType<StatusUpdateEvent>(last).StatusUpdate.Status.State);
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Extended_card_requires_authentication(string binding)
    {
        var anon = await Assert.ThrowsAsync<A2AException>(() => _host.Client(binding, user: null).GetExtendedAgentCardAsync(new GetExtendedAgentCardRequest()));
        Assert.Equal(A2AErrorCode.Unauthenticated, anon.Code);
        Assert.Equal(401, anon.HttpStatus);

        var card = await _host.Client(binding, "dave").RefreshWithExtendedAgentCardAsync();
        Assert.Equal("Extended card for dave", card.Description);
    }

    [Fact]
    public async Task Rest_errors_use_google_rpc_status_with_errorinfo()
    {
        var http = _host.Http("alice");
        using var req = new HttpRequestMessage(HttpMethod.Get, "/a2a/tasks/does-not-exist");
        req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
        var resp = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(404, err.GetProperty("code").GetInt32());
        Assert.Equal("NOT_FOUND", err.GetProperty("status").GetString());
        var info = err.GetProperty("details")[0];
        Assert.Equal("type.googleapis.com/google.rpc.ErrorInfo", info.GetProperty("@type").GetString());
        Assert.Equal("TASK_NOT_FOUND", info.GetProperty("reason").GetString());
        Assert.Equal("a2a-protocol.org", info.GetProperty("domain").GetString());
    }

    [Fact]
    public async Task JsonRpc_protocol_errors()
    {
        var http = _host.Http("alice");
        async Task<JsonElement> Post(string body)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
            var resp = await http.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        Assert.Equal(-32700, (await Post("{not json")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32600, (await Post("{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"GetTask\"}")).GetProperty("error").GetProperty("code").GetInt32());
        var nf = await Post("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"Nope\"}");
        Assert.Equal(-32601, nf.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("abc", nf.GetProperty("id").GetString());
        Assert.Equal(-32602, (await Post("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"SendMessage\",\"params\":{\"message\":{\"messageId\":\"m\",\"role\":\"ROLE_USER\",\"parts\":[]}}}")).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32001, (await Post("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"GetTask\",\"params\":{\"id\":\"x\"}}")).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Missing_version_header_is_treated_as_0_3_and_rejected()
    {
        var http = _host.Http("alice");
        var resp = await http.PostAsync("/a2a", new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"GetTask\",\"params\":{\"id\":\"x\"}}", Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(-32009, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Activated_extensions_are_echoed(string binding)
    {
        var http = _host.Http("alice");
        HttpRequestMessage req = binding == ProtocolBindings.JsonRpc
            ? new(HttpMethod.Post, "/a2a") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SendMessage\",\"params\":{\"message\":{\"messageId\":\"m\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"hello\"}]}}}", Encoding.UTF8, "application/json") }
            : new(HttpMethod.Post, "/a2a/message:send") { Content = new StringContent("{\"message\":{\"messageId\":\"m\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"hello\"}]}}", Encoding.UTF8, "application/a2a+json") };
        req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
        req.Headers.Add(A2AProtocol.Headers.Extensions, $"{TestAgentHost.OptionalExtension}, urn:unknown");
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        Assert.Equal(TestAgentHost.OptionalExtension, resp.Headers.GetValues(A2AProtocol.Headers.Extensions).Single());
    }

    private static HttpRequestMessage SendRequest(string binding, bool stream)
    {
        const string rest = "{\"message\":{\"messageId\":\"m\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"hello\"}]}}";
        var method = stream ? "SendStreamingMessage" : "SendMessage";
        var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent($"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"{method}\",\"params\":{rest}}}", Encoding.UTF8, "application/json") }
            : new HttpRequestMessage(HttpMethod.Post, stream ? "/a2a/message:stream" : "/a2a/message:send") { Content = new StringContent(rest, Encoding.UTF8, "application/a2a+json") };
        req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
        return req;
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Repeated_extension_header_lines_are_read_like_a_comma_list(string binding)
    {
        var http = _host.Http("alice");
        using var req = SendRequest(binding, stream: false);
        // Two header lines (two values), one of them unknown.
        req.Headers.Add(A2AProtocol.Headers.Extensions, TestAgentHost.OptionalExtension);
        req.Headers.Add(A2AProtocol.Headers.Extensions, "urn:unknown");
        var resp = await http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        Assert.Equal(TestAgentHost.OptionalExtension, resp.Headers.GetValues(A2AProtocol.Headers.Extensions).Single());
    }

    [Theory]
    [MemberData(nameof(Bindings))]
    public async Task Streaming_response_carries_the_extensions_header(string binding)
    {
        var http = _host.Http("alice");
        using var req = SendRequest(binding, stream: true);
        req.Headers.Add(A2AProtocol.Headers.Extensions, $"{TestAgentHost.OptionalExtension}, urn:unknown");
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TestAgentHost.OptionalExtension, resp.Headers.GetValues(A2AProtocol.Headers.Extensions).Single());
    }

    [Fact]
    public async Task Rest_subscribe_accepts_get_as_in_proto()
    {
        var client = _host.Client(ProtocolBindings.HttpJson, options: new A2AClientOptions { RestSubscribeMethod = HttpMethod.Get });
        var task = (await client.SendMessageAsync("input")).RequireTask();
        await using var e = client.SubscribeToTaskAsync(task.Id).GetAsyncEnumerator();
        Assert.True(await e.MoveNextAsync());
        Assert.Equal(task.Id, Assert.IsType<TaskEvent>(e.Current).Task.Id);
    }

    [Fact]
    public async Task Rest_tenant_prefix_scopes_tasks()
    {
        var acme = _host.Client(ProtocolBindings.HttpJson, options: new A2AClientOptions { Tenant = "acme" });
        var task = (await acme.SendMessageAsync("task")).RequireTask();
        Assert.Equal(task.Id, (await acme.GetTaskAsync(task.Id)).Id);
        var noTenant = _host.Client(ProtocolBindings.HttpJson);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => noTenant.GetTaskAsync(task.Id))).Code);
    }
}

public class HostConfigurationTests
{
    [Fact]
    public async Task Required_extension_must_be_declared_by_client()
    {
        await using var host = await TestAgentHost.StartAsync(o =>
            o.AgentCard.Capabilities.Extensions = [new AgentExtension { Uri = "urn:must", Required = true }]);
        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("hello"));
        Assert.Equal(A2AErrorCode.ExtensionSupportRequired, ex.Code);

        var ok = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { Extensions = ["urn:must"] });
        Assert.IsType<MessageResponse>(await ok.SendMessageAsync("hello"));
    }

    [Fact]
    public async Task Push_ssrf_guard_rejects_private_targets()
    {
        await using var host = await TestAgentHost.StartAsync(o => o.PushNotifications.AllowPrivateNetworks = false);
        var client = host.Client(ProtocolBindings.JsonRpc);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var ex = await Assert.ThrowsAsync<A2AException>(() =>
            client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "http://169.254.169.254/latest/meta-data" }));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Capabilities_not_declared_are_refused_by_server()
    {
        await using var host = await TestAgentHost.StartAsync(o =>
        {
            o.AgentCard.Capabilities.Streaming = false;
            o.AgentCard.Capabilities.PushNotifications = false;
        });
        var client = host.Client(ProtocolBindings.JsonRpc, options: new A2AClientOptions { ValidateCapabilities = false });
        var stream = await Assert.ThrowsAsync<A2AException>(async () => { await foreach (var _ in client.SendStreamingMessageAsync(Message.FromUser("task"))) { } });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, stream.Code);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        var push = await Assert.ThrowsAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig { TaskId = task.Id, Url = "https://x.example/h" }));
        Assert.Equal(A2AErrorCode.PushNotificationNotSupported, push.Code);
    }

    [Fact]
    public async Task RefusePrunedCardValues_refuses_to_sign_a_card_the_signature_would_not_fully_cover()
    {
        // A security requirement with no scopes is pruned by the canonicalization, so the signature would not cover it.
        var key = new AgentCardSigningKey(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), "card-key", "ES256");
        await using var host = await TestAgentHost.StartAsync(
            o =>
            {
                o.AgentCard.SecuritySchemes = new() { ["bearer"] = SecurityScheme.Bearer() };
                o.AgentCard.SecurityRequirements = [new SecurityRequirement { Schemes = { ["bearer"] = new StringList() } }];
            },
            configureHttp: h => { h.SigningKey = key; h.RefusePrunedCardValues = true; });

        // The test server hands the endpoint's exception to the caller; a real server answers 500 and logs it.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Http().GetAsync(A2AProtocol.WellKnownAgentCardPath));
        Assert.Contains("RefusePrunedCardValues", refused.Message);
        Assert.Contains("$.securityRequirements", refused.Message);
    }

    [Fact]
    public async Task Agent_card_is_cacheable_signed_and_verifiable_via_jwks()
    {
        var key = new AgentCardSigningKey(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256),
            "card-key", "ES256", "http://localhost/.well-known/jwks.json");
        await using var host = await TestAgentHost.StartAsync(configureHttp: h => h.SigningKey = key);
        var http = host.Http();

        var resp = await http.GetAsync(A2AProtocol.WellKnownAgentCardPath);
        resp.EnsureSuccessStatusCode();
        var etag = resp.Headers.ETag!;
        Assert.NotNull(resp.Headers.CacheControl!.MaxAge);
        using var conditional = new HttpRequestMessage(HttpMethod.Get, A2AProtocol.WellKnownAgentCardPath);
        conditional.Headers.IfNoneMatch.Add(etag);
        Assert.Equal(HttpStatusCode.NotModified, (await http.SendAsync(conditional)).StatusCode);

        // The jku here is http and not allowlisted, so it is ignored (spec §7.5); pin the published key for the origin instead.
        var jwks = await http.GetFromJsonAsync("/.well-known/jwks.json", A2AJson.GetTypeInfo<JwkSet>());
        var trusted = new A2ACardResolverOptions();
        trusted.Trust.TrustedKeys["http://localhost"] = jwks!;
        var resolver = new A2ACardResolver(http, trusted);
        var resolved = await resolver.ResolveAsync(new Uri("http://localhost/"));
        Assert.Equal(AgentCardVerificationStatus.Verified, resolved.Verification.Status);
        Assert.Equal(new PeerIdentity("http://localhost", "card-key"), resolved.Identity);

        // A different trusted key with the same kid must not validate the card.
        var wrong = new A2ACardResolverOptions();
        wrong.Trust.TrustedKeys["http://localhost"] = new JwkSet { Keys = [Jwk.FromPublicKey(System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), "card-key", "ES256")] };
        var strict = new A2ACardResolver(http, wrong);
        await Assert.ThrowsAsync<AgentCardVerificationException>(() => strict.ResolveAsync(new Uri("http://localhost/")));
    }

    [Fact]
    public async Task Factory_resolves_card_and_selects_first_supported_interface()
    {
        await using var host = await TestAgentHost.StartAsync();
        var client = await A2AClientFactory.CreateAsync(new Uri("http://localhost/"), host.Http("alice"));
        Assert.Equal("Scenario Agent", client.AgentCard!.Name);
        Assert.Equal(TaskState.Completed, (await client.SendMessageAsync("task")).RequireTask().Status.State);

        var restOnly = A2AClientFactory.SelectInterface(client.AgentCard, new A2AClientOptions { SupportedBindings = [ProtocolBindings.HttpJson] });
        Assert.Equal(ProtocolBindings.HttpJson, restOnly!.ProtocolBinding);
        Assert.Null(A2AClientFactory.SelectInterface(client.AgentCard, new A2AClientOptions { ProtocolVersion = "2.0" }));
    }
}
