using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace A2A.Extensions.AI.Tests;

/// <summary>A scripted <see cref="IChatClient"/>. It records what it was asked and never touches the network.</summary>
public sealed class FakeChatClient(Func<FakeChatClient.Call, CancellationToken, IAsyncEnumerable<ChatResponseUpdate>> script) : IChatClient
{
    public sealed record Call(List<ChatMessage> Messages, ChatOptions? Options);

    public List<Call> Calls { get; } = [];

    public static FakeChatClient Replying(params string[] chunks) => new((_, _) => Updates(chunks.Select(c => new ChatResponseUpdate(ChatRole.Assistant, c))));

    public static FakeChatClient Streaming(Func<Call, IEnumerable<ChatResponseUpdate>> updates) => new((c, _) => Updates(updates(c)));

    public static async IAsyncEnumerable<ChatResponseUpdate> Updates(IEnumerable<ChatResponseUpdate> updates)
    {
        foreach (var u in updates) yield return u;
        await Task.CompletedTask;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(u);
        return updates.ToChatResponse();
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var call = new Call(messages.ToList(), options);
        lock (Calls) Calls.Add(call);
        return script(call, cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>A server hosting a chat client as an A2A agent, over the ASP.NET test server. No network.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    public const string BaseUrl = "http://localhost/a2a";

    private AgentHost(WebApplication app) => App = app;

    public WebApplication App { get; }

    public static AgentCard Card() => new()
    {
        Name = "Chat Agent",
        Description = "Test agent",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.HttpJson },
        ],
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain", "application/json", "image/*"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "chat", Name = "Chat", Description = "Chats", Tags = ["test"] }],
    };

    public static async Task<AgentHost> StartAsync(IChatClient chat, Action<ChatClientAgentOptions>? configure = null, object? serviceKey = null,
        Func<A2AServerBuilder, A2AServerBuilder>? before = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (serviceKey is null) builder.Services.AddSingleton(chat);
        else builder.Services.AddKeyedSingleton(serviceKey, chat);
        builder.Services.AddAuthorization();
        builder.Services.AddA2A(a => (before?.Invoke(a) ?? a).AddAgentCard(Card()).AddChatClientAgent(configure, serviceKey))
            .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);

        var app = builder.Build();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/a2a");
        await app.StartAsync();
        return new AgentHost(app);
    }

    public A2AClient Client(string binding, A2AClientOptions? options = null, AgentCard? card = null)
    {
        options ??= new A2AClientOptions();
        var http = App.GetTestClient();
        IA2AClient transport = binding switch
        {
            ProtocolBindings.JsonRpc => new JsonRpcTransport(http, new Uri(BaseUrl), options),
            ProtocolBindings.HttpJson => new HttpJsonTransport(http, new Uri(BaseUrl), options),
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, null),
        };
        return new A2AClient(transport, options, card ?? Card());
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

public static class Bindings
{
    public static TheoryData<string> All => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };
}

/// <summary>An <see cref="IA2AClient"/> whose answers the test scripts. Used where the real server would hide the case (a task that stays working).</summary>
public sealed class FakeTransport : IA2AClient
{
    public Func<SendMessageRequest, CancellationToken, Task<SendMessageResponse>> Send { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<SendMessageRequest, CancellationToken, IAsyncEnumerable<StreamResponse>> Stream { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<GetTaskRequest, CancellationToken, Task<AgentTask>> Get { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<CancelTaskRequest, CancellationToken, Task<AgentTask>> Cancel { get; set; } = (_, _) => throw new NotSupportedException();
    public List<SendMessageRequest> Sent { get; } = [];

    public Task<SendMessageResponse> SendMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add(request);
        return Send(request, cancellationToken);
    }

    public IAsyncEnumerable<StreamResponse> SendStreamingMessageAsync(SendMessageRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Sent) Sent.Add(request);
        return Stream(request, cancellationToken);
    }

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => Get(request, cancellationToken);
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => Cancel(request, cancellationToken);

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TaskPushNotificationConfig> CreateTaskPushNotificationConfigAsync(TaskPushNotificationConfig config, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TaskPushNotificationConfig> GetTaskPushNotificationConfigAsync(GetTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ListTaskPushNotificationConfigsResponse> ListTaskPushNotificationConfigsAsync(ListTaskPushNotificationConfigsRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteTaskPushNotificationConfigAsync(DeleteTaskPushNotificationConfigRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AgentCard> GetExtendedAgentCardAsync(GetExtendedAgentCardRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public A2AClient AsClient(A2AClientOptions? options = null, AgentCard? card = null) => new(this, options, card);

    public static AgentTask TaskIn(TaskState state, string id = "t1", string contextId = "c1", Message? message = null, List<Artifact>? artifacts = null) => new()
    {
        Id = id, ContextId = contextId, Status = new AgentTaskStatus { State = state, Message = message }, Artifacts = artifacts,
    };

    public static SendMessageResponse Respond(AgentTask task) => new TaskResponse(task);
}
