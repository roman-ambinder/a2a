using A2A.Client;
using A2A.Client.Transports;
using A2A.Server;
using A2A.Server.AspNetCore;
using A2A.Server.Push;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace A2A.TestKit;

/// <summary>
/// An A2A agent host that runs in the test process, without a network port. Create one with a <c>StartAsync</c> method, talk to it
/// with <see cref="CreateClient"/> or <see cref="CreateHttpClient"/>, and dispose it at the end of the test.
/// </summary>
/// <remarks>
/// It serves the JSON-RPC and HTTP+JSON bindings (no gRPC) under <see cref="DefaultBaseUrl"/> with in-memory stores. Webhooks go
/// to <see cref="Push"/>, not to the network. A caller is anonymous unless it sends the <c>X-Test-User</c> header, which
/// <see cref="CreateHttpClient"/> and <see cref="CreateClient"/> set from their <c>user</c> argument.
/// </remarks>
public sealed class A2ATestHost : IAsyncDisposable
{
    /// <summary>The base URL of the A2A endpoints and of the interfaces on <see cref="DefaultCard"/>.</summary>
    public const string DefaultBaseUrl = "http://localhost/a2a";

    private readonly WebApplication _app;
    private int _disposed;

    private A2ATestHost(WebApplication app, AgentCard card, TestPushReceiver push, TimeProvider? clock)
    {
        _app = app;
        Card = card;
        Push = push;
        Clock = clock;
    }

    /// <summary>The Agent Card the host serves, after <see cref="A2ATestHostOptions.ConfigureCard"/> and <see cref="A2ATestHostOptions.ConfigureServer"/> ran.</summary>
    public AgentCard Card { get; }

    /// <summary>The URL of the A2A endpoints (<see cref="DefaultBaseUrl"/>).</summary>
    public Uri Url { get; } = new(DefaultBaseUrl);

    /// <summary>The services of the host.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>The request handler of the host. Use it to call the server without HTTP.</summary>
    public A2ARequestHandler Handler => _app.Services.GetRequiredService<A2ARequestHandler>();

    /// <summary>The webhook endpoint. The host sends push notifications here. Register <see cref="TestPushReceiver.DefaultUrl"/> as the webhook URL.</summary>
    public TestPushReceiver Push { get; }

    /// <summary>The clock the host uses (<see cref="A2ATestHostOptions.TimeProvider"/>), or null when it uses the real clock.</summary>
    public TimeProvider? Clock { get; }

    /// <summary>A card for a streaming agent with push notifications, JSON-RPC and HTTP+JSON interfaces at <see cref="DefaultBaseUrl"/>, and one skill.</summary>
    public static AgentCard DefaultCard() => new()
    {
        Name = "Test Agent",
        Description = "An agent under test.",
        Version = "1.0.0",
        SupportedInterfaces =
        [
            new AgentInterface { Url = DefaultBaseUrl, ProtocolBinding = ProtocolBindings.JsonRpc },
            new AgentInterface { Url = DefaultBaseUrl, ProtocolBinding = ProtocolBindings.HttpJson },
        ],
        Capabilities = new AgentCapabilities { Streaming = true, PushNotifications = true },
        DefaultInputModes = ["text/plain"],
        DefaultOutputModes = ["text/plain"],
        Skills = [new AgentSkill { Id = "test", Name = "Test", Description = "Runs the agent under test.", Tags = ["test"] }],
    };

    /// <summary>Starts a host that runs <typeparamref name="TExecutor"/> as the agent. A new instance runs for each message.</summary>
    public static Task<A2ATestHost> StartAsync<TExecutor>(Action<A2ATestHostOptions>? configure = null) where TExecutor : class, IAgentExecutor =>
        StartWithBuilderAsync(b => b.AddAgentExecutor<TExecutor>(), configure);

    /// <summary>Starts a host that runs <paramref name="executor"/> as the agent. The same instance serves every message, so its <c>CancelAsync</c> is used too.</summary>
    public static Task<A2ATestHost> StartAsync(IAgentExecutor executor, Action<A2ATestHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        return StartWithBuilderAsync(b => b.Services.Replace(ServiceDescriptor.Singleton(executor)), configure);
    }

    /// <summary>Starts a host that runs <paramref name="execute"/> as the agent.</summary>
    public static Task<A2ATestHost> StartAsync(Func<AgentRequestContext, TaskUpdater, CancellationToken, Task> execute, Action<A2ATestHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        return StartWithBuilderAsync(b => b.AddAgentExecutor(execute), configure);
    }

    /// <summary>Starts a host and lets <paramref name="configureAgent"/> set up the agent on the server builder, for example with <c>AddChatClientAgent</c> or a tenant resolver.</summary>
    public static async Task<A2ATestHost> StartWithBuilderAsync(Action<A2AServerBuilder> configureAgent, Action<A2ATestHostOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configureAgent);
        var options = new A2ATestHostOptions();
        configure?.Invoke(options);

        var card = DefaultCard();
        options.ConfigureCard?.Invoke(card);
        var push = new TestPushReceiver();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
        builder.Services.AddAuthorization();
        if (options.TimeProvider is { } clock) builder.Services.Replace(ServiceDescriptor.Singleton(clock));
        var agent = builder.Services.AddA2AServer(o =>
        {
            o.AgentCard = card;
            o.AllowAnonymous = !options.RequireAuthentication;
            o.PushNotifications.AllowInsecureUrls = true;
            o.PushNotifications.AllowPrivateNetworks = true;
            o.PushDelivery.InitialBackoff = TimeSpan.FromMilliseconds(10);
            o.PushDelivery.PollInterval = TimeSpan.FromMilliseconds(20);
            o.CancelGracePeriod = TimeSpan.FromSeconds(2);
            // A fake clock cannot end the drain wait, so a running agent would hang the disposal. Stop agents at once instead.
            o.ShutdownDrainTimeout = TimeSpan.Zero;
            options.ConfigureServer?.Invoke(o);
        });
        configureAgent(agent);
        builder.Services.AddA2AServerAspNetCore();
        builder.Services.AddKeyedSingleton(PushDeliveryService.InvokerServiceKey, push.CreateInvoker());
        if (options.ConfigureHttp is { } http) builder.Services.ConfigureA2AHttp(http);
        options.ConfigureServices?.Invoke(builder.Services);

        var app = builder.Build();
        try
        {
            app.UseAuthentication();
            app.UseA2A();
            app.UseAuthorization();
            app.MapAgentCard();
            var routes = app.MapA2A("/a2a");
            if (options.RequireAuthentication) routes.RequireAuthorization();
            await app.StartAsync().ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new A2ATestHost(app, app.Services.GetRequiredService<IOptions<A2AServerOptions>>().Value.AgentCard, push, options.TimeProvider);
    }

    /// <summary>Creates the in-memory HTTP handler that reaches the host. It sends no <c>X-Test-User</c> header.</summary>
    public HttpMessageHandler CreateHandler() => _app.GetTestServer().CreateHandler();

    /// <summary>Creates an <see cref="HttpClient"/> for the host. With <paramref name="user"/> every request carries <c>X-Test-User</c>, so the caller is authenticated as that user.</summary>
    public HttpClient CreateHttpClient(string? user = null)
    {
        var client = _app.GetTestClient();
        if (user is not null) client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        return client;
    }

    /// <summary>Creates an A2A client for the host over <paramref name="binding"/> (JSON-RPC or HTTP+JSON), authenticated as <paramref name="user"/> when given.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="binding"/> is not JSON-RPC or HTTP+JSON. The kit has no gRPC.</exception>
    public A2AClient CreateClient(string binding = ProtocolBindings.JsonRpc, string? user = null, A2AClientOptions? options = null) =>
        CreateTransportClient(CreateHttpClient(user), Url, Card, binding, options);

    internal static A2AClient CreateTransportClient(HttpClient http, Uri url, AgentCard card, string binding, A2AClientOptions? options)
    {
        options ??= new A2AClientOptions();
        IA2AClient transport = binding switch
        {
            ProtocolBindings.JsonRpc => new JsonRpcTransport(http, url, options),
            ProtocolBindings.HttpJson => new HttpJsonTransport(http, url, options),
            _ => throw new ArgumentOutOfRangeException(nameof(binding), binding, "Only JSON-RPC and HTTP+JSON are supported."),
        };
        return new A2AClient(transport, options, card);
    }

    /// <summary>
    /// Waits until no agent runs and, with the in-memory push outbox, every push delivery was sent or dropped. Polls in real time
    /// every 5 ms, so a push retry that waits on a fake clock does not finish until you advance the clock.
    /// </summary>
    /// <param name="timeout">How long to wait, in real time. Default 30 seconds.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="A2AAssertionException">The host was not idle in time.</exception>
    public async Task WaitIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Handler.WhenIdleAsync().WaitAsync(limit, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new A2AAssertionException($"The host was not idle within {limit}: an agent is still running. If it waits on a fake clock, advance the clock.");
        }
        if (_app.Services.GetService<PushOutbox>() is not InMemoryPushOutbox outbox) return;
        while (outbox.Count is var pending and > 0)
        {
            if (watch.Elapsed >= limit)
                throw new A2AAssertionException($"{pending} push deliveries were still pending after {limit}. If a retry waits on a fake clock, advance the clock.");
            await Task.Delay(TimeSpan.FromMilliseconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Stops the host and releases it. Safe to call more than once.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
