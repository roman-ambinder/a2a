using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.Agents.AI;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.Extensions.AgentFramework.Tests;

/// <summary>
/// A scripted <see cref="AIAgent"/>. It records every run (messages, session, run options) and plays the updates its
/// script returns. It never touches the network.
/// </summary>
public sealed class FakeAIAgent(Func<FakeAIAgent.Call, CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> script) : AIAgent
{
    public sealed record Call(List<ChatMessage> Messages, AgentSession? Session, AgentRunOptions? Options);

    private readonly ConcurrentQueue<Call> _calls = new();

    /// <summary>Every run, oldest first.</summary>
    public IReadOnlyList<Call> Calls => [.. _calls];

    /// <summary>How many sessions <see cref="AIAgent.CreateSessionAsync(CancellationToken)"/> made.</summary>
    public int SessionsCreated => _sessionsCreated;

    private int _sessionsCreated;

    /// <summary>An agent that streams <paramref name="chunks"/> as assistant text.</summary>
    public static FakeAIAgent Replying(params string[] chunks) =>
        new((_, _) => Updates(chunks.Select(c => new AgentResponseUpdate(ChatRole.Assistant, c))));

    /// <summary>An agent whose updates depend on the call.</summary>
    public static FakeAIAgent Streaming(Func<Call, IEnumerable<AgentResponseUpdate>> updates) =>
        new((c, _) => Updates(updates(c)));

    public static async IAsyncEnumerable<AgentResponseUpdate> Updates(IEnumerable<AgentResponseUpdate> updates)
    {
        foreach (var u in updates) yield return u;
        await Task.CompletedTask;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _sessionsCreated);
        return ValueTask.FromResult<AgentSession>(new FakeSession());
    }

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(session.StateBag.Serialize());

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new FakeSession(AgentSessionStateBag.Deserialize(serializedState)));

    protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<AgentResponseUpdate>();
        await foreach (var u in RunCoreStreamingAsync(messages, session, options, cancellationToken)) updates.Add(u);
        return updates.ToAgentResponse();
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
        AgentSession? session = null, AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = new Call([.. messages], session, options);
        _calls.Enqueue(call);
        (session as FakeSession)?.Received.AddRange(call.Messages);
        await foreach (var u in script(call, cancellationToken).WithCancellation(cancellationToken))
            yield return u;
    }
}

/// <summary>
/// The session <see cref="FakeAIAgent"/> makes. <see cref="Received"/> lists the messages runs on it were given, in order.
/// </summary>
public sealed class FakeSession : AgentSession
{
    public FakeSession() { }

    public FakeSession(AgentSessionStateBag stateBag) : base(stateBag) { }

    public List<ChatMessage> Received { get; } = [];
}

/// <summary>An in-memory <see cref="AgentSessionStore"/> that counts its calls and records the keys.</summary>
public sealed class FakeSessionStore : AgentSessionStore
{
    private readonly ConcurrentDictionary<AgentSessionStoreKey, AgentSession> _sessions = new();
    private readonly ConcurrentQueue<AgentSessionStoreKey> _saved = new();
    private readonly ConcurrentQueue<AgentSessionStoreKey> _deleted = new();
    private int _saves;
    private int _deletes;

    public int Saves => _saves;

    public int Deletes => _deletes;

    public IReadOnlyCollection<AgentSessionStoreKey> Keys => [.. _sessions.Keys];

    /// <summary>Every key saved, in order.</summary>
    public IReadOnlyList<AgentSessionStoreKey> SavedKeys => [.. _saved];

    /// <summary>Every key deleted, in order.</summary>
    public IReadOnlyList<AgentSessionStoreKey> DeletedKeys => [.. _deleted];

    /// <summary>When true, a save never completes and ignores its token, like a store that hangs.</summary>
    public bool HangOnSave { get; set; }

    /// <summary>When true, a delete throws, like a store that is down.</summary>
    public bool FailOnDelete { get; set; }

    /// <summary>
    /// When true, sessions are kept as the JSON of <see cref="AIAgent.SerializeSessionAsync"/> and read back with
    /// <see cref="AIAgent.DeserializeSessionAsync"/>, as a real store does. Each get then returns a new session object.
    /// </summary>
    public bool SerializeSessions { get; set; }

    private readonly ConcurrentDictionary<AgentSessionStoreKey, JsonElement> _json = new();

    /// <summary>
    /// With <see cref="SerializeSessions"/>: every get returns the first copy saved for the key, like a lagging replica.
    /// </summary>
    public bool ServeFirstSave { get; set; }

    public override async ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key,
        CancellationToken cancellationToken = default)
    {
        if (!SerializeSessions) return _sessions.TryGetValue(key, out var session) ? session : null;
        return _json.TryGetValue(key, out var json) ? await agent.DeserializeSessionAsync(json, cancellationToken: cancellationToken) : null;
    }

    public override async ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _saves);
        if (HangOnSave) await new TaskCompletionSource().Task;
        _saved.Enqueue(key);
        _sessions[key] = session;
        if (SerializeSessions)
        {
            // A round trip through text, as a database would keep it.
            var json = await agent.SerializeSessionAsync(session, cancellationToken: cancellationToken);
            var copy = JsonDocument.Parse(json.GetRawText()).RootElement.Clone();
            if (ServeFirstSave) _json.TryAdd(key, copy);
            else _json[key] = copy;
        }
    }

    public override ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _deletes);
        if (FailOnDelete) throw new IOException("store down");
        _deleted.Enqueue(key);
        _sessions.TryRemove(key, out _);
        _json.TryRemove(key, out _);
        return ValueTask.CompletedTask;
    }
}

public static class Bindings
{
    public static TheoryData<string> All => new() { ProtocolBindings.JsonRpc, ProtocolBindings.HttpJson };
}

/// <summary>A scripted <see cref="IChatClient"/>, to run <c>ChatClientAgentExecutor</c> side by side with the adapter.</summary>
public sealed class ScriptedChatClient(Func<List<ChatMessage>, IEnumerable<ChatResponseUpdate>> script) : IChatClient
{
    private readonly ConcurrentQueue<List<ChatMessage>> _calls = new();

    public IReadOnlyList<List<ChatMessage>> Calls => [.. _calls];

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(u);
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var call = messages.ToList();
        _calls.Enqueue(call);
        foreach (var u in script(call)) yield return u;
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

/// <summary>A server hosting an agent over the ASP.NET test server. No network.</summary>
public sealed class AgentHost : IAsyncDisposable
{
    public const string BaseUrl = "http://localhost/a2a";

    private AgentHost(WebApplication app) => App = app;

    public WebApplication App { get; }

    public static AgentCard Card() => new()
    {
        Name = "Agent Framework Agent",
        Description = "Test agent",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = BaseUrl, ProtocolBinding = ProtocolBindings.HttpJson },
        ],
        Capabilities = new AgentCapabilities { Streaming = true },
        DefaultInputModes = ["text/plain", "application/json"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "chat", Name = "Chat", Description = "Chats", Tags = ["test"] }],
    };

    /// <summary>Hosts <paramref name="agent"/> with <c>AddAIAgent</c>.</summary>
    public static Task<AgentHost> StartAsync(AIAgent agent, Action<AIAgentExecutorOptions>? configure = null) =>
        StartAsync(a => a.AddAIAgent(configure), s => s.AddSingleton(agent));

    /// <summary>Hosts whatever <paramref name="addAgent"/> registers, with <paramref name="services"/> added first.</summary>
    public static async Task<AgentHost> StartAsync(Func<A2AServerBuilder, A2AServerBuilder> addAgent,
        Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        services?.Invoke(builder.Services);
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        builder.Services.AddA2A(a => addAgent(a.AddAgentCard(Card())))
            .Configure(o => o.AllowAnonymous = true);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapA2A("/a2a");
        await app.StartAsync();
        return new AgentHost(app);
    }

    /// <summary>
    /// A client. <paramref name="user"/> authenticates as that principal (anonymous when null); <paramref name="tenant"/>
    /// calls the <c>/a2a/{tenant}</c> routes.
    /// </summary>
    public A2AClient Client(string binding = ProtocolBindings.JsonRpc, string? user = null, string? tenant = null)
    {
        var options = new A2AClientOptions();
        var http = App.GetTestClient();
        if (user is not null) http.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        var url = new Uri(tenant is null ? BaseUrl : $"{BaseUrl}/{tenant}");
        IA2ATransport transport = binding switch
        {
            ProtocolBindings.JsonRpc => new JsonRpcTransport(http, url, options),
            ProtocolBindings.HttpJson => new HttpJsonTransport(http, url, options),
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, null),
        };
        return new A2AClient(transport, options, Card());
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>Authenticates requests that carry <see cref="UserHeader"/>, as that user (<c>sub</c> claim).</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].ToString();
        if (string.IsNullOrEmpty(user)) return Task.FromResult(AuthenticateResult.NoResult());
        var identity = new ClaimsIdentity([new Claim("sub", user), new Claim(ClaimTypes.Name, user)], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>An <see cref="IA2AClient"/> whose answers the test scripts (copied from A2A.Extensions.AI.Tests). Used where the real server would hide the case (a task that stays working).</summary>
public sealed class FakeTransport : IA2ATransport
{
    public Func<SendMessageRequest, CancellationToken, Task<SendMessageResponse>> Send { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<SendMessageRequest, CancellationToken, IAsyncEnumerable<StreamResponse>> Stream { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<GetTaskRequest, CancellationToken, Task<AgentTask>> Get { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<CancelTaskRequest, CancellationToken, Task<AgentTask>> Cancel { get; set; } = (_, _) => throw new NotSupportedException();
    public Func<SubscribeToTaskRequest, CancellationToken, IAsyncEnumerable<StreamResponse>> Subscribe { get; set; } = (_, _) => throw new NotSupportedException();
    public List<SendMessageRequest> Sent { get; } = [];
    public List<string> Reads { get; } = [];

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

    public Task<AgentTask> GetTaskAsync(GetTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Reads) Reads.Add("get:" + request.Id);
        return Get(request, cancellationToken);
    }
    public Task<AgentTask> CancelTaskAsync(CancelTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => Cancel(request, cancellationToken);

    public Task<ListTasksResponse> ListTasksAsync(ListTasksRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamResponse> SubscribeToTaskAsync(SubscribeToTaskRequest request, A2ARequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        lock (Reads) Reads.Add("subscribe:" + request.Id);
        return Subscribe(request, cancellationToken);
    }
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
