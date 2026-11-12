# RomanAmbinder.A2A.Client.Grpc.V0_3Compat

Call A2A protocol v0.3 agents over gRPC (service `a2a.v1.A2AService`, the proto of tag v0.3.0) with the A2A .NET client.
Add the transport to the options of `V03CompatClientFactory`:

```csharp
var options = new A2AClientOptions().UseGrpcV03();
var client = await V03CompatClientFactory.CreateAsync(agentUrl, httpClient, options);
```

The factory then also selects the v0.3 `GRPC` interfaces of a card, in card order with the v0.3 JSON-RPC and HTTP+JSON
ones. A card that offers a v1.x interface still takes the v1.0 path (add `UseGrpc()` for v1.0 gRPC). The transport sends
`a2a-version: 0.3` and `x-a2a-extensions`, reads errors by their name prefix (`TaskNotFoundError: ...`, as the v0.3 Python
SDK sends them), and retries an idempotent call when the server is unavailable. `ListTasks` does not exist in the v0.3
proto and throws `UnsupportedOperation`. `GetExtendedAgentCardAsync` calls `GetAgentCard`.

The package uses internals of the core packages, so all `RomanAmbinder.A2A.*` packages must have the same version.

See the [v0.3 compatibility guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/v03-compatibility.md).
Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a). Targets .NET 10.
