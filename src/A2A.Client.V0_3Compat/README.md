# RomanAmbinder.A2A.Client.V0_3Compat

Talk to A2A protocol v0.3 agents with the A2A .NET client. You opt in by calling this factory instead of `A2AClientFactory`:

```csharp
var client = await V03CompatClientFactory.CreateAsync(agentUrl, httpClient);
```

It returns a normal `A2AClient`. Selection:

1. The card is fetched by `A2ACardResolver` (HTTPS rules, pinning and text limits apply). A card with a top-level
   `protocolVersion` of `0.3.x` and no `supportedInterfaces` is converted to the v1.0 model, with its interfaces marked `0.3`.
   Other `0.x` versions are refused.
2. If the card offers a v1.x interface the client uses the normal v1.0 path. A card that offers v1.0 is never downgraded.
3. Otherwise the first v0.3 `JSONRPC` or `HTTP+JSON` interface in card order is used.

The v0.3 transports share retries, headers, telemetry and SSE limits with the v1.0 transports. They send `A2A-Version: 0.3`
and `X-A2A-Extensions`. A stream stops on `final: true` or when the connection ends.

Not available on v0.3: `ListTasks` (throws `UnsupportedOperation`). Push config create, get, list and delete work, but
`PushNotificationReceiver` still reads v1.0 webhook bodies only. A v0.3-only card is read as unsigned, so a client that
requires card signatures refuses it. gRPC is not supported.

Fields v0.3 cannot carry are counted by the meter `A2A`, counter `a2a.compat.v0_3.dropped` (tag `field`).

The package uses internals of the core packages, so all `RomanAmbinder.A2A.*` packages must have the same version.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a). Targets .NET 10.
