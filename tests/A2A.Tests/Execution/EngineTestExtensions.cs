using A2A.Server;
using A2A.Server.Execution;
using A2A.Server.Idempotency;

namespace A2A.Tests.Execution;

/// <summary>Starts an execution the way the request handler does: rents the tenant's agent first, then starts it.</summary>
internal static class EngineTestExtensions
{
    public static async Task<A2A.Server.Execution.Execution> StartForTestAsync(this TaskExecutionEngine engine, AgentRequestContext context,
        bool pushEnabled = false)
    {
        var agent = await engine.RentAgentAsync(context.Call.Tenant, null, CancellationToken.None);
        return await engine.StartAsync(context, push: null, IdempotencyClaim.None, agent, pushEnabled);
    }
}
