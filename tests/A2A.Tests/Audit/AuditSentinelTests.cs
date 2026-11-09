using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Elicitations;
using A2A.Server;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Retention;
using A2A.Server.Storage;
using A2A.Tests.Elicitations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace A2A.Tests.Audit;

/// <summary>An event never holds content or a secret: every place a caller can put text carries a sentinel, and none reaches the trail.</summary>
public class AuditSentinelTests
{
    private const string Sentinel = "SENTINEL-4f9c1e7a-do-not-audit";

    private sealed class DenyList : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.ListTasks);
    }

    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task No_sentinel_reaches_an_event()
    {
        var sink = new RecordingAuditSink();
        await using var host = await AuditTestSupport.StartAsync(sink,
            o => o.AgentCard.Capabilities.Extensions = [Elicitation.CreateExtension()],
            s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IAgentExecutor>(new ElicitationServerTests.Agent()));
                s.Replace(ServiceDescriptor.Singleton<A2AAuthorizationHandler, DenyList>());
            });
        // A bearer token in the Authorization header, on a transport of its own.
        var http = host.Http("alice");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Sentinel);
        var options = new A2AClientOptions { ValidateCapabilities = false, MaxRetries = 0 };
        var client = new A2AClient(new JsonRpcTransport(http, new Uri(TestAgentHost.BaseUrl), options), options, TestAgentHost.DefaultCard());

        // A new task with text, bytes, data, metadata, and an inline push config whose URL, token and credentials hold the sentinel.
        var message = new Message
        {
            MessageId = Guid.NewGuid().ToString(), Role = Role.User,
            Parts =
            [
                Part.FromText($"book {Sentinel}"),
                Part.FromBytes(Encoding.UTF8.GetBytes(Sentinel), "text/plain", $"{Sentinel}.bin"),
                Part.FromData(Json($"{{\"secret\":\"{Sentinel}\"}}"), "text/plain"),
            ],
            Metadata = new() { ["note"] = Json($"\"{Sentinel}\"") },
        };
        var asked = (await client.SendMessageAsync(message,
            new SendMessageConfiguration
            {
                TaskPushNotificationConfig = new TaskPushNotificationConfig
                {
                    Url = $"https://hooks.example/cb?token={Sentinel}", Token = Sentinel,
                    Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = Sentinel },
                },
            }, new A2ARequestOptions().UseElicitation())).RequireTask();

        // A direct push config with the same secrets, then its delete.
        var created = await client.CreateTaskPushNotificationConfigAsync(new TaskPushNotificationConfig
        {
            TaskId = asked.Id, Url = $"https://hooks.example/cb?token={Sentinel}", Token = Sentinel,
            Authentication = new AuthenticationInfo { Scheme = "Bearer", Credentials = Sentinel },
        });
        await client.DeleteTaskPushNotificationConfigAsync(new DeleteTaskPushNotificationConfigRequest { TaskId = asked.Id, Id = created.Id! });

        // A refused call, a denied call, and an answer to the form with the sentinel as its value.
        await Assert.ThrowsAsync<A2AException>(() => client.SendMessageAsync(Message.FromUser($"{Sentinel}", taskId: "no-such-task")));
        await Assert.ThrowsAsync<A2AException>(() => client.ListTasksAsync(new ListTasksRequest()));
        var answer = ElicitationAnswer.Accept(asked.GetElicitationForm()!, new ElicitationValues { { "city", Sentinel }, { "nights", 2 } });
        await client.AnswerElicitationAsync(asked, answer, options: new A2ARequestOptions().UseElicitation());

        // The admin path, with a task that is gone.
        await host.App.Services.GetRequiredService<A2ATaskAdmin>().DeleteTaskAsync(new TaskKey("", "alice", asked.Id), "ops");

        Assert.True(sink.Events.Count >= 7, $"only {sink.Events.Count} events were recorded");
        Assert.Contains(sink.Events, e => e.Action == A2AAuditAction.ElicitationAnswered);
        Assert.Contains(sink.Events, e => e.Action == A2AAuditAction.PushConfigCreated);
        Assert.Contains(sink.Events, e => e.Action == A2AAuditAction.AccessDenied);
        foreach (var e in sink.Events)
            Assert.DoesNotContain(Sentinel, JsonSerializer.Serialize(e), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, string.Join("\n", sink.Events.Select(e => e.ToString())), StringComparison.Ordinal);
    }
}
