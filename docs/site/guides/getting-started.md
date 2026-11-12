# Getting started

This guide shows how to host an agent and how to call one. Start here if you are new to the SDK; it takes about five minutes. The SDK targets .NET 10. The protocol and client packages also target netstandard2.0.

## Install

```bash
dotnet add package RomanAmbinder.A2A.Server.AspNetCore   # host an agent
dotnet add package RomanAmbinder.A2A.Client              # call agents
```

The NuGet IDs have the `RomanAmbinder.` prefix. Assemblies and namespaces are `A2A.*`.

### Or start from a template

```bash
dotnet new install RomanAmbinder.A2A.Templates
dotnet new a2a-agent -n MyAgent        # an agent; the card and limits are in appsettings.json
dotnet new a2a-agent -n MyAgent --auth apikey   # the same, with API keys (hashes in appsettings.json)
dotnet new a2a-client -n MyClient      # a console app that calls an agent
dotnet new a2a-chat-agent -n MyChat    # an agent that hosts an IChatClient
```

`dotnet run` in the agent folder starts it in Development on `http://localhost:5000`, with `UseDevelopmentDefaults()`, so `dotnet run` in the client folder can call it right away. Outside Development the agent asks for a bearer token (`AddBearerAuthentication()`) and refuses to start until you register the scheme, for example `AddJwtBearer()`; the comment in `Program.cs` shows how. With `--auth apikey` it takes API keys instead, in every environment.

## A server in about 20 lines

These are the using lines the code needs:

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Usings)]

The server:

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Server)]

The agent is a class that implements <xref:A2A.Server.IAgentExecutor>:

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Agent)]

What the code does:

- <xref:A2A.Server.AspNetCore.A2AServiceCollectionExtensions.AddA2A*> is `AddA2AServer()`, then `AddA2AServerAspNetCore()`, then your builder calls. Use the two-step form when you need <xref:A2A.Server.A2AServerOptions> at registration.
- `UseA2A()` adds the rate limiter middleware. The app refuses to start without it, unless you turn rate limiting off.
- `MapA2A` maps JSON-RPC at `POST /a2a` and the HTTP+JSON routes under `/a2a`. See [Bindings](bindings.md). It also maps the Agent Card at `GET /.well-known/agent-card.json`.
- The executor is resolved from a new DI scope for each execution, so scoped services are safe in it.
- Streaming is on unless the card says `Streaming = false`: the served card turns an unset flag into `true`, because every executor can stream. The client checks the flag before it sends a streaming call.

Call <xref:A2A.Server.TaskUpdater.ReplyAsync*> instead for a stateless reply with no task.

## A client

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Client)]

<xref:A2A.Client.A2AClientFactory.CreateAsync*> fetches the Agent Card and picks the first interface in the card's order that the client supports.

- <xref:A2A.Client.A2AClientTextExtensions.AskAsync*> returns the answer as text: the text of a direct reply, or of the task's artifacts once the task completed. A task that ends in any state other than completed (failed, rejected, canceled), or that needs input or authorization, throws <xref:A2A.Client.TaskNotCompletedException>, which holds the task. A task that is still running is polled every second until it settles.
- <xref:A2A.Client.A2AClientTextExtensions.StreamTextAsync*> yields the text as the agent streams it, with the same exception at the end.
- For more than one turn, start a conversation (next section).
- <xref:A2A.Client.A2AClientFactory.CreateForEndpoint*> makes a client for an endpoint you already know, such as an agent in the same process, without fetching the card. The client then checks no capabilities and verifies no card signature.
- For everything else use `SendMessageAsync`. It returns a <xref:A2A.SendMessageResponse>: either a direct message or a task. `State`, `TaskId` and `ContextId` on the response read the same values from both cases. `GetText()` on the response, on an `AgentTask` and on an `Artifact` reads their text parts.

Every client method takes optional <xref:A2A.Client.A2ARequestOptions>: headers, timeout, idempotency key, extensions and `ConfigureRequest`. Two things happen without any setup:

- `SendMessage` and `SendStreamingMessage` always send an `Idempotency-Key`. The default is the message id.
- Unary calls are retried up to `MaxRetries` times. The default is 2, so 3 attempts. Set it on <xref:A2A.Client.A2AClientOptions> or per call. 0 turns retries off. A `SendMessage` is retried after a transport error only when the request never reached the server.

### A conversation

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Conversation)]

<xref:A2A.Client.A2AConversationExtensions.StartConversation*> returns an <xref:A2A.Client.A2AConversation>, which keeps the ids between turns:

- `ContextId` takes the context id of each reply, so every turn continues the same context.
- `TaskId` is the task that waits for input or authorization. The next send answers it. Once a task has ended, the next send starts a new task in the same context.

`SendAsync` returns an <xref:A2A.Client.AgentReply> for every state and throws nothing for a task state. It has the `Text` (a direct reply's text, or the task's artifact text) and the `StatusText` (the agent's question, or why it failed). It also has the `State`, the ids, `IsCompleted`, `NeedsInput` and `NeedsAuth`. `EnsureCompleted()` throws <xref:A2A.Client.TaskNotCompletedException> when the turn did not complete. `StreamTextAsync` on the conversation yields text chunks and then sets `LastReply`. A conversation runs one turn at a time; a send while another is running throws `InvalidOperationException`.

`StartConversation(contextId)` continues a context that the agent issued earlier. The server refuses a context id that it did not issue.

To use dependency injection, add the `RomanAmbinder.A2A.Client.DependencyInjection` package. Register a named client with `services.AddA2AClient("weather", o => o.AgentUrl = ...)`. Then call <xref:A2A.Client.IA2AClientFactory.GetClientAsync*> with the name. Clients are cached per name. `AddA2AClient` returns an `IHttpClientBuilder`, so you can add handlers such as `AddBearerToken`.

## What the defaults give you

| Setting | Default |
|---|---|
| Anonymous callers | Refused. The app fails at startup unless every A2A endpoint has authorization, or <xref:A2A.Server.A2AServerOptions.AllowAnonymous> is true. |
| Request body | 10 MB (413 above that) |
| Parts per message | 100 |
| JSON depth | 32 |
| History returned, ListTasks page size | 100 each |
| Rate limit | Per tenant and caller. Anonymous callers count per remote IP. Burst 20, 60 per minute. 429 with `Retry-After` when exceeded. |
| Missing `A2A-Version` header | Rejected. Set `TreatMissingVersionAsCurrent` to accept curl calls. |
| Exception text in errors | Only when the host is in Development. |
| Stores | In memory. See [Durable stores](durable-stores.md). |

These are the values of <xref:A2A.Server.A2ALimits> and <xref:A2A.Server.AspNetCore.A2ARateLimitOptions>. Change them like this:

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Defaults)]

### Local development

<xref:A2A.Server.AspNetCore.A2ADevelopmentDefaultsExtensions.UseDevelopmentDefaults*> relaxes three defaults for local work: anonymous callers are let in, a request without the `A2A-Version` header (plain curl) is accepted, and errors carry exception text. Rate limiting and the limits stay on. The app refuses to start when the host environment is not Development, so wrap the call in `IsDevelopment()` when the same code runs elsewhere:

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Development)]

### From configuration

The same settings, and the card, can come from `appsettings.json` or any other configuration source:

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#FromConfiguration)]

```json
{
  "A2A": {
    "Card": {
      "Name": "My Agent",
      "Description": "Says hello.",
      "Version": "1.0.0",
      "Capabilities": { "Streaming": true },
      "Skills": [ { "Id": "hello", "Name": "Hello", "Description": "Says hello", "Tags": [ "greeting" ] } ]
    },
    "PublicBaseUrl": "https://agent.example.com",
    "Limits": { "MaxRequestBodyBytes": 1048576, "MaxStreamDuration": "00:10:00" },
    "RateLimit": { "TokenLimit": 10, "TokensPerPeriod": 30 }
  }
}
```

The keys are listed on <xref:A2A.Server.AspNetCore.A2AConfigurationExtensions>: the card, `PublicBaseUrl`, `AgentCardMaxAge`, every limit (`Limits`), the replay buffer (`Streaming`), the rate limit, API keys (`ApiKeys`) and a few task settings. Durations use the `TimeSpan` format. Two rules keep this safe:

- An unknown key fails the app at startup and names the key, so a typo does not go unnoticed.
- Settings that weaken a secure default (`AllowAnonymous`, `TreatMissingVersionAsCurrent`, `IncludeExceptionDetails`, `ClientContextIdPolicy`, `RateLimit:Enabled`) are refused there. Set them in code, where a reviewer sees them.

Configuration is applied first. `AddAgentCard` and `Configure` calls in code override it. For the two-step form, call `services.ConfigureA2A(section)` before `AddA2AServer`.

The Agent Card needs a name, a description and a version. `AgentCard.Create(name, description, version)` makes one (the version defaults to `1.0.0`), and `.AddSkill(id, name, description, tags, examples)` adds skills; an object initializer works too. If `SupportedInterfaces` is empty, the card is completed once, at first serve, from the bindings you mapped. Each URL is <xref:A2A.Server.AspNetCore.A2AHttpOptions.PublicBaseUrl> (or the first listen address) plus the mapped pattern. Set `PublicBaseUrl` behind a proxy or load balancer. Patterns with `{tenant}` are not listed.

## Run the samples

The repository has two samples. The agent needs an API key. Make one and its hash with the `a2a` tool, which works offline:

```bash
dotnet run --project src/A2A.Cli -- keys new
```

Start the agent with the hash. It stores only the hash:

```bash
dotnet run --project samples/EchoAgent -- --EchoAgent:ApiKeyHash=sha256:...
```

It listens on `http://localhost:5100`. Change that with `--BaseUrl=...`. In another terminal, run the client with the agent URL, a text and the key:

```bash
dotnet run --project samples/EchoClient -- http://localhost:5100/ "hello from the SDK" a2a_...
```

The client sends a `ping`, streams a reply, follows a task with `ObserveTaskAsync`, and lists its tasks. The sources are [EchoAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/EchoAgent/Program.cs) and [EchoClient](https://github.com/roman-ambinder/a2a/blob/main/samples/EchoClient/Program.cs). The agent sample also shows API-key authentication (`AddApiKeyAuthentication`, see [Authentication and ownership](auth-and-ownership.md#api-keys)) and optional card signing.

## What can go wrong

`RomanAmbinder.A2A.Server.AspNetCore` brings analyzers that report the first two mistakes below at build time, each with a code fix, and a third one: A2A0001 (endpoints mapped, `UseA2A()` never called), A2A0002 (endpoints mapped without authorization and no anonymous opt-in) and A2A0003 (`UseA2A()` before `UseAuthentication()` or after `UseAuthorization()`). They only report what they can see in your code; the startup checks stay the final word. When one method holds the whole setup, as a usual `Program.cs` does, A2A0001 and A2A0002 show in the editor as you type. When the setup is spread over several methods or files (the app passed to your own `Configure(app)`, options set in another class), they appear when you build.

- The app does not start and the message mentions `UseA2A()`: add `app.UseA2A()` after `UseAuthentication()` and before `UseAuthorization()`.
- The app does not start and the message mentions an authorization policy: call `RequireAuthorization()` on the result of `MapA2A`.
- The client throws `UnsupportedOperation` on a streaming call: the agent's card says `Streaming = false`, or another SDK's agent leaves it unset.
- A curl call is rejected: add the `A2A-Version: 1.0` header, or use `UseDevelopmentDefaults()` while you develop.

## See also

- [Bindings](bindings.md): JSON-RPC, HTTP+JSON and gRPC.
- [Streaming and resume](streaming.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Durable stores](durable-stores.md)
