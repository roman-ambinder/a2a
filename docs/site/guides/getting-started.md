# Getting started

This guide shows how to host an agent and how to call one. It takes about five minutes. The SDK targets .NET 10. The protocol and client packages also target netstandard2.0.

## Install

```bash
dotnet add package RomanAmbinder.A2A.Server.AspNetCore   # host an agent
dotnet add package RomanAmbinder.A2A.Client              # call agents
```

The NuGet IDs have the `RomanAmbinder.` prefix. Assemblies and namespaces are `A2A.*`.

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
- `Streaming = true` in the card is needed for the streaming call in the client below. Without it, the server answers streaming calls with `UnsupportedOperation`.

Call <xref:A2A.Server.TaskUpdater.ReplyAsync*> instead for a stateless reply with no task.

## A client

[!code-csharp[](../../../samples/Snippets/GettingStarted.cs#Client)]

<xref:A2A.Client.A2AClientFactory.CreateAsync*> fetches the Agent Card and picks the first interface in the card's order that the client supports. `SendMessageAsync` returns a <xref:A2A.SendMessageResponse>: either a direct message or a task.

Every client method takes optional <xref:A2A.Client.A2ARequestOptions>: headers, timeout, idempotency key, extensions and `ConfigureRequest`. Two things happen without any setup:

- `SendMessage` and `SendStreamingMessage` always send an `Idempotency-Key`. The default is the message id.
- Unary calls are retried up to `MaxRetries` times. The default is 2, so 3 attempts. Set it on <xref:A2A.Client.A2AClientOptions> or per call. 0 turns retries off. A `SendMessage` is retried after a transport error only when the request never reached the server.

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

The Agent Card needs a name, a description and a version. If `SupportedInterfaces` is empty, the card is completed once, at first serve, from the bindings you mapped. Each URL is <xref:A2A.Server.AspNetCore.A2AHttpOptions.PublicBaseUrl> (or the first listen address) plus the mapped pattern. Set `PublicBaseUrl` behind a proxy or load balancer. Patterns with `{tenant}` are not listed.

## Run the samples

The repository has two samples. Start the agent. It needs an API key:

```bash
dotnet run --project samples/EchoAgent -- --EchoAgent:ApiKey=my-secret
```

It listens on `http://localhost:5100`. Change that with `--BaseUrl=...`. In another terminal, run the client with the agent URL, a text and the key:

```bash
dotnet run --project samples/EchoClient -- http://localhost:5100/ "hello from the SDK" my-secret
```

The client sends a `ping`, streams a reply, follows a task with `ObserveTaskAsync`, and lists its tasks. The sources are [EchoAgent](https://github.com/roman-ambinder/a2a/blob/main/samples/EchoAgent/Program.cs) and [EchoClient](https://github.com/roman-ambinder/a2a/blob/main/samples/EchoClient/Program.cs). The agent sample also shows API-key authentication and optional card signing.

## What can go wrong

- The app does not start and the message mentions `UseA2A()`: add `app.UseA2A()` after `UseAuthentication()` and before `UseAuthorization()`.
- The app does not start and the message mentions an authorization policy: call `RequireAuthorization()` on the result of `MapA2A`.
- The client throws `UnsupportedOperation` on a streaming call: the card does not declare `Streaming = true`.
- A curl call is rejected: add the `A2A-Version: 1.0` header.

## See also

- [Bindings](bindings.md): JSON-RPC, HTTP+JSON and gRPC.
- [Streaming and resume](streaming.md)
- [Authentication and ownership](auth-and-ownership.md)
- [Limits and rate limiting](limits-and-rate-limiting.md)
- [Durable stores](durable-stores.md)
