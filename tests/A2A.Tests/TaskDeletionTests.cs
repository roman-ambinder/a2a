using System.Net;
using System.Text;
using System.Text.Json;
using A2A.Client;
using A2A.Deletion;
using A2A.Server;
using A2A.Server.Audit;
using A2A.Server.Authorization;
using A2A.Server.Pipeline;
using A2A.Server.Storage;
using A2A.Tests.Audit;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.Tests;

/// <summary>
/// The opt-in task-deletion extension operation (spec §13.4, <c>AddTaskDeletion</c>): owner only, terminal tasks only,
/// everything the operator delete removes, audited.
/// </summary>
public sealed class TaskDeletionTests
{
    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };

    private static Task<TestAgentHost> StartAsync(Action<A2AServerOptions>? configure = null, Action<IServiceCollection>? more = null,
        bool requireAuth = true) =>
        TestAgentHost.StartAsync(configure, requireAuth: requireAuth, services: s =>
        {
            s.AddA2AServer().AddTaskDeletion();
            more?.Invoke(s);
        });

    private static async Task<AgentTask> CompletedTaskAsync(TestAgentHost host, string binding, string user = "alice")
    {
        var task = (await host.Client(binding, user).SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);
        await host.WaitIdleAsync();
        return task;
    }

    private static async Task<JsonElement> ServedCardAsync(TestAgentHost host)
    {
        var text = await host.Http().GetStringAsync("/.well-known/agent-card.json");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static bool CardListsDeletion(JsonElement card) =>
        card.GetProperty("capabilities").TryGetProperty("extensions", out var extensions)
        && extensions.EnumerateArray().Any(e => e.GetProperty("uri").GetString() == TaskDeletion.ExtensionUri);

    // ---------------------------------------------------------------- opt-in

    [Theory, MemberData(nameof(Bindings))]
    public async Task Without_AddTaskDeletion_the_operation_is_unknown_and_the_card_has_no_extension(string binding)
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = await CompletedTaskAsync(host, binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).DeleteTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.MethodNotFound, ex.Code);
        Assert.False(CardListsDeletion(await ServedCardAsync(host)));
        Assert.Equal(task.Id, (await host.Client(binding).GetTaskAsync(task.Id)).Id);
    }

    [Fact]
    public async Task The_card_declares_the_extension_when_registered()
    {
        await using var host = await StartAsync();

        var card = await ServedCardAsync(host);

        Assert.True(CardListsDeletion(card));
        var extension = card.GetProperty("capabilities").GetProperty("extensions").EnumerateArray()
            .Single(e => e.GetProperty("uri").GetString() == TaskDeletion.ExtensionUri);
        Assert.False(extension.TryGetProperty("required", out var required) && required.GetBoolean());
    }

    [Fact]
    public void Calling_it_twice_registers_one_operation_and_a_custom_operation_of_the_same_name_is_refused()
    {
        var services = new ServiceCollection();
        var builder = services.AddA2AServer().AddTaskDeletion().AddTaskDeletion();

        Assert.Single(services, d => d.ServiceType == typeof(A2AExtensionOperation));
        Assert.Throws<ArgumentException>(() =>
            builder.AddExtensionOperation(TaskDeletion.Method, "urn:test:other", (_, p, _) => ValueTask.FromResult(p), allow: true));

        var other = new ServiceCollection().AddA2AServer()
            .AddExtensionOperation(TaskDeletion.Method, "urn:test:other", (_, p, _) => ValueTask.FromResult(p), allow: true);
        Assert.Throws<ArgumentException>(() => other.AddTaskDeletion());
    }

    // ---------------------------------------------------------------- the owner deletes

    [Theory, MemberData(nameof(Bindings))]
    public async Task The_owner_deletes_a_completed_task_and_GetTask_then_answers_TaskNotFound(string binding)
    {
        await using var host = await StartAsync();
        var task = await CompletedTaskAsync(host, binding);
        var client = host.Client(binding);

        await client.DeleteTaskAsync(task.Id);

        var get = await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, get.Code);
        Assert.Empty((await client.ListTasksAsync(new ListTasksRequest())).Tasks);
        // The event log went with it.
        var events = host.App.Services.GetRequiredService<EventStore>();
        var key = new TaskKey("", "alice", task.Id);
        Assert.Equal(0, await events.LastSequenceAsync(key));

        // A second delete finds nothing.
        var again = await Assert.ThrowsAsync<A2AException>(() => client.DeleteTaskAsync(task.Id));
        Assert.Equal(A2AErrorCode.TaskNotFound, again.Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task Another_caller_gets_TaskNotFound_and_the_task_survives(string binding)
    {
        await using var host = await StartAsync();
        var task = await CompletedTaskAsync(host, binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, "bob").DeleteTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(task.Id, (await host.Client(binding).GetTaskAsync(task.Id)).Id);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_task_that_is_not_terminal_is_refused_and_survives(string binding)
    {
        await using var host = await StartAsync();
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("input")).RequireTask();
        Assert.Equal(TaskState.InputRequired, task.Status.State);

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.DeleteTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.UnsupportedOperation, ex.Code);
        Assert.Contains("terminal", ex.Message);
        Assert.Equal(TaskState.InputRequired, (await client.GetTaskAsync(task.Id)).Status.State);

        // Cancel first, then delete.
        await client.CancelTaskAsync(task.Id);
        await host.WaitIdleAsync();
        await client.DeleteTaskAsync(task.Id);
        Assert.Equal(A2AErrorCode.TaskNotFound, (await Assert.ThrowsAsync<A2AException>(() => client.GetTaskAsync(task.Id))).Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_missing_id_is_InvalidParams(string binding)
    {
        await using var host = await StartAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding)
            .InvokeExtensionOperationAsync(TaskDeletion.Method, TaskDeletion.ExtensionUri, JsonDocument.Parse("{}").RootElement));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_task_of_another_tenant_is_not_found(string binding)
    {
        await using var host = await StartAsync();
        var task = await CompletedTaskAsync(host, binding);

        var other = host.Client(binding, options: new A2AClientOptions { Tenant = "acme" });
        var ex = await Assert.ThrowsAsync<A2AException>(() => other.DeleteTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(task.Id, (await host.Client(binding).GetTaskAsync(task.Id)).Id);
    }

    // ---------------------------------------------------------------- authentication and authorization

    [Theory, MemberData(nameof(Bindings))]
    public async Task An_unauthenticated_caller_is_refused(string binding)
    {
        await using var host = await StartAsync();
        var task = await CompletedTaskAsync(host, binding);

        // The endpoint requires authorization: an anonymous call never reaches the operation.
        var path = binding == ProtocolBindings.JsonRpc ? "/a2a" : $"/a2a/ext/{TaskDeletion.Method}";
        var body = binding == ProtocolBindings.JsonRpc
            ? $$$"""{"jsonrpc":"2.0","id":1,"method":"{{{TaskDeletion.Method}}}","params":{"id":"{{{task.Id}}}"}}"""
            : $$"""{"id":"{{task.Id}}"}""";
        using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        req.Headers.Add(A2AProtocol.Headers.Extensions, TaskDeletion.ExtensionUri);
        var resp = await host.Http().SendAsync(req);

        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(task.Id, (await host.Client(binding).GetTaskAsync(task.Id)).Id);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task With_AllowAnonymous_an_anonymous_caller_is_still_Unauthenticated(string binding)
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartAsync(o => o.AllowAnonymous = true, requireAuth: false,
            more: s => s.AddA2AServer().AddAuditSink(sink));
        var task = (await host.Client(binding, user: null).SendMessageAsync("task")).RequireTask();
        await host.WaitIdleAsync();

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding, user: null).DeleteTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.Unauthenticated, ex.Code);
        Assert.Equal(task.Id, (await host.Client(binding, user: null).GetTaskAsync(task.Id)).Id);
        var denied = Assert.Single(sink.Of(A2AAuditAction.AccessDenied));
        Assert.Equal("Unauthenticated", denied.Reason);
        Assert.Equal(TaskDeletion.Method, denied.Operation);
        Assert.Empty(sink.Of(A2AAuditAction.TaskDeleted));
    }

    /// <summary>A host handler that denies task deletion and allows everything else.</summary>
    private sealed class DenyDeletion : A2AAuthorizationHandler
    {
        public override ValueTask<bool> AuthorizeAsync(ServerCallContext call, A2AOperationDescriptor operation, object request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(operation.Kind != A2AOperation.Extension);
    }

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_denial_by_a_custom_authorization_handler_is_TaskNotFound(string binding)
    {
        await using var host = await StartAsync(more: s => s.AddA2AServer().AddAuthorizationHandler<DenyDeletion>());
        var task = await CompletedTaskAsync(host, binding);

        var ex = await Assert.ThrowsAsync<A2AException>(() => host.Client(binding).DeleteTaskAsync(task.Id));

        Assert.Equal(A2AErrorCode.TaskNotFound, ex.Code);
        Assert.Equal(task.Id, (await host.Client(binding).GetTaskAsync(task.Id)).Id);
    }

    // ---------------------------------------------------------------- audit

    [Fact]
    public async Task The_delete_is_audited_with_the_caller_as_the_actor()
    {
        var sink = new RecordingAuditSink();
        await using var host = await StartAsync(more: s => s.AddA2AServer().AddAuditSink(sink));
        var task = await CompletedTaskAsync(host, ProtocolBindings.JsonRpc);
        var running = (await host.Client(ProtocolBindings.JsonRpc).SendMessageAsync("input")).RequireTask();
        var client = host.Client(ProtocolBindings.JsonRpc);

        await client.DeleteTaskAsync(task.Id);
        await Assert.ThrowsAsync<A2AException>(() => client.DeleteTaskAsync(task.Id));
        await Assert.ThrowsAsync<A2AException>(() => client.DeleteTaskAsync(running.Id));

        var deletes = sink.Of(A2AAuditAction.TaskDeleted).Final().ToList();
        Assert.Equal(3, deletes.Count);
        Assert.Equal(A2AAuditOutcome.Succeeded, deletes[0].Outcome);
        Assert.Equal("alice", deletes[0].Actor);
        Assert.Equal(task.Id, deletes[0].TaskId);
        Assert.Equal(ProtocolBindings.JsonRpc, deletes[0].Binding);
        Assert.Equal(A2AAuditOutcome.Failed, deletes[1].Outcome);
        Assert.Equal("NotFound", deletes[1].Reason);
        Assert.Equal(A2AAuditOutcome.Failed, deletes[2].Outcome);
        Assert.Equal(nameof(A2AErrorCode.UnsupportedOperation), deletes[2].Reason);
        Assert.Empty(sink.Of(A2AAuditAction.AdminTaskDeleted));
    }
}
