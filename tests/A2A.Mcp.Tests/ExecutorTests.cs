using System.Text.Json;
using A2A.Client;
using A2A.Server;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace A2A.Mcp.Tests;

public sealed class ExecutorTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static async Task<(AgentHost Host, McpPair Pair, A2AClient Client)> Start(ScriptedTool[] tools, Action<McpToolAgentOptions>? configure = null, LogSpy? logs = null)
    {
        var pair = await McpPair.StartAsync(tools);
        var mcpTools = await pair.Client.ListToolsAsync();
        var skills = McpToolSkills.ToAgentSkills(mcpTools);
        var host = await AgentHost.StartAsync(pair.Client, o =>
        {
            foreach (var t in tools) o.AllowTool(t.ProtocolTool.Name, t.ProtocolTool.Name == "text-tool" ? "text" : null);
            configure?.Invoke(o);
        }, skills, logs);
        return (host, pair, host.Client());
    }

    private static Task<AgentTask> Run(A2AClient client, Message message, string? skill = null)
    {
        var options = skill is null ? null : new A2ARequestOptions { Skill = skill };
        return SendAsync(client, message, options);
    }

    private static async Task<AgentTask> SendAsync(A2AClient client, Message message, A2ARequestOptions? options)
    {
        var response = await client.SendMessageAsync(message, null, options).Within();
        return Assert.IsType<TaskResponse>(response).Task;
    }

    private static Message Data(string json) => new() { MessageId = Message.NewId(), Role = Role.User, Parts = [Part.FromData(Json(json))] };
    private static Message Text(string text) => Message.FromUser(text);
    private static string StatusText(AgentTask t) => t.Status.Message?.GetText() ?? "";

    private static ScriptedTool Echo(string name = "echo") => new(name, (r, _) => ValueTask.FromResult(ScriptedTool.Text("ok")));

    // ---- selection and rejection ----

    [Fact]
    public async Task One_allowed_tool_is_the_default_and_data_becomes_the_arguments()
    {
        var tool = Echo();
        var (host, pair, client) = await Start([tool]);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("""{"a":1,"b":"x","c":{"d":[1,2]}}"""));
        Assert.Equal(TaskState.Completed, task.Status.State);
        var args = Assert.Single(tool.Calls)!;
        Assert.Equal(1, args["a"].GetInt32());
        Assert.Equal("x", args["b"].GetString());
        Assert.Equal("""{"d":[1,2]}""", args["c"].GetRawText());
    }

    [Fact]
    public async Task Skill_selection_picks_the_tool_and_default_tool_is_used_otherwise()
    {
        var one = Echo("one"); var two = Echo("two");
        var (host, pair, client) = await Start([one, two], o => o.DefaultTool = "two");
        await using var _h = host; await using var _p = pair;
        Assert.Equal(TaskState.Completed, (await Run(client, Data("{}"), "one")).Status.State);
        Assert.Single(one.Calls); Assert.Empty(two.Calls);
        Assert.Equal(TaskState.Completed, (await Run(client, Data("{}"))).Status.State);
        Assert.Single(two.Calls);
    }

    [Fact]
    public async Task Tool_that_is_not_allowed_is_rejected_without_echoing_its_name()
    {
        var allowed = Echo("allowed"); var other = Echo("secret-admin-tool");
        var pair = await McpPair.StartAsync([allowed, other]);
        await using var _p = pair;
        await using var host = await AgentHost.StartAsync(pair.Client, o => o.AllowTool("allowed"),
            McpToolSkills.ToAgentSkills(await pair.Client.ListToolsAsync()));
        var client = host.Client();
        var task = await Run(client, Data("{}"), "secret-admin-tool");
        Assert.Equal(TaskState.Rejected, task.Status.State);
        Assert.Equal("The requested tool is not available.", StatusText(task));
        Assert.DoesNotContain("secret-admin-tool", StatusText(task));
        Assert.Empty(other.Calls);
    }

    [Fact]
    public async Task No_selection_and_no_default_with_several_tools_is_rejected()
    {
        var one = Echo("one"); var two = Echo("two");
        var (host, pair, client) = await Start([one, two]);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Rejected, task.Status.State);
        Assert.Empty(one.Calls); Assert.Empty(two.Calls);
    }

    // ---- arguments ----

    [Fact]
    public async Task Text_goes_to_the_text_argument_and_other_tools_refuse_text()
    {
        var textTool = new ScriptedTool("text-tool", (_, _) => ValueTask.FromResult(ScriptedTool.Text("ok")));
        var plain = Echo("plain");
        var (host, pair, client) = await Start([textTool, plain]);
        await using var _h = host; await using var _p = pair;

        var ok = await Run(client, Text("hello there"), "text-tool");
        Assert.Equal(TaskState.Completed, ok.Status.State);
        Assert.Equal("hello there", Assert.Single(textTool.Calls)!["text"].GetString());

        var refused = await Run(client, Text("hello"), "plain");
        Assert.Equal(TaskState.Rejected, refused.Status.State);
        Assert.Empty(plain.Calls);
    }

    [Fact]
    public async Task Other_mixes_are_rejected_and_the_tool_does_not_run()
    {
        var tool = new ScriptedTool("text-tool", (_, _) => ValueTask.FromResult(ScriptedTool.Text("ok")));
        var (host, pair, client) = await Start([tool]);
        await using var _h = host; await using var _p = pair;
        Message M(params Part[] parts) => new() { MessageId = Message.NewId(), Role = Role.User, Parts = parts.ToList() };
        var cases = new[]
        {
            M(Part.FromText("a"), Part.FromData(Json("{}"))),
            M(Part.FromData(Json("{}")), Part.FromData(Json("{}"))),
            M(Part.FromData(Json("[1]"))),
            M(Part.FromData(Json("5"))),
            M(Part.FromText("a"), Part.FromBytes(new byte[] { 1 }, "image/png")),
            M(Part.FromText("a"), Part.FromUrl(new Uri("https://example.com/x"))),
            M(Part.FromBytes(new byte[] { 1 }, "image/png")),
            M(Part.FromUrl(new Uri("https://example.com/x"))),
        };
        foreach (var message in cases)
        {
            var task = await Run(client, message);
            Assert.Equal(TaskState.Rejected, task.Status.State);
            Assert.Equal("The message must be one JSON data part with an object, or plain text for a tool that takes text.", StatusText(task));
        }
        Assert.Empty(tool.Calls);
    }

    [Fact]
    public async Task Construction_needs_at_least_one_tool()
    {
        var pair = await McpPair.StartAsync([Echo()]);
        await using var _p = pair;
        Assert.Throws<ArgumentException>(() => new McpToolAgentExecutor(pair.Client, new McpToolAgentOptions()));
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddA2AServer().AddMcpToolAgent(_ => { }));
        Assert.Throws<ArgumentException>(() => new McpToolAgentOptions().AllowTool(" "));
    }

    // ---- outcomes ----

    [Fact]
    public async Task Success_becomes_a_named_artifact_and_empty_content_completes_without_one()
    {
        var empty = false;
        var tool = new ScriptedTool("echo", (_, _) => ValueTask.FromResult(empty ? new CallToolResult { Content = [] } : ScriptedTool.Text("the answer")));
        var (host, pair, client) = await Start([tool], o => o.ArtifactName = "out");
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Completed, task.Status.State);
        var artifact = Assert.Single(task.Artifacts!);
        Assert.Equal("out", artifact.Name);
        Assert.Equal("the answer", Assert.IsType<TextPart>(Assert.Single(artifact.Parts)).Text);

        empty = true;
        var none = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Completed, none.Status.State);
        Assert.True(none.Artifacts is null || none.Artifacts.Count == 0);
    }

    [Fact]
    public async Task Tool_error_fails_with_fixed_text_and_logs_the_detail()
    {
        var logs = new LogSpy();
        var tool = new ScriptedTool("echo", (_, _) => ValueTask.FromResult(ScriptedTool.Failure("db password is hunter2")));
        var (host, pair, client) = await Start([tool], logs: logs);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal("The tool reported an error.", StatusText(task));
        Assert.Contains(logs.Entries, e => e.Text.Contains("hunter2"));
    }

    [Fact]
    public async Task Tool_error_text_is_copied_only_on_request()
    {
        var tool = new ScriptedTool("echo", (_, _) => ValueTask.FromResult(ScriptedTool.Failure("no such city")));
        var (host, pair, client) = await Start([tool], o => o.IncludeToolErrorText = true);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal("no such city", StatusText(task));
    }

    [Fact]
    public async Task Invalid_params_is_a_rejection()
    {
        var tool = new ScriptedTool("echo", (_, _) => throw new McpProtocolException("bad argument 'x' at /srv/secret", McpErrorCode.InvalidParams));
        var (host, pair, client) = await Start([tool]);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Rejected, task.Status.State);
        Assert.Equal("The tool rejected the arguments.", StatusText(task));
    }

    [Fact]
    public async Task Url_elicitation_fails_with_fixed_text_and_never_forwards_the_url()
    {
        var logs = new LogSpy();
        var tool = new ScriptedTool("echo", (_, _) => throw new UrlElicitationRequiredException("Authorize", [
            new ElicitRequestParams { Mode = "url", Url = "https://evil.example/authorize?code=1", ElicitationId = "e1", Message = "Authorize" }]));
        var (host, pair, client) = await Start([tool], logs: logs);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.DoesNotContain("evil.example", StatusText(task));
        Assert.DoesNotContain("evil.example", JsonSerializer.Serialize(task.Status.Message?.Parts.OfType<TextPart>().Select(p => p.Text)));
        Assert.DoesNotContain(logs.Entries, e => e.Text.Contains("evil.example"));
    }

    [Fact]
    public async Task Tool_timeout_fails_the_task_and_cancels_the_mcp_call()
    {
        var sawCancel = new TaskCompletionSource();
        var tool = new ScriptedTool("echo", async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancel.TrySetResult(); throw; }
            return ScriptedTool.Text("never");
        });
        var (host, pair, client) = await Start([tool], o => o.ToolTimeout = TimeSpan.FromMilliseconds(300));
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.Equal("The tool did not finish in time.", StatusText(task));
        await sawCancel.Task.Within();
    }

    [Fact]
    public async Task Cancel_from_the_caller_reaches_the_mcp_tool()
    {
        var started = new TaskCompletionSource();
        var sawCancel = new TaskCompletionSource();
        var tool = new ScriptedTool("echo", async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { sawCancel.TrySetResult(); throw; }
            return ScriptedTool.Text("never");
        });
        var (host, pair, client) = await Start([tool]);
        await using var _h = host; await using var _p = pair;
        var message = Data("{}");
        var response = await client.SendMessageAsync(message, new SendMessageConfiguration { ReturnImmediately = true }).Within();
        var id = Assert.IsType<TaskResponse>(response).Task.Id;
        await started.Task.Within();
        await client.CancelTaskAsync(id).Within();
        await sawCancel.Task.Within();
        await Waiting.Until(async () => (await client.GetTaskAsync(id)).Status.State == TaskState.Canceled);
    }

    [Fact]
    public async Task A_tool_that_throws_fails_the_task_without_the_exception_text()
    {
        var tool = new ScriptedTool("echo", (_, _) => throw new InvalidOperationException("stack detail at /srv/app/secret.cs"));
        var (host, pair, client) = await Start([tool]);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        Assert.Equal(TaskState.Failed, task.Status.State);
        Assert.DoesNotContain("secret.cs", StatusText(task));
    }

    // ---- progress ----

    [Fact]
    public async Task Progress_is_throttled_and_the_pump_stops_before_the_final_state()
    {
        var first = "first-progress";
        var release = new TaskCompletionSource();
        var tool = new ScriptedTool("echo", async (r, ct) =>
        {
            var token = r.Params!.ProgressToken!.Value;
            await r.Server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = 1, Message = first }, cancellationToken: ct);
            await release.Task.WaitAsync(ct);
            for (var i = 2; i <= 30; i++)
                await r.Server.NotifyProgressAsync(token, new ProgressNotificationValue { Progress = i, Message = "later-" + i }, cancellationToken: ct);
            return ScriptedTool.Text("done");
        });
        var logs = new LogSpy();
        // An hour between updates: after the first one, nothing else may be sent before the end.
        var (host, pair, client) = await Start([tool], o => o.ProgressInterval = TimeSpan.FromHours(1), logs);
        await using var _h = host; await using var _p = pair;

        var response = await client.SendMessageAsync(Data("{}"), new SendMessageConfiguration { ReturnImmediately = true }).Within();
        var id = Assert.IsType<TaskResponse>(response).Task.Id;
        await Waiting.Until(async () => (await client.GetTaskAsync(id)).Status.Message?.GetText() == first);
        release.SetResult();
        var done = await client.WaitForTaskAsync(id, TimeSpan.FromMilliseconds(100)).Within();
        Assert.Equal(TaskState.Completed, done.Status.State);

        var history = (await client.GetTaskAsync(id, 100)).History!;
        var texts = history.Select(m => m.GetText()).ToList();
        Assert.Equal(1, texts.Count(t => t == first));
        Assert.DoesNotContain(texts, t => t.StartsWith("later-", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Text.Contains("MCP", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Progress_right_before_the_end_does_not_collide_with_the_final_state()
    {
        var logs = new LogSpy();
        var tool = new ScriptedTool("echo", async (r, ct) =>
        {
            await r.Server.NotifyProgressAsync(r.Params!.ProgressToken!.Value, new ProgressNotificationValue { Progress = 1, Message = "almost" }, cancellationToken: ct);
            return ScriptedTool.Text("done");
        });
        var (host, pair, client) = await Start([tool], o => o.ProgressInterval = TimeSpan.FromMilliseconds(100), logs);
        await using var _h = host; await using var _p = pair;
        for (var i = 0; i < 5; i++)
            Assert.Equal(TaskState.Completed, (await Run(client, Data("{}"))).Status.State);
        Assert.DoesNotContain(logs.Entries, e => e.Text.Contains("MCP", StringComparison.Ordinal));
    }

    // ---- skills and DI ----

    [Fact]
    public async Task Tools_become_skills_with_fixed_descriptions()
    {
        var pair = await McpPair.StartAsync([Echo("alpha")]);
        await using var _p = pair;
        var tools = await pair.Client.ListToolsAsync();
        var skill = Assert.Single(McpToolSkills.ToAgentSkills(tools));
        Assert.Equal("alpha", skill.Id);
        Assert.Equal("alpha", skill.Name);
        Assert.Equal(McpToolSkills.DefaultDescription, skill.Description);
        Assert.Equal(["mcp"], skill.Tags);
        Assert.Equal(["application/json"], skill.InputModes);
        Assert.Equal("custom alpha", Assert.Single(McpToolSkills.ToAgentSkills(tools, t => "custom " + t.Name)).Description);
    }

    [Fact]
    public async Task Keyed_mcp_client_is_resolved_from_di()
    {
        var tool = Echo();
        var pair = await McpPair.StartAsync([tool]);
        await using var _p = pair;
        await using var host = await AgentHost.StartAsync(pair.Client, o => o.AllowTool("echo"), McpToolSkills.ToAgentSkills(await pair.Client.ListToolsAsync()), serviceKey: "mcp");
        var task = await Run(host.Client(), Data("{}"));
        Assert.Equal(TaskState.Completed, task.Status.State);
        Assert.Single(tool.Calls);
    }

    // ---- end to end through the Expose direction ----

    [Fact]
    public async Task Structured_content_becomes_one_data_part_end_to_end()
    {
        var tool = new ScriptedTool("echo", (_, _) => ValueTask.FromResult(new CallToolResult
        {
            Content = [new TextContentBlock { Text = """{ "n": 1 }""" }, ImageContentBlock.FromBytes(new byte[] { 1, 2 }, "image/png")],
            StructuredContent = Json("""{"n":1}"""),
        }));
        var (host, pair, client) = await Start([tool]);
        await using var _h = host; await using var _p = pair;
        var task = await Run(client, Data("{}"));
        var parts = Assert.Single(task.Artifacts!).Parts;
        Assert.Equal(2, parts.Count);
        Assert.Equal("""{"n":1}""", Assert.IsType<DataPart>(parts[0]).Data.GetRawText());
        Assert.Equal("image/png", Assert.IsType<RawPart>(parts[1]).MediaType);
    }
}
