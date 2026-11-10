using System.Net;
using System.Text;
using System.Text.Json;

namespace A2A.Tests;

/// <summary>
///     The A2A error rows of the binding table in spec §5.4 (v1.0.1, unchanged in v1.1.0-rc): JSON-RPC code, gRPC status
///     and HTTP status. v1.0.0 had a different table (409, 415, 502 and UNIMPLEMENTED in places); v1.0.1 replaced it, and
///     the A2A TCK checks the v1.0.1 values.
/// </summary>
public class ErrorMappingTests
{
    [Theory]
    [InlineData(A2AErrorCode.TaskNotFound, -32001, "NOT_FOUND", 404)]
    [InlineData(A2AErrorCode.TaskNotCancelable, -32002, "FAILED_PRECONDITION", 400)]
    [InlineData(A2AErrorCode.PushNotificationNotSupported, -32003, "FAILED_PRECONDITION", 400)]
    [InlineData(A2AErrorCode.UnsupportedOperation, -32004, "FAILED_PRECONDITION", 400)]
    [InlineData(A2AErrorCode.ContentTypeNotSupported, -32005, "INVALID_ARGUMENT", 400)]
    [InlineData(A2AErrorCode.InvalidAgentResponse, -32006, "INTERNAL", 500)]
    [InlineData(A2AErrorCode.ExtendedAgentCardNotConfigured, -32007, "FAILED_PRECONDITION", 400)]
    [InlineData(A2AErrorCode.ExtensionSupportRequired, -32008, "FAILED_PRECONDITION", 400)]
    [InlineData(A2AErrorCode.VersionNotSupported, -32009, "FAILED_PRECONDITION", 400)]
    public void A2A_errors_map_as_in_the_spec_table(A2AErrorCode code, int jsonRpc, string grpc, int http)
    {
        Assert.Equal(jsonRpc, (int)code);
        Assert.Equal(grpc, A2AErrors.GrpcStatus(code));
        Assert.Equal(http, A2AErrors.HttpStatus(code));
    }

    [Fact]
    public async Task Rest_cancel_of_a_completed_task_is_400()
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

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Equal("FAILED_PRECONDITION", error.GetProperty("status").GetString());
    }

    [Fact]
    public void InvalidField_keeps_the_message_and_adds_a_BadRequest()
    {
        var ex = A2AException.InvalidField("message.messageId", "is required");
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal("message.messageId is required", ex.Message);
        var violation = AssertBadRequest(A2AErrors.BuildDetails(ex));
        Assert.Equal("message.messageId", violation.GetProperty("field").GetString());
        Assert.Equal("is required", violation.GetProperty("description").GetString());
    }

    [Fact]
    public void Details_without_a_type_are_not_sent()
    {
        using var doc = JsonDocument.Parse("""[{"note":"untyped"},"text",{"@type":""},{"@type":"type.example/Ok","x":1}]""");
        var ex = new A2AException(A2AErrorCode.Internal, "boom") { Details = doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList() };
        var details = A2AErrors.BuildDetails(ex);
        Assert.All(details, d => Assert.True(A2AErrors.HasType(d)));
        Assert.Equal([A2AErrors.ErrorInfoType, "type.example/Ok"], details.Select(d => d.GetProperty("@type").GetString()));
    }

    public static TheoryData<string> Bindings => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson, ProtocolBindings.Grpc };

    [Theory, MemberData(nameof(Bindings))]
    public async Task A_validation_error_carries_a_BadRequest_on_every_binding(string binding)
    {
        await using var host = await TestAgentHost.StartAsync(grpc: true);
        var client = host.Client(binding);
        var task = (await client.SendMessageAsync("task")).RequireTask();

        var ex = await Assert.ThrowsAsync<A2AException>(() => client.CreateTaskPushNotificationConfigAsync(
            new TaskPushNotificationConfig { TaskId = task.Id, Url = "ftp://hooks.test/x" }));

        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        var violation = AssertBadRequest(ex.Details!);
        Assert.Equal("config.url", violation.GetProperty("field").GetString());
        Assert.Equal("must be an absolute http(s) URL", violation.GetProperty("description").GetString());
    }

    private static JsonElement AssertBadRequest(IEnumerable<JsonElement> details)
    {
        var badRequest = Assert.Single(details, d => d.GetProperty("@type").GetString() == A2AErrors.BadRequestType);
        return Assert.Single(badRequest.GetProperty("fieldViolations").EnumerateArray());
    }

    [Fact]
    public void ErrorInfo_metadata_is_read_from_the_A2A_entry_only()
    {
        using var doc = JsonDocument.Parse("""
            [{"@type":"type.googleapis.com/google.rpc.ErrorInfo","domain":"other.example","metadata":{"a":"wrong"}},
             {"@type":"type.googleapis.com/google.rpc.ErrorInfo","domain":"a2a-protocol.org","reason":"TASK_NOT_FOUND",
              "metadata":{"taskId":"t1","retryable":"false","n":1}}]
            """);
        var metadata = A2AErrors.GetErrorInfoMetadata(doc.RootElement.EnumerateArray().ToList());

        Assert.Equal(new Dictionary<string, string> { ["taskId"] = "t1", ["retryable"] = "false" }, metadata);
        Assert.Null(A2AErrors.GetErrorInfoMetadata(null));
        using var none = JsonDocument.Parse("""[{"@type":"type.googleapis.com/google.rpc.ErrorInfo","domain":"a2a-protocol.org"}]""");
        Assert.Null(A2AErrors.GetErrorInfoMetadata(none.RootElement.EnumerateArray().ToList()));
    }
}
