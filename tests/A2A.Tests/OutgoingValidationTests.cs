using System.Text.Json;
using A2A.Validation;

namespace A2A.Tests;

public class OutgoingValidationTests
{
    private static void Rejects(Action act, string message)
    {
        var ex = Assert.Throws<A2AException>(act);
        Assert.Equal(A2AErrorCode.InvalidParams, ex.Code);
        Assert.Equal(message, ex.Message);
    }

    private static SendMessageRequest Send(Role role = Role.User, List<Part>? parts = null, SendMessageConfiguration? cfg = null) => new()
    {
        Message = new Message { MessageId = "m", Role = role, Parts = parts ?? [Part.FromText("a")] },
        Configuration = cfg,
    };

    private static SendMessageConfiguration Push(TaskPushNotificationConfig p) => new() { TaskPushNotificationConfig = p };

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void GetTask_requires_id(string id) =>
        Rejects(() => A2AValidator.ValidateOutgoing(new GetTaskRequest { Id = id }), "id is required");

    [Fact]
    public void CancelTask_requires_id() =>
        Rejects(() => A2AValidator.ValidateOutgoing(new CancelTaskRequest { Id = "" }), "id is required");

    [Fact]
    public void Subscribe_requires_id() =>
        Rejects(() => A2AValidator.ValidateOutgoing(new SubscribeToTaskRequest { Id = "" }), "id is required");

    [Fact]
    public void GetPushConfig_requires_taskId_and_id()
    {
        Rejects(() => A2AValidator.ValidateOutgoing(new GetTaskPushNotificationConfigRequest { TaskId = "", Id = "c" }), "taskId is required");
        Rejects(() => A2AValidator.ValidateOutgoing(new GetTaskPushNotificationConfigRequest { TaskId = "t", Id = "" }), "id is required");
    }

    [Fact]
    public void DeletePushConfig_requires_taskId_and_id()
    {
        Rejects(() => A2AValidator.ValidateOutgoing(new DeleteTaskPushNotificationConfigRequest { TaskId = "", Id = "c" }), "taskId is required");
        Rejects(() => A2AValidator.ValidateOutgoing(new DeleteTaskPushNotificationConfigRequest { TaskId = "t", Id = "" }), "id is required");
    }

    [Fact]
    public void ListPushConfigs_requires_taskId() =>
        Rejects(() => A2AValidator.ValidateOutgoing(new ListTaskPushNotificationConfigsRequest { TaskId = "" }), "taskId is required");

    [Fact]
    public void CreatePushConfig_requires_url_taskId_and_scheme()
    {
        Rejects(() => A2AValidator.ValidateOutgoing(new TaskPushNotificationConfig { TaskId = "t", Url = "" }), "config.url is required");
        Rejects(() => A2AValidator.ValidateOutgoing(new TaskPushNotificationConfig { Url = "https://x" }), "config.taskId is required");
        Rejects(() => A2AValidator.ValidateOutgoing(new TaskPushNotificationConfig
        {
            TaskId = "t", Url = "https://x", Authentication = new() { Scheme = "" },
        }), "config.authentication.scheme is required");
    }

    [Fact]
    public void SendMessage_requires_role() =>
        Rejects(() => A2AValidator.ValidateOutgoing(Send(role: Role.Unspecified)), "message.role is required");

    [Fact]
    public void SendMessage_requires_part_content()
    {
        Rejects(() => A2AValidator.ValidateOutgoing(Send(parts: [new TextPart(null!)])), "message.parts[0].text is required");
        Rejects(() => A2AValidator.ValidateOutgoing(Send(parts: [Part.FromText("a"), new UrlPart(null!)])), "message.parts[1].url is required");
        Rejects(() => A2AValidator.ValidateOutgoing(Send(parts: [new DataPart(default)])), "message.parts[0].data is required");
    }

    [Fact]
    public void SendMessage_requires_nested_push_url_and_scheme()
    {
        Rejects(() => A2AValidator.ValidateOutgoing(Send(cfg: Push(new() { Url = "" }))),
            "configuration.taskPushNotificationConfig.url is required");
        Rejects(() => A2AValidator.ValidateOutgoing(Send(cfg: Push(new() { Url = "https://x", Authentication = new() { Scheme = " " } }))),
            "configuration.taskPushNotificationConfig.authentication.scheme is required");
    }

    [Fact]
    public void Accepts_valid_edge_cases()
    {
        A2AValidator.ValidateOutgoing(Send(cfg: Push(new() { Url = "https://x" })));
        A2AValidator.ValidateOutgoing(Send(parts: [Part.FromText("")]));
        A2AValidator.ValidateOutgoing(Send(parts: [Part.FromBytes(ReadOnlyMemory<byte>.Empty, "a/b")]));
        A2AValidator.ValidateOutgoing(Send(parts: [Part.FromData(JsonDocument.Parse("null").RootElement)]));
        A2AValidator.ValidateOutgoing(new TaskPushNotificationConfig { TaskId = "t", Url = "ftp://x" });
        A2AValidator.ValidateOutgoing(new TaskPushNotificationConfig
        {
            TaskId = "t", Url = "https://x",
            Authentication = new() { Scheme = A2A.Push.WebhookSignature.Scheme, Credentials = "not-a-secret" },
        });
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((SendMessageRequest)null!));
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((GetTaskRequest)null!));
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((CancelTaskRequest)null!));
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((SubscribeToTaskRequest)null!));
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((TaskPushNotificationConfig)null!));
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((GetTaskPushNotificationConfigRequest)null!));
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((DeleteTaskPushNotificationConfigRequest)null!));
        Assert.Throws<ArgumentNullException>(() => A2AValidator.ValidateOutgoing((ListTaskPushNotificationConfigsRequest)null!));
    }

    [Fact]
    public void Happy_path_does_not_allocate()
    {
        var send = Send(parts: [Part.FromText("a"), Part.FromUrl(new Uri("https://x/y"), "image/png"), Part.FromData(JsonDocument.Parse("{}").RootElement)],
            cfg: Push(new() { Url = "https://x", Authentication = new() { Scheme = "Bearer" } }));
        var get = new GetTaskRequest { Id = "t" };
        var cancel = new CancelTaskRequest { Id = "t" };
        var sub = new SubscribeToTaskRequest { Id = "t" };
        var create = new TaskPushNotificationConfig { TaskId = "t", Url = "https://x", Authentication = new() { Scheme = "Bearer" } };
        var getCfg = new GetTaskPushNotificationConfigRequest { TaskId = "t", Id = "c" };
        var delCfg = new DeleteTaskPushNotificationConfigRequest { TaskId = "t", Id = "c" };
        var listCfg = new ListTaskPushNotificationConfigsRequest { TaskId = "t" };

        void RunAll()
        {
            A2AValidator.ValidateOutgoing(send);
            A2AValidator.ValidateOutgoing(get);
            A2AValidator.ValidateOutgoing(cancel);
            A2AValidator.ValidateOutgoing(sub);
            A2AValidator.ValidateOutgoing(create);
            A2AValidator.ValidateOutgoing(getCfg);
            A2AValidator.ValidateOutgoing(delCfg);
            A2AValidator.ValidateOutgoing(listCfg);
        }

        for (var i = 0; i < 1000; i++) RunAll();
        var before = GC.GetAllocatedBytesForCurrentThread();
        RunAll();
        var after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(before, after);
    }
}
