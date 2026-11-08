using System.Collections.Concurrent;
using System.Text.Json;
using A2A.Client;
using A2A.Elicitations;
using A2A.Server;

namespace A2A.Grpc.Tests;

/// <summary>Structured elicitation over gRPC. Metadata travels as a Struct there, so every number is a double.</summary>
public class GrpcElicitationTests
{
    private static ElicitationForm TripForm() => new(
    [
        ElicitationField.Text("city", "City", required: true, maxLength: 100),
        ElicitationField.Integer("nights", required: true, minimum: 1, maximum: 30),
        ElicitationField.Number("budget", minimum: 0),
        ElicitationField.Choice("room", ["single", "double"]),
        ElicitationField.Boolean("late"),
    ], "f1");

    private static Task<GrpcHost> StartAsync(ConcurrentQueue<ElicitationAnswer?> seen, Action<A2AServerOptions>? configure = null) => GrpcHost.StartAsync(
        o =>
        {
            o.AgentCard.Capabilities.Extensions = [Elicitation.CreateExtension()];
            configure?.Invoke(o);
        },
        server: b => b.AddAgentExecutor(async (ctx, updater, ct) =>
        {
            if (ctx.Task is null)
            {
                await updater.RequireInputAsync(ctx, TripForm(), "Which city?", ct);
                return;
            }
            seen.Enqueue(ctx.ElicitationAnswer);
            if (ctx.ElicitationAnswer is { Action: ElicitationAction.Accept } a)
                await updater.CompleteAsync($"booked {a.GetString("city")} {a.GetInt64("nights")} {a.GetDouble("budget")} {a.GetBoolean("late")}", ct);
            else await updater.FailAsync("no answer", ct);
        }));

    private static A2AClient ClientFor(GrpcHost host) => host.Client(options: new A2AClientOptions { ValidateCapabilities = false });

    private static A2ARequestOptions Activated() => new A2ARequestOptions().UseElicitation();

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Accept_round_trip_reads_integers_after_the_struct_conversion()
    {
        var seen = new ConcurrentQueue<ElicitationAnswer?>();
        await using var host = await StartAsync(seen);
        var client = ClientFor(host);
        var task = (await client.SendMessageAsync(Message.FromUser("book"), options: Activated())).RequireTask();

        var form = task.GetElicitationForm();
        Assert.NotNull(form);
        Assert.Equal("f1", form.Id);
        Assert.Equal(["budget", "city", "late", "nights", "room"], form.Fields.Select(f => f.Name).Order().ToArray());
        var nights = form.Fields.Single(f => f.Name == "nights");
        Assert.Equal(ElicitationFieldType.Integer, nights.Type);
        Assert.True(nights.Required);
        Assert.Equal(1, nights.Minimum);
        Assert.Equal(30, nights.Maximum);
        Assert.Equal(100, form.Fields.Single(f => f.Name == "city").MaxLength);
        Assert.Equal(["single", "double"], form.Fields.Single(f => f.Name == "room").Options);

        var answer = ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Oslo" }, { "nights", 2 }, { "budget", 99.5 }, { "late", true } });
        var done = (await client.AnswerElicitationAsync(task, answer)).RequireTask();
        Assert.Equal("booked Oslo 2 99.5 True", done.Status.Message!.GetText());
        var got = Assert.Single(seen)!;
        Assert.Equal(2L, got.GetInt64("nights"));
        Assert.Equal(99.5, got.GetDouble("budget"));
        Assert.Equal("Oslo", got.GetString("city"));
        Assert.Equal(true, got.GetBoolean("late"));

        var stored = await client.GetTaskAsync(task.Id);
        Assert.Equal(task.Status.Message!.MessageId, stored.Metadata![Elicitation.AnsweredKey].GetString());
    }

    [Fact]
    public async Task Decline_and_cancel_reach_the_agent()
    {
        var seen = new ConcurrentQueue<ElicitationAnswer?>();
        await using var host = await StartAsync(seen);
        var client = ClientFor(host);
        foreach (var make in new Func<ElicitationForm, ElicitationAnswer>[] { ElicitationAnswer.Decline, ElicitationAnswer.Cancel })
        {
            var task = (await client.SendMessageAsync(Message.FromUser("book"), options: Activated())).RequireTask();
            var done = (await client.AnswerElicitationAsync(task, make(task.GetElicitationForm()!))).RequireTask();
            Assert.Equal("no answer", done.Status.Message!.GetText());
        }
        Assert.Collection(seen,
            a => { Assert.Equal(ElicitationAction.Decline, a!.Action); Assert.Empty(a.Content); },
            a => { Assert.Equal(ElicitationAction.Cancel, a!.Action); Assert.Empty(a.Content); });
    }

    [Fact]
    public async Task Streaming_round_trip()
    {
        var seen = new ConcurrentQueue<ElicitationAnswer?>();
        await using var host = await StartAsync(seen);
        var client = ClientFor(host);

        string? taskId = null, contextId = null;
        ElicitationForm? form = null;
        await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("book"), options: Activated()))
        {
            if (evt is StatusUpdateEvent { StatusUpdate: var su } && su.Status.State == TaskState.InputRequired)
            {
                taskId = su.TaskId; contextId = su.ContextId; form = su.Status.Message!.GetElicitationForm();
                break; // the stream stays open while the task waits for input
            }
        }
        Assert.NotNull(form);

        var answer = ElicitationAnswer.Accept(form, new ElicitationValues { { "city", "Bergen" }, { "nights", 3 } });
        string? text = null;
        await foreach (var evt in client.SendStreamingMessageAsync(new SendMessageRequest { Message = answer.ToMessage(taskId!, contextId) }, Activated()))
            if (evt is StatusUpdateEvent { StatusUpdate.Status.Message: { } m }) text = m.GetText();
        Assert.StartsWith("booked Bergen 3", text);
        Assert.Equal(3L, Assert.Single(seen)!.GetInt64("nights"));
    }

    [Fact]
    public async Task Refused_answer_names_the_extension_and_field_and_the_task_still_waits()
    {
        var seen = new ConcurrentQueue<ElicitationAnswer?>();
        await using var host = await StartAsync(seen);
        var client = ClientFor(host);
        var task = (await client.SendMessageAsync(Message.FromUser("book"), options: Activated())).RequireTask();

        var bad = new Message
        {
            MessageId = "bad-1", Role = Role.User, TaskId = task.Id, Parts = [Part.FromText("x")],
            Metadata = new() { [Elicitation.MetadataKey] = Json("{\"id\":\"f1\",\"action\":\"accept\",\"content\":{\"city\":\"Oslo\",\"nights\":\"SECRET-MARKER\"}}") },
        };
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(bad, options: Activated()));
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(Elicitation.ExtensionUri, ex.Metadata?["extension"]);
        Assert.Equal("nights", ex.Metadata?["field"]);
        Assert.DoesNotContain("SECRET-MARKER", ex.Message);

        Assert.Empty(seen);
        var waiting = await client.GetTaskAsync(task.Id);
        Assert.Equal(TaskState.InputRequired, waiting.Status.State);
        Assert.False(waiting.Metadata?.ContainsKey(Elicitation.AnsweredKey) ?? false);
        var done = (await client.AnswerElicitationAsync(task, ElicitationAnswer.Accept(task.GetElicitationForm()!, new ElicitationValues { { "city", "Oslo" }, { "nights", 2 } }))).RequireTask();
        Assert.StartsWith("booked Oslo 2", done.Status.Message!.GetText());
    }

    [Fact]
    public async Task Not_activated_the_form_is_text_only_and_an_answer_key_is_ignored()
    {
        var seen = new ConcurrentQueue<ElicitationAnswer?>();
        await using var host = await StartAsync(seen);
        var client = ClientFor(host);
        var task = (await client.SendMessageAsync(Message.FromUser("book"))).RequireTask();
        Assert.Equal("Which city?", task.Status.Message!.GetText());
        Assert.Equal(A2AProtocol.MessageRecordedAtKey, Assert.Single(task.Status.Message.Metadata!).Key); // the form is gone; only the server stamp remains

        var ignored = new Message
        {
            MessageId = "ig-1", Role = Role.User, TaskId = task.Id, Parts = [Part.FromText("in words")],
            Metadata = new() { [Elicitation.MetadataKey] = Json("{\"id\":\"nonsense\"}") },
        };
        var done = (await client.SendMessageAsync(ignored)).RequireTask();
        Assert.Equal("no answer", done.Status.Message!.GetText());
        Assert.Null(Assert.Single(seen));
    }

    [Fact]
    public async Task Max_turns_are_counted_for_accepted_answers_only()
    {
        var seen = new ConcurrentQueue<ElicitationAnswer?>();
        await using var host = await StartAsync(seen, o => o.MaxTurnsPerTask = 1);
        var client = ClientFor(host);
        var task = (await client.SendMessageAsync(Message.FromUser("book"), options: Activated())).RequireTask();
        var ex = await Assert.ThrowsAsync<A2AException>(() => client.AnswerElicitationAsync(task, ElicitationAnswer.Decline(task.GetElicitationForm()!)));
        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Empty(seen);
    }
}

internal static class ElicitationResponseExtensions
{
    public static AgentTask RequireTask(this SendMessageResponse response) => Assert.IsType<TaskResponse>(response).Task;
}
