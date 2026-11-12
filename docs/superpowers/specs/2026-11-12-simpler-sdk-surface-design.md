# Simpler SDK surface: one way to do each common thing

Date: 2026-11-12. Status: phases 1 and 2 done (see "Phase 2 as built"); phase 3 items 1-3 done (see "Phase 3 as built"), items 4-5 open.
Scope: the developer-facing API of `A2A.Client`, `A2A.Protocol` (response types), `A2A.Server`, `A2A.Server.AspNetCore` and the two AI adapters. It builds on `2026-11-07-sdk-design-principles-design.md`: safe and correct by default stays the floor, and "easy to use" is the principle this design serves.

Decisions from the user:

- Breaking changes are allowed. The packages have no users yet, so no deprecation path is needed.
- Order of work: fix the client's multi-turn path and the response types first, then the single server builder, then the surface size.

## 1. Problem

A review of the public API on 2026-11-12 found that the SDK is safe by default but hard to learn. There are several ways to do each common step, and some steps that look optional are required. Every finding below was checked against the code at commit `dd4a70b`.

### Client

- C1. **Multi-turn leaves the easy API.**
  - `AskAsync(this IA2AClient client, string text, string? contextId = null, ...)` has no task id, and neither does `StreamTextAsync`. When the agent needs input, both throw `TaskNotCompletedException`.
  - The documented recovery ("send the next message with the task's ContextId and Id") uses `A2AClient.SendMessageAsync(text, contextId, taskId)`, which is only on the concrete class and returns the raw union.
  - `samples/ClientFeatures/Program.cs` writes its own `AskAsync` as `(reply as MessageResponse)?.Message.GetText()`, which returns null for a task reply.
- C2. **The response unions are hard to read.**
  - `SendMessageResponse` exposes only `GetText()`: no state, task id or context id. Every caller has to pattern-match.
  - `StreamResponse` has `TaskId` and `State` but no `ContextId`, so `StreamTextAsync` casts `((StatusUpdateEvent)evt).StatusUpdate.ContextId`.
- C3. **There are at least nine ways to build a client**:
  - `A2AClientFactory.CreateAsync` (two overloads that disagree on `HttpClient` versus `HttpMessageHandler`)
  - `Create(card)`
  - `CreateForEndpoint`
  - `new A2AClient(transport)`
  - two gRPC factory methods
  - the DI factory
  - the v0.3 factory

  The README's first example passes a plain `HttpClient`, so it does not get the safe handler (no redirects, no cookies) that the credential overload and DI build.
- C4. **The conveniences are on the wrong type.**
  - The friendly overloads live on `sealed class A2AClient`, while transports also implement `IA2AClient`. So `AskAsync` over a raw transport compiles but skips the client's checks.
  - `AsAIFunction`, `AsChatClient` and `AsAIAgent` take the concrete `A2AClient`.
- C5. **Knobs are duplicated and credentials work three different ways.**
  - Extensions can be set in three places and `MaxRetries` in two.
  - `SelectSkill` mutates both the message and the options.
  - Credentials are attached as providers, as delegating handlers, or by hand-built gRPC channels.
- C6. **`TaskNotCompletedException` derives from `Exception`**, so it escapes `catch (A2AException)`.

### Server

- S1. **`UseA2A()` only calls `UseRateLimiter()`**, yet it is required. It must sit between `UseAuthentication()` and `UseAuthorization()`. Startup fails without it, and two analyzers (A2A0001, A2A0003) exist only to enforce its presence and position.
- S2. **The two registration forms have inverted shapes.**
  - `AddA2A(Action<A2AServerBuilder>)` returns `IServiceCollection`; `AddA2AServer(Action<A2AServerOptions>)` returns the builder.
  - The builder has no `Configure`, so settings go through `AddA2AServer(o => ...)`, `ConfigureA2AHttp`, `AddA2ARateLimiting` and `ConfigureA2A(IConfiguration)`.
  - Retention and push-secret protection are added on `IServiceCollection`, not on the builder.
- S3. **There are four ways to set who may call:**
  - `RequireAuthorization()`
  - `A2AEndpointOptions.AuthorizationPolicy`
  - `AddApiKeyAuthentication`
  - `AddBearerAuthentication`

  Anonymous access has three more forms of its own. `UseDevelopmentDefaults()` throws outside Development, so callers wrap it in their own environment check.
- S4. **`TaskUpdater` makes the simplest code noisy.**
  - `CompleteAsync(string? text = null, CancellationToken cancellationToken = default)` forces `cancellationToken: ct` named arguments (37 in the samples).
  - `AddArtifactAsync` has eight parameters, seven of them optional, and no text overload.
  - The executor delegate form `AddAgentExecutor(Func<...>)` exists but no sample shows it.
- S5. **Agent cards carry boilerplate.**
  - 23 sample files build the full `new AgentCard { ... }`; two use `AgentCard.Create`.
  - Samples set `Streaming = true`, which `AgentCardProvider` already defaults.
- S6. **The two AI adapters don't match.** `AddChatClientAgent(ChatClientAgentOptions)` and `AddAIAgent(AIAgentExecutorOptions)` duplicate eight properties, and both need the model registered in DI separately.
- S7. **The visible surface is far larger than what hosting needs.** `A2A.Server` declares 139 public types; hosting an agent needs about five.

## 2. Goals

- G1. Multi-turn on the client takes no ids handled by the caller.
- G2. Every send result answers "what state, which task, which context, what text" without a type switch.
- G3. One client entry point and one server entry point, each with one builder that owns all its settings.
- G4. No required middleware call whose only job is to be in the right place.
- G5. A server hello world without named arguments or card boilerplate.

Non-goals: wire changes, any weaker default (P0 floor), and new protocol features.

## 3. Design

### Phase 1: client multi-turn and readable responses (C1, C2)

**Response properties.** These are added to `A2A.Protocol` and marked `[JsonIgnore]`, like the existing `StreamResponse.State`.

| Type | New member | Value |
|---|---|---|
| `SendMessageResponse` | `TaskState? State` | the task's state; null for a direct message |
| `SendMessageResponse` | `string? TaskId` | the task's id, or the message's `TaskId` |
| `SendMessageResponse` | `string? ContextId` | the task's or the message's context id |
| `StreamResponse` | `string? ContextId` | the context id of the event; null for a `StreamErrorEvent` |

**`AgentReply`.** A sealed class in `A2A.Client`: the outcome of one turn, never thrown.

- `Text`: the direct message's text, or the text of the task's artifacts.
- `StatusText`: the text of the task's status message, for example the agent's question when input is required, or the failure reason. Null when there is none.
- `State` (null for a direct message), `TaskId`, `ContextId`, `Task`, `Message`.
- `IsCompleted`: a direct message, or state `Completed`.
- `NeedsInput` (`InputRequired`) and `NeedsAuth` (`AuthRequired`).
- `EnsureCompleted()`: returns the reply, or throws `TaskNotCompletedException`.

**`A2AConversation`.** A sealed class in `A2A.Client`, created with `client.StartConversation(contextId: null, options: null)`, an extension on `IA2AClient`.

- `ContextId`: null until the agent assigns one.
- `TaskId`: the task that waits for input or authorization, or null when the next message starts a new task.
- `LastReply`.
- `SendAsync(string text, CancellationToken)` and `SendAsync(Message message, CancellationToken)`. These fill in the message's context and task ids when they are not set, poll a running task until it is terminal or interrupted (as `AskAsync` does), update the ids, and return the `AgentReply`. They never throw for a task state.
- `StreamTextAsync(string text, CancellationToken)` yields text chunks the same way `A2AClientTextExtensions.StreamTextAsync` does. It then updates the ids and `LastReply` and does not throw for a task state. A stream that ends before the task reaches a terminal or interrupted state is followed by polling, so `LastReply` is always final.
- **Id rule:** after a reply, `ContextId` takes the reply's context id when it has one. `TaskId` keeps the task id only while the task is interrupted (`InputRequired` or `AuthRequired`). A terminal task cannot take more messages, so the next message starts a new task in the same context.
- **One turn at a time.** A conversation is not thread-safe, and a second call while one is running throws `InvalidOperationException`.

The target call site:

```csharp
var chat = client.StartConversation();
var reply = await chat.SendAsync("book a flight to Paris");
while (reply.NeedsInput)
{
    Console.WriteLine(reply.StatusText);          // the agent's question
    reply = await chat.SendAsync(Console.ReadLine()!);
}
Console.WriteLine(reply.EnsureCompleted().Text);
```

`AskAsync` and `StreamTextAsync` stay as one-shot shortcuts. `StreamTextAsync` uses the new `StreamResponse.ContextId` instead of a cast.

### Phase 2: one server builder (S1, S2, S3, S4, S5)

- `AddA2A` returns `A2AServerBuilder`. The builder gains `Configure(Action<A2AServerOptions>)`, `ConfigureHttp(Action<A2AHttpOptions>)` and `ConfigureRateLimiting(Action<A2ARateLimitOptions>)`. Retention, push-secret protection and health checks move onto the builder. `AddA2AServer` stays only for hosts without ASP.NET Core and says so in its summary.
- Rate limiting moves into the A2A endpoints as an endpoint filter that runs after authentication, so `UseA2A()` and analyzers A2A0001 and A2A0003 go away. The limiter keeps its partitioning by principal and its default-on behaviour.
- Caller policy has one place, on the builder:
  - `RequireApiKey(...)`
  - `RequireBearer(...)`
  - `RequirePolicy(name)`
  - `AllowAnonymousInDevelopment()`, which does nothing outside Development and logs that, instead of throwing.

  `MapA2A` reads the choice and refuses to start when none was made, which keeps the secure default. `RequireAuthorization()` on the endpoint group keeps working.
- `TaskUpdater`:
  - `CancellationToken`-only overloads of `StartWorkAsync`, `CompleteAsync`, `FailAsync` and `CancelAsync`.
  - `AddArtifactAsync(string text, string? name = null, CancellationToken)`.
  - The rare parameters (`append`, `lastChunk`, `metadata`, `description`) grouped into an `ArtifactOptions` record.
- An executor shortcut: `AddAgent(Func<AgentRequestContext, CancellationToken, ValueTask<string>>)`, which replies with the returned text. The README hello world uses it.
- `AddA2A(string name, string description, Action<A2AServerBuilder>)` builds the card. Default input and output modes become `text/plain` when empty. The samples move to `AgentCard.Create` and drop `Streaming = true`.

### Phase 2 as built

Phase 2 shipped with these differences from the plan above:

- Rate limiting (D2, settled). An endpoint filter alone would run after authorization, and requests with wrong
  credentials would no longer be counted. That would weaken a default, so the limit is taken in a decorator of
  `IAuthorizationMiddlewareResultHandler` for endpoints with a policy (after authentication, before the decision is
  acted on), and by the endpoint itself for anonymous endpoints. A host that replaces the result handler after `AddA2A`
  keeps the limit, but requests that fail authorization are then not counted. A custom policy is a keyed
  `IRateLimiterPolicy<string>`.
- Caller policy. `AddApiKeyAuthentication`, `AddBearerAuthentication` and `AddCardAuthentication` keep their names:
  they also declare the scheme on the card, which a `Require*` name would hide. `RequirePolicy(name)` was added next to
  them. Anonymous access is `Configure(o => o.AllowAnonymous = true)` or `ConfigureForDevelopment()`;
  no `AllowAnonymousInDevelopment()` was added, because `ConfigureForDevelopment()` already covers it.
- The reply shortcut is two overloads of `AddAgentExecutor` (a sync `Func<AgentRequestContext, string>` and an async
  `Func<AgentRequestContext, CancellationToken, Task<string>>`), not a new `AddAgent` name.
- `AddAgentCard(name, description)` on the builder instead of an `AddA2A(name, description, ...)` overload, so `AddA2A`
  keeps one shape.
- Default input and output modes were not changed. The server checks message parts against the card's input modes, and
  a card without modes accepts every media type. Defaulting to `text/plain` would make such agents refuse files and
  data, and tell clients they take only text.
- Not done: the `ArtifactOptions` record for the rare `AddArtifactAsync` parameters, `RequireInputAsync(form, text)`
  without the context, and moving health checks onto the builder (`AddA2AHealthChecks` stays on
  `IHealthChecksBuilder`, the ASP.NET Core convention).

### Phase 3: client entry point and surface size (C3, C4, C5, C6, S6, S7)

- **Connecting.**
  - `A2AClient.ConnectAsync(Uri, Action<A2AClientBuilder>?)` owns the safe handler and takes an optional `HttpClient`.
  - Credentials, card trust, binding preference and retries are set on that one builder, for HTTP and gRPC alike.
  - `CreateForEndpoint` and `Create(card)` stay as documented advanced paths.
  - The client keeps the `ResolvedAgentCard`, so verification and peer identity are not lost.
- **Types.**
  - Transports implement a separate `IA2ATransport`.
  - The string conveniences become extensions on `IA2AClient`.
  - The AI and MCP adapters accept `IA2AClient`.
- **Options.**
  - Extensions and retries each have one home.
  - `A2ARequestOptions.Skill` replaces `SelectSkill`.
  - `BearerTokenCredentialProvider` joins the core credential providers.
- **AI adapters.**
  - One shared options base for the two adapters.
  - Instance overloads: `AddChatClientAgent(chatClient, ...)` and `AddAIAgent(agent, ...)`.
- **Surface size.** Store, engine and coordination types move to `A2A.Server.Advanced` (namespace), so IntelliSense in `A2A.Server` shows the hosting basics.

### Phase 3 as built (items 1-3)

- `A2AClient.ConnectAsync(url, o => ...)` with `A2AConnectOptions` (`Credentials`, `Card`, `Client`, `Handler`).
  `A2AClientFactory.CreateAsync(url, httpClient)` stays public for an `HttpClient` the caller already has; the docs
  lead with `ConnectAsync`. `A2AClient.ResolvedCard` keeps the resolved card.
- The 11 operations moved to `IA2ATransport`; `IA2AClient : IA2ATransport` adds `AgentCard`. The interfaces are linked
  rather than duplicated, so an `A2AClient` can wrap another `IA2AClient`; that is harmless and keeps one definition of
  each operation. `ObserveTaskAsync` stays on `A2AClient` (it needs the resume internals); the Agent Framework adapter
  uses it when it has an `A2AClient` and subscribes once otherwise.
- `A2ARequestOptions.Skill` replaces `SelectSkill`; the client copies the options before adding the extension.
- `BearerTokenCredentialProvider` takes a token source only, keeping the earlier decision against a fixed-token
  provider.
- Not changed: `Extensions` and `MaxRetries` on both `A2AClientOptions` and `A2ARequestOptions`. They are a client
  default and a per-call override (the same layering as the tenant, D6 of the design principles), not two ways to do
  one thing. The per-transport credential models (AWS handler versus provider, gRPC channel setup) were not unified.

## 4. Open decisions

- D1 (C6). Settled: `TaskNotCompletedException` stays a plain `Exception`. Deriving from `A2AException` needs an
  `A2AErrorCode`, and an `A2AException` thrown in a server request path (an extension operation or filter that calls a
  peer agent) is passed to the caller as is, with the code's number as the JSON-RPC code. A code that must never reach
  the wire would need a guard on every binding's output (JSON-RPC, HTTP+JSON, gRPC, v0.3, stream errors); today the
  same exception becomes a generic Internal error. With `A2AConversation`, multi-turn callers no longer catch it, so the
  gain does not pay for the risk.
- D2 (phase 2). Settled: see "Phase 2 as built".

## 5. Testing

- Phase 1:
  - Unit tests for the new response properties, over every union case.
  - Integration tests on JSON-RPC and HTTP+JSON through `TestAgentHost`: a direct reply; a completed task; input-required, then an answer that completes the same task; an answer that asks again; a failed task returned, not thrown; a new task in the same context after completion; streaming that updates the ids; a concurrent call refused.
- Phase 2 and phase 3 keep the parity suite unchanged and move every sample to the new forms. The line-count gate on the hello-world snippets (design principles §8) is lowered to match.
