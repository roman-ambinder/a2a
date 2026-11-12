using A2A;
using A2A.Client;
using A2A.Elicitations;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

// An agent asks for a booking form, and a client answers it. Four runs: accept, decline, cancel and a plain-text reply.
// No API keys, no external calls: the server listens on localhost only.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:0"); // loopback only, free port
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var card = new AgentCard
{
    Name = "Booking Agent",
    Description = "Books a hotel room. Asks for the details with a small form.",
    Version = "1.0.0",
    // The optional extension entry. A client that does not know the extension still gets a plain-text question.
    Capabilities = new AgentCapabilities { Extensions = [Elicitation.CreateExtension()] },
    DefaultInputModes = ["text/plain"],
    DefaultOutputModes = ["text/plain"],
    Skills =
    [
        new AgentSkill { Id = "book", Name = "Book a room", Description = "Books a hotel room", Tags = ["booking"] }
    ]
};

// AllowAnonymous is fine for a sample that only listens on loopback. A real agent needs authentication
// (see samples/EchoAgent) and MapA2A(...).RequireAuthorization().
builder.Services.AddA2A(a => a.AddAgentExecutor<BookingAgent>().AddAgentCard(card))
    .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);

var app = builder.Build();
app.MapA2A("/a2a");
await app.StartAsync();

var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
Console.WriteLine($"Agent listening on {address}/a2a");

using var http = new HttpClient();
var client = A2AClientFactory.CreateForEndpoint(new Uri($"{address}/a2a"), http);

foreach (var reply in new[] { "accept", "decline", "cancel", "text" })
{
    Console.WriteLine();
    Console.WriteLine($"--- run: {reply}");

    // The client opts in with UseElicitation. Without it the agent sends only the text.
    var started =
        (await client.SendMessageAsync(Message.FromUser("Book me a room"),
            options: new A2ARequestOptions().UseElicitation())).AsTask();
    Console.WriteLine($"agent asks: {started.Status.Message!.GetText()}");

    var form = started.GetElicitationForm();
    if (form is null) throw new InvalidOperationException("The agent sent no form.");

    // A real app must show this form to a person, say which agent is asking (by its card origin, not only its name),
    // and let the person fill it in. Never let a model fill it in alone: a hostile agent could ask for data from its context.
    foreach (var field in form.Fields)
        Console.WriteLine(
            $"  field {field.Name} ({field.Type}{(field.Required ? ", required" : "")}){(field.Options is { } o ? $": {string.Join("/", o)}" : "")}");

    AgentTask done;
    switch (reply)
    {
        case "accept":
            var answer = ElicitationAnswer.Accept(form,
                new ElicitationValues { { "city", "Oslo" }, { "nights", 2 }, { "room", "double" }, { "late", true } });
            done = (await client.AnswerElicitationAsync(started, answer)).AsTask();
            break;
        case "decline":
            done = (await client.AnswerElicitationAsync(started, ElicitationAnswer.Decline(form))).AsTask();
            break;
        case "cancel":
            done = (await client.AnswerElicitationAsync(started, ElicitationAnswer.Cancel(form))).AsTask();
            break;
        default:
            // A client can always answer in words, with the same task id.
            done = (await client.SendMessageAsync(Message.FromUser("Bergen, 3 nights", started.ContextId, started.Id)))
                .AsTask();
            break;
    }

    Console.WriteLine($"state: {done.Status.State}");
    Console.WriteLine($"agent says: {done.Status.Message!.GetText()}");
}

await app.StopAsync();

/// <summary>Asks for a booking form, then books, or stops, depending on the answer.</summary>
internal sealed class BookingAgent : IAgentExecutor
{
    #region ElicitServer
    public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater,
        CancellationToken cancellationToken)
    {
        // The form is peer input in the other direction too: a client may send anything. The server already checked the
        // answer against the form, so the types and limits hold. The values are still what the user typed.
        if (context.ElicitationAnswer is { } answer)
        {
            switch (answer.Action)
            {
                case ElicitationAction.Accept:
                    await updater.CompleteAsync(
                        $"Booked a {answer.GetString("room") ?? "standard"} room in {answer.GetString("city")} for {answer.GetInt64("nights")} nights"
                        + (answer.GetBoolean("late") == true ? ", late check-in." : "."), cancellationToken);
                    break;
                case ElicitationAction.Decline:
                    await updater.FailAsync("No booking: the user declined to give details.", cancellationToken);
                    break;
                default
                    : // Cancel: the user dismissed the form without a decision. The server moves no state: the agent decides.
                    await updater.CancelAsync("Booking dismissed.", cancellationToken);
                    break;
            }

            return;
        }

        // A turn after a form without an answer: the user replied in plain text (or the client cannot read forms).
        if (context.Task is { Status.State: TaskState.InputRequired })
        {
            await updater.CompleteAsync($"Booked from your words: \"{context.UserText}\".", cancellationToken);
            return;
        }

        // Never ask for secrets in a form. The form refuses fields that look like passwords, tokens or card numbers.
        var form = new ElicitationForm(
        [
            ElicitationField.Text("city", "City", required: true, maxLength: 100),
            ElicitationField.Integer("nights", "Nights", required: true, minimum: 1, maximum: 30),
            ElicitationField.Choice("room", ["single", "double"], "Room type"),
            ElicitationField.Boolean("late", "Late check-in")
        ]);
        await updater.RequireInputAsync(context, form, "Which city, how many nights, and what room do you want?",
            cancellationToken);
    }
    #endregion
}

internal static class SampleExtensions
{
    /// <summary>The task of a response. A task that waits for input always comes back as a task, not a plain message.</summary>
    public static AgentTask AsTask(this SendMessageResponse response)
    {
        return response is TaskResponse { Task: var task }
            ? task
            : throw new InvalidOperationException("The agent answered with a message, not a task.");
    }
}