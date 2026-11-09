# RomanAmbinder.A2A.TestKit

Test tools for A2A (Agent2Agent) agents. They work with any test framework: a failed check throws `A2AAssertionException`, and the package references no test framework.

- `A2ATestHost`: runs your agent in the test process, with no network port. Both JSON-RPC and HTTP+JSON work. Create a client with `host.CreateClient()`.
- `FakePeerAgent`: a scripted agent for testing an agent that calls other agents. Say what it answers, then read the calls it received.
- `A2AAssert`, `ExpectedEvent`, `TestEvents`: check task states, event order and A2A errors. Failure messages show the expected and the actual events.
- `TestClock`: a `TimeProvider` that moves only when you call `Advance`. Pass it as `A2ATestHostOptions.TimeProvider`.
- `TestPushReceiver`: a fake webhook. The host sends push notifications to it, and you wait for them without polling.

```csharp
await using var host = await A2ATestHost.StartAsync(async (context, updater, ct) =>
{
    await updater.StartWorkAsync(cancellationToken: ct);
    await updater.CompleteAsync("done", ct);
});
var client = host.CreateClient();
var events = await client.SendStreamingMessageAsync(new SendMessageRequest { Message = Message.FromUser("go") }).CollectAsync();
A2AAssert.StateOrder(events, TaskState.Submitted, TaskState.Working, TaskState.Completed);
```

The kit has no gRPC. It is meant for test projects only, and it does not claim AOT compatibility.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) and the [testing guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/testing.md) for full docs.
