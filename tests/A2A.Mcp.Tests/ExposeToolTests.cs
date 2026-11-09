using System.Text.Json;
using A2A.Client;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace A2A.Mcp.Tests;

public sealed class ExposeToolTests
{
    private static string[] Names(IEnumerable<McpServerTool> tools) => tools.Select(t => t.ProtocolTool.Name).ToArray();

    private static JsonElement Body(CallToolResult r) => r.StructuredContent!.Value;
    private static string State(CallToolResult r) => Body(r).GetProperty("state").GetString()!;
    private static string Content(CallToolResult r) => Body(r).GetProperty("remoteContent").GetString()!;
    private static string ErrorText(CallToolResult r) => ((TextContentBlock)r.Content[0]).Text;

    private static Dictionary<string, object?> Msg(string text, string? taskId = null, string? contextId = null)
    {
        var d = new Dictionary<string, object?> { ["message"] = text };
        if (taskId is not null) d["taskId"] = taskId;
        if (contextId is not null) d["contextId"] = contextId;
        return d;
    }

    private static Task<CallToolResult> Call(McpPair pair, string tool, Dictionary<string, object?> args, IProgress<ProgressNotificationValue>? progress = null, CancellationToken ct = default) =>
        pair.Client.CallToolAsync(tool, args, progress, cancellationToken: ct).AsTask();

    private static async Task<(McpPair Pair, FakeTransport Fake)> Start(Action<FakeTransport> script, Action<A2AMcpToolOptions>? configure = null, params string[] skills)
    {
        var fake = new FakeTransport();
        script(fake);
        var options = new A2AMcpToolOptions();
        configure?.Invoke(options);
        var tools = fake.AsClient(Cards.With(skills.Length == 0 ? ["echo"] : skills)).AsMcpTools(options);
        return (await McpPair.StartAsync(tools), fake);
    }

    // ---- names ----

    [Fact]
    public void Names_are_sanitized_and_collisions_get_a_suffix()
    {
        var client = new FakeTransport().AsClient(Cards.With("a b", "a_b", "x/y", "a b"));
        Assert.Equal(["a_b", "a_b_2", "x_y", "a_b_3", "get_task", "cancel_task"], Names(client.AsMcpTools()));
    }

    [Fact]
    public void A_skill_named_like_a_task_tool_gets_a_suffix()
    {
        var client = new FakeTransport().AsClient(Cards.With("get_task"));
        Assert.Equal(["get_task_2", "get_task", "cancel_task"], Names(client.AsMcpTools()));
    }

    [Fact]
    public void Prefix_applies_to_every_tool_and_names_stop_at_128_characters()
    {
        var client = new FakeTransport().AsClient(Cards.With(new string('s', 300), new string('s', 300)));
        var names = Names(client.AsMcpTools(new A2AMcpToolOptions { ToolNamePrefix = "pre_" }));
        Assert.All(names, n => { Assert.StartsWith("pre_", n); Assert.True(n.Length <= 128); });
        Assert.Equal(128, names[0].Length);
        Assert.EndsWith("_2", names[1]);
        Assert.Equal(128, names[1].Length);
        Assert.Equal(["pre_get_task", "pre_cancel_task"], names[2..]);
    }

    [Fact]
    public void Filter_empty_ids_and_task_tool_switch()
    {
        var card = Cards.With("keep", "drop", "");
        var client = new FakeTransport().AsClient(card);
        var tools = client.AsMcpTools(new A2AMcpToolOptions { SkillFilter = s => s.Id != "drop", IncludeTaskTools = false });
        Assert.Equal(["keep"], Names(tools));
    }

    [Fact]
    public void Without_a_card_it_throws_and_a_card_argument_is_used()
    {
        var client = new A2AClient(new FakeTransport());
        Assert.Throws<InvalidOperationException>(() => client.AsMcpTools());
        Assert.Contains("only", Names(client.AsMcpTools(card: Cards.With("only"))));
    }

    [Fact]
    public void Options_are_checked()
    {
        var o = new A2AMcpToolOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => o.PollInterval = TimeSpan.FromMilliseconds(99));
        Assert.Throws<ArgumentOutOfRangeException>(() => o.MaxResultCharacters = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.MaxBinaryBytes = -1);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.MaxTrackedTasks = 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => o.WaitTimeout = TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => o.ToolNamePrefix = "bad prefix");
        o.WaitTimeout = Timeout.InfiniteTimeSpan;
        o.MaxBinaryBytes = 0;
    }

    // ---- listing ----

    [Fact]
    public async Task Description_is_fixed_text_unless_the_host_opts_in()
    {
        var (pair, _) = await Start(_ => { });
        await using (pair)
        {
            var tool = (await pair.Client.ListToolsAsync()).Single(t => t.Name == "echo");
            Assert.Equal(A2AMcpToolOptions.DefaultSkillDescription, tool.Description);
            Assert.DoesNotContain("REMOTE", tool.Description);
        }

        var (trusting, _) = await Start(_ => { }, o => o.SkillDescription = s => s.Description);
        await using (trusting)
        {
            var tool = (await trusting.Client.ListToolsAsync()).Single(t => t.Name == "echo");
            Assert.Equal("REMOTE DESCRIPTION echo", tool.Description);
        }
    }

    [Fact]
    public async Task Schemas_and_annotations()
    {
        var (pair, _) = await Start(_ => { });
        await using var _pair = pair;
        var tools = await pair.Client.ListToolsAsync();
        var skill = tools.Single(t => t.Name == "echo").ProtocolTool;
        Assert.Equal(["message", "contextId", "taskId"], skill.InputSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name));
        Assert.Equal(["message"], skill.InputSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(skill.InputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.NotNull(skill.OutputSchema);
        Assert.True(skill.OutputSchema!.Value.GetProperty("properties").TryGetProperty("remoteContent", out _));
        Assert.True(skill.Annotations!.OpenWorldHint);
        Assert.Null(skill.Annotations.ReadOnlyHint);
        Assert.Null(skill.Annotations.DestructiveHint);
        Assert.Null(skill.Annotations.IdempotentHint);
        Assert.Equal(["echo", "get_task", "cancel_task"], tools.Select(t => t.Name));
    }

    // ---- states ----

    [Fact]
    public async Task Completed_task_returns_artifacts_and_the_same_object_twice()
    {
        var (pair, fake) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(
            FakeTransport.TaskIn(TaskState.Completed, artifacts: [FakeTransport.TextArtifact("a1", "hello"), FakeTransport.TextArtifact("a2", "world")]))));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("hi"));
        Assert.False(result.IsError);
        Assert.Equal("completed", State(result));
        Assert.Equal("hello\n\nworld", Content(result));
        Assert.Equal("t1", Body(result).GetProperty("taskId").GetString());
        Assert.Equal("c1", Body(result).GetProperty("contextId").GetString());
        Assert.False(Body(result).GetProperty("truncated").GetBoolean());
        Assert.Equal(0, Body(result).GetProperty("omittedParts").GetInt32());
        Assert.Equal(A2AMcpToolOptions.DefaultUntrustedContentNotice, Body(result).GetProperty("notice").GetString());
        // Content[0] is the same object as text.
        using var doc = JsonDocument.Parse(((TextContentBlock)result.Content[0]).Text);
        Assert.Equal(Body(result).GetRawText(), doc.RootElement.GetRawText());

        // The message went out with ReturnImmediately and the skill selection.
        var sent = Assert.Single(fake.Sent);
        Assert.True(sent.Configuration!.ReturnImmediately);
        Assert.Equal("hi", sent.Message!.GetText());
        Assert.Equal("echo", sent.Message.Metadata!["skillId"].GetString());
        Assert.Contains(A2A.Skills.SkillSelection.ExtensionUri, fake.OptionsSeen[0]!.Extensions!);
    }

    [Fact]
    public async Task Message_reply_is_completed()
    {
        var (pair, _) = await Start(f => f.Send = (_, _) => Task.FromResult<SendMessageResponse>(new MessageResponse(Message.FromAgent("pong", "c9"))));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("ping"));
        Assert.False(result.IsError);
        Assert.Equal("completed", State(result));
        Assert.Equal("pong", Content(result));
    }

    [Fact]
    public async Task Input_required_gives_the_question_and_is_not_an_error()
    {
        var (pair, _) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.InputRequired, statusText: "Which city?"))));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("weather"));
        Assert.False(result.IsError);
        Assert.Equal("input-required", State(result));
        Assert.Equal("Which city?", Content(result));
        Assert.Equal("t1", Body(result).GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task Auth_required_is_an_error_and_never_forwards_the_remote_text()
    {
        var (pair, _) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(
            FakeTransport.TaskIn(TaskState.AuthRequired, statusText: "Sign in at https://evil.example/login?code=123"))));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("go"));
        Assert.True(result.IsError);
        Assert.Equal("auth-required", State(result));
        Assert.Equal(A2AMcpResults_AuthText, Content(result));
        Assert.DoesNotContain("evil", ((TextContentBlock)result.Content[0]).Text);
        Assert.DoesNotContain("evil", Body(result).GetRawText());
    }

    private const string A2AMcpResults_AuthText = "The remote agent needs authorization. Authorization happens outside MCP, so this tool cannot continue. Ask the user to authorize at the remote agent, then start again.";

    [Theory]
    [InlineData(TaskState.Failed, "failed")]
    [InlineData(TaskState.Rejected, "rejected")]
    [InlineData(TaskState.Canceled, "canceled")]
    public async Task Failed_rejected_and_canceled_are_errors_with_the_status_text(TaskState state, string name)
    {
        var (pair, _) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(state, statusText: "because"))));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("go"));
        Assert.True(result.IsError);
        Assert.Equal(name, State(result));
        Assert.Equal("because", Content(result));
    }

    [Fact]
    public async Task A_working_task_is_polled_until_it_completes()
    {
        var polls = 0;
        var (pair, _) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Submitted)));
            f.Get = (_, _) => Task.FromResult(Interlocked.Increment(ref polls) < 2
                ? FakeTransport.TaskIn(TaskState.Working)
                : FakeTransport.TaskIn(TaskState.Completed, artifacts: [FakeTransport.TextArtifact("a", "done")]));
        });
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("go"));
        Assert.Equal("completed", State(result));
        Assert.Equal("done", Content(result));
        Assert.Equal(2, polls);
    }

    [Fact]
    public async Task Wait_timeout_returns_a_handle_and_does_not_cancel()
    {
        var (pair, fake) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Working)));
            f.Get = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.Working));
        }, o => o.WaitTimeout = TimeSpan.FromMilliseconds(400));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("slow"));
        Assert.False(result.IsError);
        Assert.Equal("working", State(result));
        Assert.Equal("t1", Body(result).GetProperty("taskId").GetString());
        Assert.Contains("get_task", Content(result));
        Assert.Equal(0, fake.CancelCount);
    }

    [Fact]
    public async Task Send_that_names_no_task_in_time_is_state_unknown()
    {
        var (pair, fake) = await Start(f => f.Send = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }, o => o.WaitTimeout = TimeSpan.FromMilliseconds(300));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("hang"));
        Assert.True(result.IsError);
        Assert.Equal("unknown", State(result));
        Assert.False(Body(result).TryGetProperty("taskId", out _));
        Assert.Equal(0, fake.CancelCount);
    }

    [Fact]
    public async Task Caller_cancel_cancels_the_remote_task()
    {
        var polled = new TaskCompletionSource();
        var canceled = new TaskCompletionSource<string>();
        var (pair, _) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Working)));
            f.Get = (_, _) => { polled.TrySetResult(); return Task.FromResult(FakeTransport.TaskIn(TaskState.Working)); };
            f.Cancel = (r, _) => { canceled.TrySetResult(r.Id); return Task.FromResult(FakeTransport.TaskIn(TaskState.Canceled)); };
        });
        await using var _pair = pair;
        using var cts = new CancellationTokenSource();
        var call = Call(pair, "echo", Msg("long"), ct: cts.Token);
        await polled.Task.Within();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal("t1", await canceled.Task.Within());
    }

    [Fact]
    public async Task Progress_is_sent_when_state_or_status_text_changes()
    {
        var polls = 0;
        var (pair, _) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Working)));
            f.Get = (_, _) => Task.FromResult(Interlocked.Increment(ref polls) switch
            {
                1 => FakeTransport.TaskIn(TaskState.Working),                       // no change
                2 => FakeTransport.TaskIn(TaskState.Working, statusText: "step 1"), // text changed
                _ => FakeTransport.TaskIn(TaskState.Completed, artifacts: [FakeTransport.TextArtifact("a", "x")]),
            });
        });
        await using var _pair = pair;
        var seen = new List<ProgressNotificationValue>();
        var third = new TaskCompletionSource();
        var progress = new SyncProgress(v => { lock (seen) { seen.Add(v); if (seen.Count == 2) third.TrySetResult(); } });
        var result = await Call(pair, "echo", Msg("go"), progress);
        Assert.Equal("completed", State(result));
        await third.Task.Within();
        lock (seen)
        {
            // The final state is the result, not progress.
            Assert.Equal([1f, 2f], seen.Take(2).Select(p => p.Progress));
            Assert.All(seen, p => Assert.Null(p.Total));
            Assert.Equal(["working", "step 1"], seen.Take(2).Select(p => p.Message));
        }
    }

    private sealed class SyncProgress(Action<ProgressNotificationValue> report) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => report(value);
    }

    // ---- continuing, task tools ----

    [Fact]
    public async Task Only_new_artifacts_are_returned_when_a_task_is_continued()
    {
        var (pair, fake) = await Start(f =>
        {
            f.Send = (r, _) => Task.FromResult(FakeTransport.Respond(r.Message!.TaskId is null
                ? FakeTransport.TaskIn(TaskState.InputRequired, statusText: "more?", artifacts: [FakeTransport.TextArtifact("old", "old text")])
                : FakeTransport.TaskIn(TaskState.Completed, artifacts: [FakeTransport.TextArtifact("old", "old text"), FakeTransport.TextArtifact("new", "new text")])));
            f.Get = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.InputRequired, artifacts: [FakeTransport.TextArtifact("old", "old text")]));
        });
        await using var _pair = pair;
        await Call(pair, "echo", Msg("first"));
        var second = await Call(pair, "echo", Msg("yes", taskId: "t1", contextId: "c1"));
        Assert.Equal("new text", Content(second));
        Assert.Equal("t1", fake.Sent[1].Message!.TaskId);
        Assert.Equal("c1", fake.Sent[1].Message!.ContextId);
    }

    private static AgentTask Asking(string messageId, string text, List<Artifact>? artifacts = null)
    {
        var task = FakeTransport.TaskIn(TaskState.InputRequired, artifacts: artifacts);
        task.Status.Message = new Message { MessageId = messageId, Role = Role.Agent, Parts = [Part.FromText(text)] };
        return task;
    }

    [Fact]
    public async Task An_answer_is_not_met_with_the_question_it_answers()
    {
        var gets = 0;
        var (pair, _) = await Start(f =>
        {
            f.Send = (r, _) => Task.FromResult(FakeTransport.Respond(Asking("q1", "Which city?")));
            f.Get = (_, _) => Task.FromResult(Interlocked.Increment(ref gets) <= 2
                ? Asking("q1", "Which city?") // the lookup before the send, then one poll where the agent has not picked it up
                : FakeTransport.TaskIn(TaskState.Completed, artifacts: [FakeTransport.TextArtifact("a", "Oslo it is")]));
        }, o => o.PollInterval = TimeSpan.FromMilliseconds(100));
        await using var _pair = pair;
        await Call(pair, "echo", Msg("first"));

        var result = await Call(pair, "echo", Msg("Oslo", taskId: "t1", contextId: "c1"));

        Assert.Equal("Oslo it is", Content(result));
        Assert.Equal("completed", State(result));
    }

    [Fact]
    public async Task A_new_question_on_the_same_task_is_returned()
    {
        var (pair, _) = await Start(f =>
        {
            f.Send = (r, _) => Task.FromResult(FakeTransport.Respond(Asking(r.Message!.TaskId is null ? "q1" : "q2", r.Message.TaskId is null ? "Which city?" : "Which month?")));
            f.Get = (_, _) => Task.FromResult(Asking("q1", "Which city?"));
        });
        await using var _pair = pair;
        await Call(pair, "echo", Msg("first"));

        var result = await Call(pair, "echo", Msg("Oslo", taskId: "t1", contextId: "c1"));

        Assert.Equal("Which month?", Content(result));
        Assert.Equal("input-required", State(result));
    }

    [Fact]
    public async Task A_timeout_on_an_unanswered_question_reports_running_not_the_old_question()
    {
        var (pair, _) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(Asking("q1", "Which city?")));
            f.Get = (_, _) => Task.FromResult(Asking("q1", "Which city?"));
        }, o =>
        {
            o.PollInterval = TimeSpan.FromMilliseconds(100);
            o.WaitTimeout = TimeSpan.FromMilliseconds(400);
        });
        await using var _pair = pair;
        await Call(pair, "echo", Msg("first"));

        var result = await Call(pair, "echo", Msg("Oslo", taskId: "t1", contextId: "c1"));

        Assert.Equal("working", State(result));
        Assert.DoesNotContain("Which city?", Content(result));
        Assert.NotEqual(true, result.IsError);
    }

    [Fact]
    public async Task A_failed_poll_keeps_the_task_handle()
    {
        var (pair, _) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Working)));
            f.Get = (_, _) => throw new HttpRequestException("secret-host refused");
        }, o => o.PollInterval = TimeSpan.FromMilliseconds(100));
        await using var _pair = pair;

        var result = await Call(pair, "echo", Msg("go"));

        Assert.Equal("working", State(result));
        Assert.NotEqual(true, result.IsError);
        Assert.DoesNotContain("secret-host", Content(result));
        Assert.Equal("t1", result.StructuredContent!.Value.GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task Get_task_returns_all_artifacts_and_cancel_task_returns_the_object()
    {
        var (pair, fake) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.InputRequired, statusText: "q")));
            f.Get = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.Completed, artifacts: [FakeTransport.TextArtifact("a1", "one"), FakeTransport.TextArtifact("a2", "two")]));
            f.Cancel = (_, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.Canceled));
        });
        await using var _pair = pair;
        await Call(pair, "echo", Msg("start"));

        var got = await Call(pair, "get_task", new() { ["taskId"] = "t1" });
        Assert.False(got.IsError);
        Assert.Equal("one\n\ntwo", Content(got));

        var canceled = await Call(pair, "cancel_task", new() { ["taskId"] = "t1" });
        Assert.False(canceled.IsError); // the caller asked for it
        Assert.Equal("canceled", State(canceled));
        Assert.Equal(1, fake.CancelCount);
    }

    [Fact]
    public async Task Task_tools_need_a_task_id_and_message_is_required()
    {
        var (pair, fake) = await Start(_ => { });
        await using var _pair = pair;
        Assert.True((await Call(pair, "get_task", new())).IsError);
        Assert.True((await Call(pair, "echo", new())).IsError);
        Assert.True((await Call(pair, "echo", Msg("   "))).IsError);
        Assert.True((await Call(pair, "echo", new() { ["message"] = 5 })).IsError);
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task Request_options_come_from_the_callback_for_every_call()
    {
        var made = 0;
        var (pair, fake) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Completed))),
            o => o.CreateRequestOptions = _ =>
            {
                made++;
                var ro = new A2ARequestOptions();
                ro.Headers["X-Bridge"] = "yes";
                return ro;
            });
        await using var _pair = pair;
        await Call(pair, "echo", Msg("a"));
        await Call(pair, "echo", Msg("b"));
        Assert.Equal(2, made);
        Assert.NotSame(fake.OptionsSeen[0], fake.OptionsSeen[1]);
        Assert.All(fake.OptionsSeen, o => Assert.Equal("yes", o!.Headers["X-Bridge"]));
    }

    // ---- ownership ----

    private static async Task<(McpPair Alice, McpPair Bob, FakeTransport Fake)> TwoCallers(Action<A2AMcpToolOptions>? configure = null)
    {
        var fake = new FakeTransport();
        var n = 0;
        fake.Send = (_, _) =>
        {
            var i = Interlocked.Increment(ref n);
            return Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.InputRequired, id: "t" + i, contextId: "c" + i, statusText: "q")));
        };
        fake.Get = (r, _) => Task.FromResult(FakeTransport.TaskIn(TaskState.Completed, id: r.Id, artifacts: [FakeTransport.TextArtifact("a", "secret of " + r.Id)]));
        var options = new A2AMcpToolOptions { TaskOwner = r => r.Server.ClientInfo?.Name };
        configure?.Invoke(options);
        var tools = fake.AsClient(Cards.With("echo")).AsMcpTools(options);
        return (await McpPair.StartAsync(tools, "alice"), await McpPair.StartAsync(tools, "bob"), fake);
    }

    [Fact]
    public async Task Another_owner_is_refused_with_the_text_of_an_unknown_task()
    {
        var (alice, bob, fake) = await TwoCallers();
        await using var _a = alice;
        await using var _b = bob;
        await Call(alice, "echo", Msg("mine"));
        var sentBefore = fake.Sent.Count;
        var getsBefore = fake.GetCount;

        var unknown = await Call(bob, "get_task", new() { ["taskId"] = "nope" });
        Assert.True(unknown.IsError);
        Assert.Equal("Unknown or expired task.", ErrorText(unknown));

        var stolenGet = await Call(bob, "get_task", new() { ["taskId"] = "t1" });
        var stolenCancel = await Call(bob, "cancel_task", new() { ["taskId"] = "t1" });
        var stolenSendTask = await Call(bob, "echo", Msg("hi", taskId: "t1"));
        var stolenSendContext = await Call(bob, "echo", Msg("hi", contextId: "c1"));
        foreach (var r in new[] { stolenGet, stolenCancel, stolenSendTask, stolenSendContext })
        {
            Assert.True(r.IsError);
            Assert.Equal(ErrorText(unknown), ErrorText(r));
            Assert.Null(r.StructuredContent);
        }
        Assert.Equal(sentBefore, fake.Sent.Count);
        Assert.Equal(getsBefore, fake.GetCount);
        Assert.Equal(0, fake.CancelCount);

        // The owner still has access, to the task and to the context.
        Assert.Contains("secret of t1", Content(await Call(alice, "get_task", new() { ["taskId"] = "t1" })));
        Assert.False((await Call(alice, "echo", Msg("again", contextId: "c1"))).IsError);
    }

    [Fact]
    public async Task Oldest_ids_are_dropped_first()
    {
        var (alice, bob, _) = await TwoCallers(o => o.MaxTrackedTasks = 2);
        await using var _a = alice;
        await using var _b = bob;
        await Call(alice, "echo", Msg("one"));  // t1, c1
        await Call(alice, "echo", Msg("two"));  // t2, c2: t1 and c1 are dropped
        Assert.Equal("Unknown or expired task.", ErrorText(await Call(alice, "get_task", new() { ["taskId"] = "t1" })));
        Assert.False((await Call(alice, "get_task", new() { ["taskId"] = "t2" })).IsError);
    }

    [Fact]
    public async Task Ownership_check_can_be_turned_off()
    {
        var (alice, bob, _) = await TwoCallers(o => o.ValidateTaskOwnership = false);
        await using var _a = alice;
        await using var _b = bob;
        await Call(alice, "echo", Msg("mine"));
        Assert.Contains("secret of t1", Content(await Call(bob, "get_task", new() { ["taskId"] = "t1" })));
    }

    [Fact]
    public async Task Default_owner_without_identity_or_session_is_shared()
    {
        // In-memory pipes have no user and no session id: the owner is the empty string, as on stdio.
        var (alice, bob, _) = await TwoCallers(o => o.TaskOwner = null);
        await using var _a = alice;
        await using var _b = bob;
        await Call(alice, "echo", Msg("mine"));
        Assert.False((await Call(bob, "get_task", new() { ["taskId"] = "t1" })).IsError);
    }

    [Fact]
    public async Task Remote_task_not_found_reads_like_an_unknown_task()
    {
        var (pair, _) = await Start(f =>
        {
            f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.InputRequired)));
            f.Get = (r, _) => throw A2AException.TaskNotFound(r.Id);
        });
        await using var _pair = pair;
        await Call(pair, "echo", Msg("x"));
        Assert.Equal("Unknown or expired task.", ErrorText(await Call(pair, "get_task", new() { ["taskId"] = "t1" })));
    }

    // ---- content ----

    [Fact]
    public async Task Long_text_is_cut_and_marked()
    {
        var (pair, _) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(
            FakeTransport.TaskIn(TaskState.Completed, artifacts: [FakeTransport.TextArtifact("a", "0123456789")]))), o => o.MaxResultCharacters = 5);
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("x"));
        Assert.Equal("01234", Content(result));
        Assert.True(Body(result).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Binary_limit_counts_total_inline_bytes()
    {
        var png = new byte[600];
        var (pair, _) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Completed,
            artifacts: [FakeTransport.ArtifactOf("a", Part.FromBytes(png, "image/png"), Part.FromBytes(png, "image/png"))]))), o => o.MaxBinaryBytes = 1000);
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("x"));
        Assert.Equal(1, Body(result).GetProperty("omittedParts").GetInt32());
        Assert.Equal(2, result.Content.Count); // the JSON text and one image
        Assert.IsType<ImageContentBlock>(result.Content[1]);
    }

    [Fact]
    public async Task Parts_become_content_blocks()
    {
        var (pair, _) = await Start(f => f.Send = (_, _) => Task.FromResult(FakeTransport.Respond(FakeTransport.TaskIn(TaskState.Completed,
            artifacts: [FakeTransport.ArtifactOf("a",
                Part.FromText("caption"),
                Part.FromBytes(new byte[] { 1, 2 }, "image/png"),
                Part.FromBytes(new byte[] { 3 }, "audio/wav"),
                Part.FromBytes(new byte[] { 4, 5, 6 }, "application/pdf"),
                Part.FromBytes(new byte[] { 7 }, "bad media type!"),
                Part.FromUrl(new Uri("https://example.com/f.txt"), "text/plain", "f.txt"),
                Part.FromUrl(new Uri("file:///etc/passwd")),
                Part.FromData(JsonDocument.Parse("""{"k":1}""").RootElement.Clone()))]))));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("x"));
        Assert.Equal("caption\n{\"k\":1}", Content(result));
        Assert.Equal(1, Body(result).GetProperty("omittedParts").GetInt32()); // the file: link
        var blocks = result.Content.Skip(1).ToList();
        Assert.Equal(5, blocks.Count);
        Assert.Equal("image/png", Assert.IsType<ImageContentBlock>(blocks[0]).MimeType);
        Assert.Equal("audio/wav", Assert.IsType<AudioContentBlock>(blocks[1]).MimeType);
        var pdf = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(blocks[2]).Resource);
        Assert.Equal("a2a://part/3", pdf.Uri);
        Assert.Equal("application/pdf", pdf.MimeType);
        Assert.Equal(new byte[] { 4, 5, 6 }, pdf.DecodedData.ToArray());
        Assert.Equal("application/octet-stream", Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(blocks[3]).Resource).MimeType);
        var link = Assert.IsType<ResourceLinkBlock>(blocks[4]);
        Assert.Equal("https://example.com/f.txt", link.Uri);
        Assert.Equal("f.txt", link.Name);
        Assert.Equal("text/plain", link.MimeType);
    }

    // ---- errors ----

    [Fact]
    public async Task Errors_are_fixed_text_without_exception_details()
    {
        var kind = 0;
        var (pair, _) = await Start(f => f.Send = (_, _) => Interlocked.Increment(ref kind) switch
        {
            1 => throw new A2AException(A2AErrorCode.InvalidParams, "secret-token-xyz"),
            2 => throw new HttpRequestException("connect to 10.0.0.5 failed"),
            3 => throw new InvalidOperationException("password=hunter2"),
            _ => throw new A2AException(A2AErrorCode.TaskNotFound),
        });
        await using var _pair = pair;
        var a2a = await Call(pair, "echo", Msg("x"));
        var net = await Call(pair, "echo", Msg("x"));
        var other = await Call(pair, "echo", Msg("x"));
        var notFound = await Call(pair, "echo", Msg("x"));
        foreach (var r in new[] { a2a, net, other, notFound }) Assert.True(r.IsError);
        Assert.Contains("InvalidParams", ErrorText(a2a));
        Assert.DoesNotContain("secret-token-xyz", ErrorText(a2a));
        Assert.DoesNotContain("10.0.0.5", ErrorText(net));
        Assert.DoesNotContain("hunter2", ErrorText(other));
    }

    [Fact]
    public async Task Callback_that_throws_does_not_leak_its_message()
    {
        var (pair, _) = await Start(_ => { }, o => o.CreateRequestOptions = _ => throw new InvalidOperationException("internal-detail"));
        await using var _pair = pair;
        var result = await Call(pair, "echo", Msg("x"));
        Assert.True(result.IsError);
        Assert.DoesNotContain("internal-detail", ErrorText(result));
    }
}
