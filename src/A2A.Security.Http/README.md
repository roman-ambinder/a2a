# RomanAmbinder.A2A.Security.Http

SSRF-guarded HTTP for the A2A (Agent2Agent) .NET SDK, shared by the client and the server.

- `PrivateAddressGuard.CreateHandler(...)`: a `SocketsHttpHandler` that refuses loopback, private, link-local (including cloud metadata), CGNAT and ULA addresses at connect time, against the resolved IP (blocks DNS rebinding), with redirects off.
- `GuardedJwksFetcher`: fetches Agent Card signing keys (`jku`) through that handler, with size and time limits.
- `SafeHttp`: guarded downloads with redirect refusal and a size cap.

You rarely reference this package directly: `RomanAmbinder.A2A.Client` and `RomanAmbinder.A2A.Server` use it for card verification and push webhooks.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a), implementing [A2A protocol v1.0](https://a2a-protocol.org/latest/specification/). Targets .NET 8 and .NET 10 only: the guard needs `SocketsHttpHandler.ConnectCallback`, so DNS rebinding cannot be blocked on netstandard2.0.
See the [repository README](https://github.com/roman-ambinder/a2a#readme) for full docs.
