# A2A .NET SDK

A .NET SDK for the [Agent2Agent (A2A) protocol](https://a2a-protocol.org/latest/specification/) **v1.0**.
Client and server. JSON-RPC and HTTP+JSON bindings. SSE streaming. Push notifications. Agent Card signing.

Targets **.NET 8** and **.NET 10**. No third-party dependencies beyond `Microsoft.Extensions.*`.

| Package | What it is |
|---|---|
| `A2A.Protocol` | Data model (from `a2a.proto`), ProtoJSON serialization, errors, JCS (RFC 8785), JWS card signing/verification |
| `A2A.Client` | `A2AClient`, JSON-RPC + REST transports, SSE reader, card resolver with caching and signature policy, DI |
| `A2A.Server` | Transport-agnostic server core: executor model, task lifecycle, stores, event bus, push sender |
| `A2A.Server.AspNetCore` | Endpoints: JSON-RPC, REST, SSE, `/.well-known/agent-card.json`, JWKS |

## Server in 20 lines

```csharp
builder.Services.AddA2AServer(o => o.AgentCard = new AgentCard
{
    Name = "My Agent", Description = "...", Version = "1.0.0",
    SupportedInterfaces =
    [
        new() { Url = "https://agent.example.com/a2a", ProtocolBinding = ProtocolBindings.JsonRpc },
        new() { Url = "https://agent.example.com/a2a", ProtocolBinding = ProtocolBindings.HttpJson },
    ],
    Capabilities = new() { Streaming = true, PushNotifications = true },
    DefaultInputModes = ["text/plain"], DefaultOutputModes = ["text/plain"],
    Skills = [new() { Id = "x", Name = "X", Description = "...", Tags = ["x"] }],
}).AddAgentExecutor<MyAgent>();
builder.Services.AddA2AServerAspNetCore();   // registers the startup check and the default rate limit (required by MapA2A)

app.UseAuthentication();
app.UseA2A();                               // rate limiter middleware; the app refuses to start without it (or o.EnableRateLimiting = false)
app.UseAuthorization();

app.MapAgentCard();                         // GET /.well-known/agent-card.json (anonymous, cacheable)
app.MapA2A("/a2a").RequireAuthorization();  // JSON-RPC at POST /a2a, REST at /a2a/message:send, /a2a/tasks/...
// Without RequireAuthorization() the app refuses to start, unless you set o.AllowAnonymous = true.
// Limits (o.Limits): body 10 MB, 100 parts per message, JSON depth 32, history 100, page size 100. Rate limit per tenant and
// principal: 20 burst, 60 per minute (services.AddA2ARateLimiting(o => ...) to change). Both answer 429/413 with the right headers.
// Tenants: MapA2A("/{tenant}/a2a") or the default /a2a/{tenant} variants set ServerCallContext.Tenant.

class MyAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext ctx, TaskUpdater updater, CancellationToken ct)
    {
        await updater.StartWorkAsync(cancellationToken: ct);
        await updater.AddArtifactAsync([Part.FromText($"You said: {ctx.UserText}")], name: "answer", cancellationToken: ct);
        await updater.CompleteAsync(cancellationToken: ct);
    }
}
```

The executor is resolved from a **new DI scope per execution**, so scoped services (DbContext etc.) are safe.
Call `updater.ReplyAsync(...)` instead for a stateless reply with no task.

## Client

```csharp
var client = await A2AClientFactory.CreateAsync(new Uri("https://agent.example.com/"), httpClient);
var reply = await client.SendMessageAsync("hello");

await foreach (var evt in client.SendStreamingMessageAsync(Message.FromUser("stream this")))
    Console.WriteLine(evt.StatusUpdate?.Status.State);
```

DI: `services.AddA2AClient("weather", o => o.AgentUrl = ...).AddBearerToken(...)`, then `IA2AClientFactory.GetClientAsync("weather")`.

The factory picks the first interface **in the card's order** that the client supports (spec §8.3.2), and sets `tenant` from it.

## Behavior and spec decisions

These are places where the spec is loose or self-contradictory. Here is what this SDK does.

**Security (stricter than the spec's SHOULDs)**
- **Task ownership** (§13.1): each task is bound to the caller id (`sub` → NameIdentifier → Name) and tenant. Other callers get `TaskNotFound` and never learn the task exists. `ListTasks` filters before counting. Anonymous callers share one scope, so put `RequireAuthorization()` on the endpoints in production.
- **Push SSRF guard**: HTTPS required. Loopback, private, link-local (incl. cloud metadata 169.254/16), CGNAT, and ULA targets are rejected, both at registration and **at connect time against the resolved IP** (blocks DNS rebinding). Redirects are disabled.
- **Card verification**: algorithm allowlist (ES256/384/512, RS*, PS*; never `none` or HS*). Unknown `crit` is rejected. Key type must match `alg`. `jku` is only followed over HTTPS to the card's own host (configurable). `SignaturePolicy.Require` rejects unsigned cards.
- **Executor errors** become a `Failed` task with a generic message. Exception details are logged, not sent.

**Signing and forward compatibility**
- The verifier canonicalizes the **raw received JSON**, not a re-serialized model. Fields this SDK doesn't know (newer versions, extensions) stay covered by the signature. Both the spec's normalized form (§8.4.1) and the verbatim form are accepted.

**Protocol choices**
- **Missing `A2A-Version`** means 0.3 per §3.6.2 and is rejected. Set `TreatMissingVersionAsCurrent = true` to accept curl-style calls.
- **Extensions**: required extensions are enforced. The server **echoes the activated extensions** in the `A2A-Extensions` response header, so clients can tell what was honored.
- **REST subscribe**: the spec text says `POST /tasks/{id}:subscribe` but the normative proto says `GET`. The server accepts both. The client defaults to POST (`RestSubscribeMethod` to change).
- **Stream closing**: a send stream ends on a terminal state or when the executor returns. `AUTH_REQUIRED` keeps the stream open while the executor waits for out-of-band credentials (§7.6.1). `SubscribeToTask` ends on a terminal state.
- **Push delivery**: events for one task are delivered in order, at least once, with exponential backoff (408/429/5xx retried). Each POST carries `X-A2A-Notification-Token` and `X-A2A-Notification-Sequence` (an SDK extension: a per-task counter for deduplication and ordering; the spec defines neither).
- **Auth errors**: the HTTP layer answers 401/403 with `WWW-Authenticate`. Over JSON-RPC the body uses code `-32603` with an `ErrorInfo` reason of `UNAUTHENTICATED` / `PERMISSION_DENIED`, since the spec assigns no JSON-RPC number.
- **Artifacts**: `append: true` concatenates parts onto the stored artifact; otherwise the artifact is replaced.

## Not included yet

- gRPC binding
- Durable stores (implement `TaskStore` / `PushConfigStore`; the in-memory ones are for dev and tests)
- A multi-instance event bus (`TaskEventBus` is process-local; subclass it over Redis or similar)
- A webhook-receiver helper for clients

## Build and test

```bash
dotnet build A2A.slnx
dotnet test tests/A2A.Tests
```

Run the sample:

```bash
dotnet run --project samples/EchoAgent
dotnet run --project samples/EchoClient
```

`docs/a2a.proto` is the normative proto this SDK was built against.
