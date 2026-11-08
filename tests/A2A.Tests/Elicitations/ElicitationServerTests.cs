using System.Collections.Concurrent;
using System.Text.Json;
using A2A.Client;
using A2A.Elicitations;
using A2A.Server;
using A2A.Server.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Elicitations;

public class ElicitationServerTests
{
    public static TheoryData<string> Bindings => BindingTests.Bindings;

    private const string Marker = "SECRET-MARKER-77";

    internal static ElicitationForm TripForm(string id = "f1") => new(
    [
        ElicitationField.Text("city", "City", required: true, maxLength: 100),
        ElicitationField.Integer("nights", required: true, minimum: 1, maximum: 30),
        ElicitationField.Choice("room", ["single", "double"]),
        ElicitationField.Boolean("late"),
    ], id);

    /// <summary>Asks for a trip form on the first message. Follow-ups: see the cases below.</summary>
    internal sealed class Agent : IAgentExecutor
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool? NewTaskCanElicit { get; private set; }
        public ConcurrentQueue<(ElicitationAnswer? Answer, string Text, bool CanElicit)> Seen { get; } = new();
        public ArgumentException? EmptyTextError { get; private set; }

        public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (context.Task is null)
            {
                NewTaskCanElicit = context.CanElicit;
                switch (context.UserText)
                {
                    case "plain-input": await updater.RequireInputAsync("text only", ct); return;
                    case "empty-text":
                        try { await updater.RequireInputAsync(context, TripForm(), "  ", ct); }
                        catch (ArgumentException e) { EmptyTextError = e; }
                        await updater.CompleteAsync("done", ct);
                        return;
                    default:
                        await updater.RequireInputAsync(context, TripForm(), "Which city, and how many nights?", ct);
                        return;
                }
            }

            var answer = context.ElicitationAnswer;
            Seen.Enqueue((answer, context.UserText, context.CanElicit));
            if (answer is null)
            {
                if (context.UserText == "stay") return; // no state change: the form stays pending
                if (context.UserText == "ask-again") await updater.RequireInputAsync(context, TripForm(), "Again?", ct);
                else await updater.CompleteAsync($"text: {context.UserText}", ct);
                return;
            }
            switch (answer.Action)
            {
                case ElicitationAction.Accept when answer.GetString("city") == "Again":
                    await updater.RequireInputAsync(context, TripForm(answer.FormId), "Once more?", ct); // a new status message, same form id
                    break;
                case ElicitationAction.Accept when answer.GetString("city") == "Stay":
                    break;
                case ElicitationAction.Accept:
                    await updater.CompleteAsync($"booked {answer.GetString("city")} {answer.GetInt64("nights")}", ct);
                    break;
                case ElicitationAction.Decline: await updater.FailAsync("declined", ct); break;
                default: await updater.FailAsync("cancelled", ct); break;
            }
        }
    }

    internal static async Task<(TestAgentHost Host, Agent Agent)> StartAsync(Action<A2AServerOptions>? configure = null, bool declare = true)
    {
        var agent = new Agent();
        var host = await TestAgentHost.StartAsync(o =>
        {
            if (declare) o.AgentCard.Capabilities.Extensions = [Elicitation.CreateExtension()];
            configure?.Invoke(o);
        }, services: s => s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent)));
        return (host, agent);
    }

    internal static A2AClient ClientFor(TestAgentHost host, string binding, A2AClientOptions? options = null) =>
        host.Client(binding, "alice", options ?? new A2AClientOptions { ValidateCapabilities = false });

    internal static A2ARequestOptions Activated() => new A2ARequestOptions().UseElicitation();

    private static async Task<AgentTask> AskAsync(A2AClient client, string text = "book", bool activate = true) =>
        (await client.SendMessageAsync(Message.FromUser(text), options: activate ? Activated() : null)).RequireTask();

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Message Raw(AgentTask task, string answerJson, string text = "x") =>
        new()
        {
            MessageId = Guid.NewGuid().ToString(), Role = Role.User, TaskId = task.Id, Parts = [Part.FromText(text)],
            Metadata = new() { [Elicitation.MetadataKey] = Json(answerJson) },
        };

    private static ElicitationAnswer Good(AgentTask task, string city = "Oslo", long nights = 2) =>
        ElicitationAnswer.Accept(task.GetElicitationForm()!, new ElicitationValues { { "city", city }, { "nights", nights } });

    // ---- round trips ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task Accept_round_trip(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);
        var task = await AskAsync(client);

        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal("Which city, and how many nights?", task.Status.Message!.GetText());
        var form = task.GetElicitationForm();
        Assert.NotNull(form);
        Assert.Equal("f1", form.Id);
        Assert.Equal(["city", "nights", "room", "late"], form.Fields.Select(f => f.Name).ToArray());

        var done = (await client.AnswerElicitationAsync(task, Good(task, "Oslo", 2))).RequireTask();
        Assert.Equal(TaskState.Completed, done.Status.State);
        Assert.Equal("booked Oslo 2", done.Status.Message!.GetText());
        var seen = Assert.Single(agent.Seen);
        Assert.Equal(ElicitationAction.Accept, seen.Answer!.Action);
        Assert.Equal("f1", seen.Answer.FormId);
        Assert.Equal(2L, seen.Answer.GetInt64("nights"));
        Assert.True(seen.CanElicit);

        var stored = await client.GetTaskAsync(task.Id);
        Assert.Equal(task.Status.Message.MessageId, stored.Metadata![Elicitation.AnsweredKey].GetString());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Decline_and_cancel_reach_the_agent_without_content(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);

        var declined = await AskAsync(client);
        var done = (await client.AnswerElicitationAsync(declined, ElicitationAnswer.Decline(declined.GetElicitationForm()!))).RequireTask();
        Assert.Equal("declined", done.Status.Message!.GetText());
        Assert.Equal(TaskState.Failed, done.Status.State);

        var cancelled = await AskAsync(client);
        done = (await client.AnswerElicitationAsync(cancelled, ElicitationAnswer.Cancel(cancelled.GetElicitationForm()!))).RequireTask();
        Assert.Equal("cancelled", done.Status.Message!.GetText());

        Assert.Collection(agent.Seen,
            s => { Assert.Equal(ElicitationAction.Decline, s.Answer!.Action); Assert.Empty(s.Answer.Content); },
            s => { Assert.Equal(ElicitationAction.Cancel, s.Answer!.Action); Assert.Empty(s.Answer.Content); });
        // A valid answer of any action records the form as used.
        Assert.NotNull((await client.GetTaskAsync(declined.Id)).Metadata![Elicitation.AnsweredKey].GetString());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Streaming_round_trip(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);

        string? taskId = null, contextId = null;
        ElicitationForm? form = null;
        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("book"), options: Activated()))
        {
            switch (evt)
            {
                case TaskEvent { Task: var t }: taskId = t.Id; contextId = t.ContextId; form ??= t.GetElicitationForm(); break;
                case StatusUpdateEvent { StatusUpdate: var su } when su.Status.State == TaskState.InputRequired:
                    taskId = su.TaskId; contextId = su.ContextId; form = su.Status.Message!.GetElicitationForm(); break;
            }
            if (form is not null) break; // the stream stays open while the task waits for input
        }
        Assert.NotNull(form);
        Assert.NotNull(taskId);

        var answer = ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Bergen" }, { "nights", 3 } });
        TaskState? last = null;
        string? text = null;
        await foreach (var evt in client.SendStreamingMessageAsync(
            new SendMessageRequest { Message = answer.ToMessage(taskId, contextId) }, Activated()))
        {
            if (evt is StatusUpdateEvent { StatusUpdate.Status: var s }) { last = s.State; text = s.Message?.GetText() ?? text; }
        }
        Assert.Equal(TaskState.Completed, last);
        Assert.Equal("booked Bergen 3", text);
        Assert.Equal(3L, agent.Seen.Single().Answer!.GetInt64("nights"));
    }

    // ---- D10: refusals ----

    [Theory]
    [MemberData(nameof(RefusalCases))]
    public async Task Refused_answer_is_not_stored_agent_does_not_run_and_a_retry_works(string binding, string answerJson)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);
        var task = await AskAsync(client);
        var before = await client.GetTaskAsync(task.Id);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Raw(task, answerJson), options: Activated()));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.DoesNotContain(Marker, Everything(ex));

        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        var after = await client.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.InputRequired, after.Status.State);
        Assert.Equal(before.History!.Count, after.History!.Count);
        Assert.False(after.Metadata?.ContainsKey(Elicitation.AnsweredKey) ?? false);

        var done = (await client.AnswerElicitationAsync(task, Good(task))).RequireTask();
        Assert.Equal("booked Oslo 2", done.Status.Message!.GetText());
    }

    /// <summary>All the text an error shows a caller: message and details.</summary>
    private static string Everything(A2AException ex) => ex.Message + string.Join("", ex.Details?.Select(d => d.GetRawText()) ?? []);

    private static HttpRequestMessage RawSend(string binding, string taskId, string answerJson)
    {
        var message = "{\"messageId\":\"raw-1\",\"taskId\":\"" + taskId + "\",\"role\":\"ROLE_USER\",\"parts\":[{\"text\":\"x\"}],\"metadata\":{\""
            + Elicitation.MetadataKey + "\":" + answerJson + "}}";
        var req = binding == ProtocolBindings.JsonRpc
            ? new HttpRequestMessage(HttpMethod.Post, "/a2a") { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"SendMessage\",\"params\":{\"message\":" + message + "}}", System.Text.Encoding.UTF8, "application/json") }
            : new HttpRequestMessage(HttpMethod.Post, "/a2a/message:send") { Content = new StringContent("{\"message\":" + message + "}", System.Text.Encoding.UTF8, "application/a2a+json") };
        req.Headers.Add(A2AProtocol.Headers.Version, "1.0");
        req.Headers.Add(A2AProtocol.Headers.Extensions, Elicitation.ExtensionUri);
        return req;
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Refusal_names_the_extension_and_the_field_on_the_wire(string binding)
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        var task = await AskAsync(ClientFor(host, binding));
        using var req = RawSend(binding, task.Id, "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":\"" + Marker + "\"}}");
        var resp = await host.Http("alice").SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Marker, body);
        using var doc = JsonDocument.Parse(body);
        var error = doc.RootElement.GetProperty("error");
        var details = binding == ProtocolBindings.JsonRpc ? error.GetProperty("data") : error.GetProperty("details");
        Assert.Equal("INVALID_PARAMS", details[0].GetProperty("reason").GetString());
        var metadata = details[0].GetProperty("metadata");
        Assert.Equal(Elicitation.ExtensionUri, metadata.GetProperty("extension").GetString());
        Assert.Equal("nights", metadata.GetProperty("field").GetString());
    }

    public static TheoryData<string, string> RefusalCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var binding in new[] { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson })
        {
            foreach (var json in new[]
            {
                "{\"id\":\"other\",\"action\":\"decline\"}",                                                   // wrong id
                "{\"id\":\"f1\",\"action\":\"maybe\"}",                                                         // unknown action
                "{\"id\":\"f1\",\"action\":\"decline\",\"content\":{\"city\":\"" + Marker + "\"}}",              // content on decline
                "{\"id\":\"f1\",\"action\":\"cancel\",\"content\":{}}",                                         // content on cancel
                "{\"id\":\"f1\",\"action\":\"accept\"}",                                                        // missing content
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":[\"" + Marker + "\"]}",                       // content not an object
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":2,\"" + Marker + "\":1}}", // unknown property
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"" + Marker + "\"}}",              // missing required
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":null,\"nights\":2}}",               // null value
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":\"" + Marker + "\"}}", // wrong type
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":2.5}}",         // not whole
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":99}}",          // range
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"" + new string('x', 101) + "\",\"nights\":2}}", // length
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":2,\"room\":\"" + Marker + "\"}}", // option
                "{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":2,\"late\":\"" + Marker + "\"}}", // boolean
                "\"" + Marker + "\"",                                                                           // not an object
                "{\"id\":\"f1\",\"action\":\"decline\",\"extra\":\"" + Marker + "\"}",                          // unknown key
            })
                data.Add(binding, json);
        }
        return data;
    }

    // ---- D7 ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task D7_not_activated_the_helper_sends_text_only(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var task = await AskAsync(ClientFor(host, binding), activate: false);
        Assert.Equal("Which city, and how many nights?", task.Status.Message!.GetText());
        Assert.Null(task.Status.Message.Metadata);
        Assert.Null(task.GetElicitationForm());
        Assert.False(agent.NewTaskCanElicit);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task D7_card_without_the_extension_strips_the_form(string binding)
    {
        var (host, agent) = await StartAsync(declare: false);
        await using var _ = host;
        var task = await AskAsync(ClientFor(host, binding));
        Assert.Null(task.Status.Message!.Metadata);
        Assert.Null(task.GetElicitationForm());
        Assert.False(agent.NewTaskCanElicit);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task D7_not_activated_an_answer_key_is_ignored_entirely(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);
        var task = await AskAsync(client);

        // Valid, then garbage: neither is read, checked or refused without activation.
        var done = (await client.SendMessageAsync(Raw(task, "{\"id\":\"zzz\",\"action\":\"nonsense\"}", "in words"))).RequireTask();
        Assert.Equal("text: in words", done.Status.Message!.GetText());
        var seen = Assert.Single(agent.Seen);
        Assert.Null(seen.Answer);
        Assert.False(seen.CanElicit);
        Assert.False((await client.GetTaskAsync(task.Id)).Metadata?.ContainsKey(Elicitation.AnsweredKey) ?? false);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task D7_activated_plain_text_reply_is_accepted_and_does_not_consume_the_form(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);
        var task = await AskAsync(client);

        await client.SendMessageAsync(Message.FromUser("stay", taskId: task.Id), options: Activated());
        await host.WaitIdleAsync();
        var seen = Assert.Single(agent.Seen);
        Assert.Null(seen.Answer);
        Assert.Equal("stay", seen.Text);
        var waiting = await client.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.InputRequired, waiting.Status.State);
        Assert.False(waiting.Metadata?.ContainsKey(Elicitation.AnsweredKey) ?? false);

        // The same form can still be answered.
        var done = (await client.AnswerElicitationAsync(waiting, Good(waiting))).RequireTask();
        Assert.Equal("booked Oslo 2", done.Status.Message!.GetText());
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task D7_activated_answer_without_a_pending_form_is_refused(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);

        // A task that waits with a plain text question.
        var plain = await AskAsync(client, "plain-input");
        Assert.Null(plain.Status.Message!.Metadata);
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Raw(plain, "{\"id\":\"f1\",\"action\":\"decline\"}"), options: Activated()));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);

        // A new task has no form to answer.
        var newTask = new Message { MessageId = "new-1", Role = Role.User, Parts = [Part.FromText("book")], Metadata = new() { [Elicitation.MetadataKey] = Json("{\"id\":\"f1\",\"action\":\"decline\"}") } };
        ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(newTask, options: Activated()));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(1, agent.Calls);
        Assert.Single((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task D7_new_task_with_the_key_is_ignored_when_not_activated(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var message = new Message { MessageId = "new-1", Role = Role.User, Parts = [Part.FromText("book")], Metadata = new() { [Elicitation.MetadataKey] = Json("{\"id\":\"f1\",\"action\":\"decline\"}") } };
        var task = (await ClientFor(host, binding).SendMessageAsync(message)).RequireTask();
        Assert.Equal(TaskState.InputRequired, task.Status.State);
        Assert.Equal(1, agent.Calls);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Empty_text_is_refused_by_the_helper(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        await ClientFor(host, binding).SendMessageAsync(Message.FromUser("empty-text"), options: Activated());
        Assert.NotNull(agent.EmptyTextError);
    }

    // ---- D9: single use ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task D9_a_used_form_cannot_be_answered_again(string binding)
    {
        var (host, agent) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);
        var task = await AskAsync(client);

        // The agent does nothing, so the task stays in InputRequired with the same status message.
        await client.AnswerElicitationAsync(task, Good(task, "Stay"));
        await host.WaitIdleAsync();
        var stored = await client.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.InputRequired, stored.Status.State);
        Assert.Equal(task.Status.Message!.MessageId, stored.Metadata![Elicitation.AnsweredKey].GetString());
        var historyCount = stored.History!.Count;

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.AnswerElicitationAsync(stored, Good(stored)));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        await host.WaitIdleAsync();
        Assert.Equal(2, agent.Calls);
        Assert.Equal(historyCount, (await client.GetTaskAsync(task.Id)).History!.Count);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task D9_a_re_ask_with_the_same_form_id_can_be_answered(string binding)
    {
        var (host, _) = await StartAsync();
        await using var _ = host;
        var client = ClientFor(host, binding);
        var first = await AskAsync(client);

        var second = (await client.AnswerElicitationAsync(first, Good(first, "Again"))).RequireTask();
        Assert.Equal(TaskState.InputRequired, second.Status.State);
        Assert.Equal("f1", second.GetElicitationForm()!.Id);
        Assert.NotEqual(first.Status.Message!.MessageId, second.Status.Message!.MessageId);

        var done = (await client.AnswerElicitationAsync(second, Good(second, "Oslo", 4))).RequireTask();
        Assert.Equal("booked Oslo 4", done.Status.Message!.GetText());
        Assert.Equal(second.Status.Message.MessageId, (await client.GetTaskAsync(first.Id)).Metadata![Elicitation.AnsweredKey].GetString());
    }

    // ---- D14: MaxTurnsPerTask ----

    [Theory, MemberData(nameof(Bindings))]
    public async Task D14_refused_answers_use_no_turn_accepted_ones_use_one_and_the_cap_is_checked_first(string binding)
    {
        var (host, agent) = await StartAsync(o => o.MaxTurnsPerTask = 2);
        await using var _ = host;
        var client = ClientFor(host, binding);
        var task = await AskAsync(client); // turn 1

        var refused = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Raw(task, "{\"id\":\"f1\",\"action\":\"accept\"}"), options: Activated()));
        Assert.Equal(A2AErrorCode.InvalidParams, refused.Code); // no turn used

        var second = (await client.AnswerElicitationAsync(task, Good(task, "Again"))).RequireTask(); // turn 2
        Assert.Equal(TaskState.InputRequired, second.Status.State);

        var capped = await Assert.ThrowsAsync<A2AException>(() => client.AnswerElicitationAsync(second, Good(second)));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, capped.Code);
        // The cap runs first, even for an answer that would be refused.
        var cappedBad = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Raw(second, "{\"id\":\"nope\",\"action\":\"decline\"}"), options: Activated()));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, cappedBad.Code);
        await host.WaitIdleAsync();
        Assert.Equal(2, agent.Calls);
    }

    // ---- per-tenant cards ----

    private sealed class Tenants : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            var card = TestAgentHost.DefaultCard();
            if (scope.Tenant == "with") card.Capabilities.Extensions = [Elicitation.CreateExtension()];
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Per_tenant_cards_decide_activation_for_new_tasks_and_answers(string binding)
    {
        var agent = new Agent();
        await using var host = await TestAgentHost.StartAsync(
            services: s =>
            {
                s.AddA2AServer().AddTenantCardProvider<Tenants>();
                s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(agent));
            }, grpc: true);
        A2AClient Tenant(string name) => host.Client(binding, options: new A2AClientOptions { Tenant = name, ValidateCapabilities = false });

        // CanElicit is true on a new task, with no answer key on the message to make the server look at the card.
        var with = await AskAsync(Tenant("with"));
        Assert.True(agent.NewTaskCanElicit);
        Assert.NotNull(with.GetElicitationForm());
        var done = (await Tenant("with").AnswerElicitationAsync(with, Good(with))).RequireTask();
        Assert.Equal("booked Oslo 2", done.Status.Message!.GetText());

        var without = await AskAsync(Tenant("without"));
        Assert.False(agent.NewTaskCanElicit);
        Assert.Null(without.Status.Message!.Metadata);
    }
}
