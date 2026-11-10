using System.Text.Json;
using System.Text.Json.Serialization;
using A2A.Client;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Grpc;
using A2A.Server.Pipeline;
using A2A.Server.Storage;

namespace Snippets.CustomOperations;

#region Types
internal sealed record ReportRequest(string Name, int Year);

internal sealed record ReportResult(string Url);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReportRequest))]
[JsonSerializable(typeof(ReportResult))]
internal sealed partial class ReportJson : JsonSerializerContext;
#endregion

internal static class Snippets
{
    private const string ReportsUri = "https://example.com/extensions/reports/v1";

    public static void RegisterRaw(IServiceCollection services)
    {
        #region Raw
        services.AddA2A(a => a
            .AddAgentExecutor<Agent>()
            .AddExtensionOperation("reports.ping", ReportsUri,
                (context, parameters, cancellationToken) =>
                    ValueTask.FromResult(JsonSerializer.SerializeToElement(new { ok = true, caller = context.Call.Principal })),
                allow: true));
        #endregion
    }

    public static void RegisterTyped(IServiceCollection services)
    {
        #region Typed
        services.AddA2A(a => a
            .AddAgentExecutor<Agent>()
            .AddExtensionOperation("reports.create", ReportsUri,
                ReportJson.Default.ReportRequest, ReportJson.Default.ReportResult,
                (context, request, cancellationToken) =>
                    ValueTask.FromResult(new ReportResult($"https://example.com/reports/{request.Year}/{request.Name}")),
                authorizationPolicy: "reports"));
        #endregion
    }

    #region Owner
    // A custom operation has no task of its own. To touch a task, key it with the tenant and the principal of the call.
    internal static async ValueTask<JsonElement> ReadStatusAsync(TaskStore tasks, A2AOperationContext context, string taskId, CancellationToken cancellationToken)
    {
        var key = new TaskKey(context.Call.Tenant, context.Call.Principal, taskId);
        var record = await tasks.GetAsync(key, cancellationToken);
        return JsonSerializer.SerializeToElement(new { found = record is not null });
    }
    #endregion

    public static void MapGrpc(WebApplication app)
    {
        #region MapGrpc
#pragma warning disable A2AEXP004 // the gRPC extension service is experimental
        app.MapA2AGrpcExtensions().RequireAuthorization();
#pragma warning restore A2AEXP004
        #endregion
    }

    public static async Task CallTyped(IA2AClient client)
    {
        #region ClientTyped
        var result = await client.InvokeExtensionOperationAsync(
            "reports.create", ReportsUri,
            new ReportRequest("sales", 2026),
            ReportJson.Default.ReportRequest, ReportJson.Default.ReportResult);
        Console.WriteLine(result.Url);
        #endregion
    }

    public static async Task CallRaw(IA2AClient client)
    {
        #region ClientRaw
        using var parameters = JsonDocument.Parse("""{"verbose":true}""");
        var result = await client.InvokeExtensionOperationAsync("reports.ping", ReportsUri, parameters.RootElement);
        Console.WriteLine(result.GetRawText());
        #endregion
    }

    private sealed class Agent : IAgentExecutor
    {
        public Task ExecuteAsync(AgentRequestContext context, TaskUpdater updater, CancellationToken cancellationToken) =>
            updater.CompleteAsync("done", cancellationToken).AsTask();
    }
}
