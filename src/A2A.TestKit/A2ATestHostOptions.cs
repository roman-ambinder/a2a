using A2A.Server;
using A2A.Server.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace A2A.TestKit;

/// <summary>Settings for <see cref="A2ATestHost"/>. Every property is optional.</summary>
public sealed class A2ATestHostOptions
{
    /// <summary>Changes the default card (<see cref="A2ATestHost.DefaultCard"/>), for example its name or capabilities.</summary>
    public Action<AgentCard>? ConfigureCard { get; set; }

    /// <summary>Changes the server options. Runs after the defaults of the test host (short push backoff, insecure and private webhook URLs allowed).</summary>
    public Action<A2AServerOptions>? ConfigureServer { get; set; }

    /// <summary>Changes the HTTP options of the A2A endpoints.</summary>
    public Action<A2AHttpOptions>? ConfigureHttp { get; set; }

    /// <summary>
    /// Changes the service collection. Runs after the SDK registrations and after the push invoker, so it can replace stores
    /// and the push invoker.
    /// </summary>
    public Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>
    /// The clock of the host. When set, it is registered as the <see cref="TimeProvider"/> singleton that the server reads for
    /// task time limits, push retries and expiry. Any provider works, including <see cref="TestClock"/>. Null keeps the real clock.
    /// </summary>
    public TimeProvider? TimeProvider { get; set; }

    /// <summary>When true, the A2A routes require an authenticated caller (send <c>X-Test-User</c>). The Agent Card stays public. Default false.</summary>
    public bool RequireAuthentication { get; set; }
}
