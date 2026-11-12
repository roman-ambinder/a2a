# V03Compat sample

A v1.0 agent that also answers v0.3 clients (`RomanAmbinder.A2A.Server.V0_3Compat`, and over gRPC
`RomanAmbinder.A2A.Server.Grpc.V0_3Compat`), and the client factory for v0.3 agents (`RomanAmbinder.A2A.Client.V0_3Compat`,
and over gRPC `RomanAmbinder.A2A.Client.Grpc.V0_3Compat`). It listens on `127.0.0.1:5320` (HTTP/1.1) and
`127.0.0.1:5321` (HTTP/2, for gRPC) only and needs no keys.

The agent maps `MapA2A("/", o => o.EnableV03Compat())`. The sample reads the server's
`a2a.compat.v0_3.requests` counter (meter `A2A`) to check which protocol each call used:

| Call | Protocol | v0.3 requests |
|---|---|---|
| `A2AClientFactory` | v1.0 | 0 |
| A raw v0.3 JSON-RPC `message/send`, no `A2A-Version` header | v0.3 | 1 |
| `V03CompatClientFactory.Create` with a card whose only interface is marked `0.3` | v0.3 | 1 |
| `V03CompatClientFactory.CreateAsync` on this agent's card | v1.0: a card that offers v1.0 is never downgraded | 0 |
| `V03CompatClientFactory.Create` with a card whose only interface is v0.3 `GRPC`, and `UseGrpcV03()` | v0.3 over gRPC | 1 |

The third card stands in for the card of an agent that only speaks v0.3. It points at this agent's v0.3 surface.

```bash
dotnet run --project samples/V03Compat
```

Expected output ends with:

```text
v1.0 client: reply 'echo: hello', served on v0.3: 0
raw v0.3 request (result kind 'message'): reply 'echo: hello', served on v0.3: 1
V03CompatClientFactory, v0.3-only card: reply 'echo: hello', served on v0.3: 1
V03CompatClientFactory, blended card: reply 'echo: hello', served on v0.3: 0
V03CompatClientFactory, v0.3 gRPC card: reply 'echo: hello', served on v0.3: 1
OK: v1.0 and v0.3 clients got the same answer, and only the v0.3 calls used the v0.3 surface.
```

Anonymous access is on because the sample is loopback only. A real agent needs authentication; `RequireAuthorization()`
on `MapA2A` covers the v0.3 routes too. See the [v0.3 compatibility guide](../../docs/site/guides/v03-compatibility.md).
