# Testing your agent

`A2A.TestKit` is a package for tests. It runs your agent in the test process, gives you a fake peer agent, and checks task states and event order. It works with any test framework. It references none, and a failed check throws <xref:A2A.TestKit.A2AAssertionException>.

```shell
dotnet add package RomanAmbinder.A2A.TestKit
```

It is for test projects only. It does not claim AOT compatibility, and it has no gRPC: tests run over JSON-RPC and HTTP+JSON.

The repository has a runnable sample, [samples/TestingAgent](https://github.com/roman-ambinder/a2a/tree/main/samples/TestingAgent).

## Run your agent in the test process

<xref:A2A.TestKit.A2ATestHost> starts a real server with in-memory stores and no network port. Start it with your agent as a type, an instance, a delegate, or the full server builder (for `AddChatClientAgent` or a tenant resolver).

[!code-csharp[](../snippets/Testing.cs#StartHost)]

Then create a client and use the normal A2A API.

[!code-csharp[](../snippets/Testing.cs#Send)]

Dispose the host at the end of the test. The host serves its card at `A2ATestHost.DefaultBaseUrl` and lets you reach the request handler (`host.Handler`) and the services (`host.Services`).

### Options

[!code-csharp[](../snippets/Testing.cs#Options)]

<xref:A2A.TestKit.A2ATestHostOptions> lets you change the card, the server options, the HTTP options and the services. Your changes run after the defaults of the host: webhook URLs may be insecure and private, and push retries start after 10 ms. `ConfigureServices` runs after the SDK registrations, so it can replace a store or the push invoker. Set `TimeProvider` to use a [fake clock](#fake-clock).

Callers are anonymous unless they send the `X-Test-User` header. `CreateClient(user: "alice")` and `CreateHttpClient("alice")` set it. With `RequireAuthentication` the A2A routes refuse anonymous callers. The Agent Card stays public.

Your agent runs with the real <xref:A2A.Server.TaskUpdater>, so the events you see are the events a real client sees.

## Check states and events

Use <xref:A2A.TestKit.TestEvents.CollectAsync*> to read a stream to its end. It never hangs: after a timeout it throws and lists the events so far. Then check them with <xref:A2A.TestKit.A2AAssert>.

[!code-csharp[](../snippets/Testing.cs#Stream)]

| Method | Checks |
|---|---|
| `IsTask`, `IsMessage` | A `SendMessage` response is a task, or a message. Returns it. |
| `HasState` | A task (or a response with a task) is in a state. |
| `HasArtifact` | A task has an artifact with this name or id. |
| `StateOrder` | The states of the task and status events, in exact order, with no de-duplication. |
| `EventOrder` | The events are exactly the expected ones. |
| `ContainsEventOrder` | The expected events appear in this order, with other events allowed in between. |
| `ThrowsA2AAsync` | An action throws an `A2AException`, with a given error code. |
| `EventuallyHasStateAsync` | A task reaches a state. It calls GetTask every 10 ms. |

Every failure message shows what was expected and what happened, with one line for all events, for example `Task(SUBMITTED), Status(WORKING), Artifact(result), Status(COMPLETED)`. <xref:A2A.TestKit.ExpectedEvent> has `Task`, `Message`, `Status`, `Artifact` and `Matching` for your own checks.

## Fake peer agent

If your agent calls other agents, give it a <xref:A2A.TestKit.FakePeerAgent>. The peer runs in-process, answers as you script, and records every call.

[!code-csharp[](../snippets/Testing.cs#Peer)]

A call is answered by the first match in this order:

1. Queued answers (`Enqueue`), one use each, first in first out.
2. Rules (`WhenText`, `When`), in the order you added them.
3. `Otherwise`.

If nothing matches, the peer fails the task with a text that names what it received, and records an unmatched call. A test fails with a clear message instead of hanging. You can add rules after the peer started.

Each answer is a list of steps that run in order:

| Step | What the peer does |
|---|---|
| `Reply(text)` | Answers with a message and no task. |
| `Working(text?)` | Moves the task to `WORKING`. |
| `Artifact(text, name?)`, `Artifact(parts, name?)` | Adds an artifact. |
| `RequireInput(text)` | Moves the task to `INPUT_REQUIRED`. Send a follow-up to the same task to continue. |
| `RequireAuth(text)` | Moves the task to `AUTH_REQUIRED`. |
| `Complete(text?)` | Moves the task to `COMPLETED`. |
| `Fail(text?)` | Moves the task to `FAILED`. |
| `Reject(text?)` | Moves the task to `REJECTED`. |
| `Delay(span)` | Waits. Uses the host's `TimeProvider` when you set one. |
| `WaitFor(task)` | Waits until the task completes, or the call is canceled. |
| `Throw(exception)` | Throws from the agent. |
| `Run(step)` | Runs your own code with the context and the updater. |

Read what the peer saw with `peer.Calls`, `peer.UnmatchedCalls` and `WaitForCallAsync`. The peer has an Agent Card named "Fake Peer", so `A2AClientFactory.CreateAsync(peer.Url, peer.CreateHttpClient())` finds it.

[!code-csharp[](../snippets/Testing.cs#PeerCalls)]

## Fake clock

<xref:A2A.TestKit.TestClock> is a `TimeProvider` that moves only when you call `Advance` or `SetUtcNow`. Pass it as `A2ATestHostOptions.TimeProvider`. The server reads the `TimeProvider` from DI, so time limits such as `MaxTaskDuration`, execution leases, expiry, and the waits of push retries all follow it.

[!code-csharp[](../snippets/Testing.cs#Clock)]

`Advance` fires the timers that are due, in due order, on your thread. `Task.Delay(span, clock)` and `new CancellationTokenSource(span, clock)` work with it. The clock cannot go back.

What the clock drives, and what it does not:

- It drives the host and your agent, when your agent takes the time from `TimeProvider` (inject it, or read it from `host.Clock`).
- `WaitIdleAsync`, `EventuallyHasStateAsync`, the timeouts of `CollectAsync`, `WaitForAsync` and `WaitForCallAsync` use real time. They never wait on the fake clock.
- A push retry that waits on the fake clock does not happen until you advance it. `WaitIdleAsync` times out with a message that says so.
- A timer with a zero due time fires at once, on a thread pool thread, as it does with the real clock.

## Push notifications

The host sends webhooks to <xref:A2A.TestKit.TestPushReceiver> (`host.Push`), not to the network. Register `TestPushReceiver.DefaultUrl` as the webhook of a task, then wait for what you expect.

[!code-csharp[](../snippets/Testing.cs#Push)]

Each <xref:A2A.TestKit.ReceivedPush> has the URL, the method, the headers (names are case-insensitive), the body, the parsed `Event`, and `DeliveryId`, `Sequence` and `Token` read from the SDK headers. `WaitForAsync` and `WaitForCountAsync` do not poll. They look at the deliveries that came already and at later ones, and they throw with a list of what did arrive when time runs out. `Clear` forgets the past deliveries.

`Respond` sets the status code of the answer. Answer with a 5xx to see the host retry.

## Wait until the host is quiet

[!code-csharp[](../snippets/Testing.cs#Idle)]

`WaitIdleAsync` waits until no agent runs. With the default in-memory push outbox, it also waits until every push delivery was sent or dropped. It checks every 5 ms in real time and throws after 30 seconds by default.
