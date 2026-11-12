using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

/// <summary>An in-memory <see cref="AgentSessionStore"/> that counts its calls.</summary>
public sealed class FakeSessionStore : AgentSessionStore
{
    private readonly ConcurrentDictionary<AgentSessionStoreKey, AgentSession> _sessions = new();
    private int _saves;
    private int _deletes;

    public int Saves => _saves;

    public int Deletes => _deletes;

    public IReadOnlyCollection<AgentSessionStoreKey> Keys => [.. _sessions.Keys];

    public override ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey key,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_sessions.TryGetValue(key, out var session) ? session : null);

    public override ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey key, AgentSession session,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _saves);
        _sessions[key] = session;
        return ValueTask.CompletedTask;
    }

    public override ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey key,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _deletes);
        _sessions.TryRemove(key, out _);
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
        builder.Services.AddAuthorization();
        builder.Services.AddA2A(a => addAgent(a.AddAgentCard(Card())))
            .Configure<A2AServerOptions>(o => o.AllowAnonymous = true);

        var app = builder.Build();
        app.UseA2A();
        app.UseAuthorization();
        app.MapA2A("/a2a");
        await app.StartAsync();
        return new AgentHost(app);
    }

    public A2AClient Client(string binding = ProtocolBindings.JsonRpc)
    {
        var options = new A2AClientOptions();
        var http = App.GetTestClient();
        IA2AClient transport = binding switch
        {
            ProtocolBindings.JsonRpc => new JsonRpcTransport(http, new Uri(BaseUrl), options),
            ProtocolBindings.HttpJson => new HttpJsonTransport(http, new Uri(BaseUrl), options),
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
