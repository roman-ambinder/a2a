# Client resilience

The client retries a failed unary call (a call with one response, not a stream) on its own. This guide also covers falling back to another interface of the card, waiting for a task that is still running, and the circuit breaker, which stops calls to a peer that keeps failing.

## Retries

A unary call is retried up to `MaxRetries` times (default 2) on HTTP 503, 429, errors the peer marked retryable, and some transport errors. Streams are not retried, but `SendStreamingMessageAsync` and `ObserveTaskAsync` reconnect. See <xref:A2A.Client.A2AClientOptions.MaxRetries>.

A runnable version, where the agent answers 503 once and the client retries, is in [samples/ClientFeatures](https://github.com/roman-ambinder/a2a/blob/main/samples/ClientFeatures/README.md).

## Interface fallback

An Agent Card can list several interfaces, for example JSON-RPC and HTTP+JSON, or the same binding at two addresses. The client uses the first one it supports (spec §8.3.2). Set <xref:A2A.Client.A2AClientOptions.InterfaceFallback> to `true` and a client made by `A2AClientFactory` moves to the next interface when the one it uses cannot be reached (spec §5.2). It is off by default.

```csharp
var client = await A2AClientFactory.CreateAsync(agentUrl, http,
    new A2AClientOptions { InterfaceFallback = true });
```

- **What falls back.** Only a failure that proves no request reached the agent: the connection, the name lookup, the proxy tunnel or the TLS handshake failed (an `HttpRequestException` with `HttpRequestError` `ConnectionError`, `NameResolutionError`, `ProxyTunnelError` or `SecureConnectionError`, also behind a gRPC status). The transport's own retries (`MaxRetries`) run first.
- **What never falls back.** An HTTP status, an A2A error, a timeout, a cancellation, a failure during the request or the response, and an address the SSRF guard refused. The agent may have seen those requests: on HTTP+JSON a 404 is `TaskNotFound`, not a missing endpoint. A stream falls back only before its first event.
- **Which interfaces.** Every interface the client could select, in the card's order, with the same tenant as the selected one (a null and an empty tenant are the same). Each gets its own transport, created on first use, with its own protocol version. All of them pass the `EndpointPolicy` check when the client is built.
- **Which one is used.** A call that works keeps its interface for later calls. Each call tries every candidate at most once, starting at the current one and wrapping around to the first. When all fail, the error of the first one tried is thrown, as without fallback.
- **Credentials.** A client built with credential providers falls back only to interfaces on the card's origin that the credentials may be sent to (the same checks as for the selected interface: origin, HTTPS, no gRPC through the `HttpClient`). Interfaces on another origin are left out, so credentials never reach them. gRPC interfaces are left out too, since credentials cannot go on them through the `HttpClient`. A client you build with `A2AClientFactory.Create` over an `HttpClient` with an `A2ACredentialHandler` (as `AddA2AClient` does) skips an interface on another origin when it is tried: the handler refuses to send the request, so nothing leaves. There, a gRPC interface is still a candidate, and the gRPC transport, which does not use the `HttpClient`, sends no credentials. A client without credentials falls back to any candidate.
- **Metric.** The counter `a2a.client.interface.fallback` (meter `A2A`) counts every move, with the tags `from` and `to` (the two bindings). A span names the binding in use when its call started.
- **netstandard2.0** cannot tell that a request never left (there is no `HttpRequestError`), so it never falls back.

## Tasks that are still running

A blocking `SendMessageAsync` normally returns when the task is finished or needs input. This SDK's server waits that long. Some servers and proxies answer sooner, with a task that is still `SUBMITTED` or `WORKING`.

- Set <xref:A2A.Client.A2AClientOptions.WaitForTaskCompletion> to `true`. The client then polls the task once a second, as `WaitForTaskAsync` does, until it is terminal or interrupted. It returns that task with the history the request asked for.
- A request that sets `ReturnImmediately` is returned as it is.
- `AskAsync` always waits this way.

## Circuit breaker

Retries help with a short fault. When a peer is down for minutes, every call still waits and fails. A circuit breaker notices this and fails fast, so your threads and your users do not wait on a dead peer. It is off by default.

Turn it on with <xref:A2A.Client.A2AClientOptions.CircuitBreaker>:

[!code-csharp[](../../../samples/Snippets/ClientResilience.cs#TurnOn)]

### States

| State | What happens |
|---|---|
| Closed | Calls flow. The client counts failed calls in a row. A success resets the count. |
| Open | The count reached `FailureThreshold`. Calls throw <xref:A2A.Client.Resilience.CircuitOpenException> and no request is sent. |
| Half-open | `OpenDuration` has passed. One call (the probe) is sent. Other calls are refused while it runs. If the probe succeeds, the circuit closes. If it fails, the circuit opens again for a full `OpenDuration`. |

A probe that never ends is replaced after one more `OpenDuration`.

### What counts

| Result of a call | Effect |
|---|---|
| Connection failure, network I/O error, timeout | Counts as a failure |
| HTTP 503, or an error marked retryable | Counts as a failure |
| A reply from the peer: HTTP 4xx (401, 404 and so on), 429, a task-level error (such as `TaskNotFound`), a task that ended in `FAILED` | Not a failure. The peer is up, so the count is reset, and a probe that gets this reply closes the circuit. |
| A bare HTTP 500, or an invalid reply from the peer | Neither. The call is not counted, and a run of failures is not reset. A probe that gets this leaves the circuit half-open, and the next call is the new probe. |
| A caller's cancellation, a local validation error, an exception from `OnMessageReceived` | Neither |

Only the transport call is judged. Checks before it do not count and are never blocked by an open circuit.

### One call is one count

One call counts once, even when the client retried it. With `MaxRetries = 2` and `FailureThreshold = 5`, the circuit opens after 5 failed calls, which is at most 15 requests.

A stream is judged by how it starts. The first event, or a clean end, counts as success. An error before the first event is judged like any other error. Errors after the first event are not counted. `SendStreamingMessageAsync` and `ObserveTaskAsync` are one call each, reconnects included.

### The state belongs to one client

Each `A2AClient` has its own breaker, and a client talks to one peer. Share one client for each peer. If you create a new client for every call, the breaker never sees a second failure. `AddA2AClient` caches clients by name, so clients from `IA2AClientFactory` share their state.

The breaker works with every binding, gRPC included. A bare gRPC `UNAVAILABLE` counts as HTTP 503. Time comes from <xref:A2A.Client.A2AClientOptions.TimeProvider>.

### Handle the exception

[!code-csharp[](../../../samples/Snippets/ClientResilience.cs#HandleOpen)]

`CircuitOpenException` is an `A2AException` with code `Internal`. Its `RetryAfter` is the time until the next test call.

### Metric

The counter `a2a.client.circuit.state_changes` (meter `A2A`) counts every state change. Its tags are `gen_ai.agent.name` (when the client has a card) and `a2a.circuit.state`: `closed`, `open` or `half_open`. Add `AddMeter("A2A")` to see it. See [Observability](observability.md).
