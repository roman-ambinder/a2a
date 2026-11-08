using A2A.Client;
using A2A.Server;
using A2A.Server.Agents;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Pb = Lf.A2a.V1;

namespace A2A.Grpc.Tests;

/// <summary>Two agents behind one gRPC service, picked by the request's <c>tenant</c> field.</summary>
public class MultiAgentGrpcTests
{
    private sealed class Cards : TenantCardProvider
    {
        public override ValueTask<AgentCard?> GetCardAsync(AgentRequestScope scope, CancellationToken cancellationToken)
        {
            if (scope.Tenant is not ("acme" or "globex")) return ValueTask.FromResult<AgentCard?>(null);
            var card = GrpcHost.Card();
            card.Name = scope.Tenant;
            card.Capabilities.Streaming = scope.Tenant == "acme";
            return ValueTask.FromResult<AgentCard?>(card);
        }
    }

    private sealed class Agents : AgentExecutorResolver
    {
        public override IAgentExecutor? Resolve(string tenant, IServiceProvider services) =>
            tenant is "acme" or "globex" ? new Named(tenant) : null;

        private sealed class Named(string name) : IAgentExecutor
        {
            public async Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken ct) =>
                await updater.CompleteAsync($"{name}: {context.UserText}", ct);
        }
    }

    [Fact]
    public async Task Tenant_field_selects_the_agent_and_its_card()
    {
        await using var host = await GrpcHost.StartAsync(services: s => s.AddA2AServer().AddTenantCardProvider<Cards>().AddAgentExecutorResolver<Agents>());
        var raw = host.Raw();
        var headers = new Metadata { { "a2a-version", "1.0" } };
        Pb.SendMessageRequest Send(string tenant, string text) => new()
        {
            Tenant = tenant,
            Message = new Pb.Message { MessageId = Guid.NewGuid().ToString(), Role = Pb.Role.User, Parts = { new Pb.Part { Text = text } } },
        };

        var acme = await raw.SendMessageAsync(Send("acme", "work"), headers);
        Assert.Equal("acme: work", acme.Task.Status.Message.Parts[0].Text);
        var globex = await raw.SendMessageAsync(Send("globex", "work"), headers);
        Assert.Equal("globex: work", globex.Task.Status.Message.Parts[0].Text);

        // Tasks stay with their agent.
        var cross = await Assert.ThrowsAsync<RpcException>(() => raw.GetTaskAsync(new Pb.GetTaskRequest { Tenant = "globex", Id = acme.Task.Id }, headers).ResponseAsync);
        Assert.Equal(A2AErrorCode.TaskNotFound, GrpcErrors.FromRpcException(cross).Code);

        // globex's card has no streaming.
        var stream = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            using var call = raw.SendStreamingMessage(Send("globex", "work"), headers);
            while (await call.ResponseStream.MoveNext(CancellationToken.None)) { }
        });
        Assert.Equal(A2AErrorCode.UnsupportedOperation, GrpcErrors.FromRpcException(stream).Code);

        // No agent for the tenant.
        var unknown = await Assert.ThrowsAsync<RpcException>(() => raw.SendMessageAsync(Send("initech", "work"), headers).ResponseAsync);
        var error = GrpcErrors.FromRpcException(unknown);
        Assert.Equal(A2AErrorCode.UnsupportedOperation, error.Code);
        Assert.Equal("No agent is configured for this tenant.", error.Message);
        var missing = await Assert.ThrowsAsync<RpcException>(() => raw.GetTaskAsync(new Pb.GetTaskRequest { Tenant = "initech", Id = acme.Task.Id }, headers).ResponseAsync);
        Assert.Equal(A2AErrorCode.TaskNotFound, GrpcErrors.FromRpcException(missing).Code);
    }
}
