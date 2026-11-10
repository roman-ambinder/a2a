using System.Net;
using System.Text;
using System.Text.Json;

namespace A2A.Tests;

/// <summary>The A2A error rows of the binding table in spec §5.4: JSON-RPC code, gRPC status and HTTP status.</summary>
public class ErrorMappingTests
{
    [Theory]
    [InlineData(A2AErrorCode.TaskNotFound, -32001, "NOT_FOUND", 404)]
    [InlineData(A2AErrorCode.TaskNotCancelable, -32002, "FAILED_PRECONDITION", 409)]
    [InlineData(A2AErrorCode.PushNotificationNotSupported, -32003, "UNIMPLEMENTED", 400)]
    [InlineData(A2AErrorCode.UnsupportedOperation, -32004, "UNIMPLEMENTED", 400)]
    [InlineData(A2AErrorCode.ContentTypeNotSupported, -32005, "INVALID_ARGUMENT", 415)]
    [InlineData(A2AErrorCode.InvalidAgentResponse, -32006, "INTERNAL", 502)]
    [InlineData(A2AErrorCode.ExtendedAgentCardNotConfigured, -32007, "FAILED_PRECONDITION", 400)]
    [InlineData(A2AErrorCode.ExtensionSupportRequired, -32008, "FAILED_PRECONDITION", 400)]
    [InlineData(A2AErrorCode.VersionNotSupported, -32009, "UNIMPLEMENTED", 400)]
    public void A2A_errors_map_as_in_the_spec_table(A2AErrorCode code, int jsonRpc, string grpc, int http)
    {
        Assert.Equal(jsonRpc, (int)code);
        Assert.Equal(grpc, A2AErrors.GrpcStatus(code));
        Assert.Equal(http, A2AErrors.HttpStatus(code));
    }

    [Fact]
    public async Task Rest_cancel_of_a_completed_task_is_409_Conflict()
    {
        await using var host = await TestAgentHost.StartAsync();
        var task = (await host.Client(ProtocolBindings.HttpJson).SendMessageAsync("task")).RequireTask();
        Assert.Equal(TaskState.Completed, task.Status.State);

        using var http = host.Http("alice");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"/a2a/tasks/{task.Id}:cancel")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(A2AProtocol.Headers.Version, A2AProtocol.Version);
        using var resp = await http.SendAsync(req);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(409, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }
}
