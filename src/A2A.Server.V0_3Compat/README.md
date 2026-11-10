# RomanAmbinder.A2A.Server.V0_3Compat

Serve A2A protocol v0.3 clients from an A2A v1.0 agent. Opt in inside `MapA2A`:

```csharp
app.MapA2A("/", o => o.EnableV03Compat()).RequireAuthorization();
```

What it adds, all inside the group `MapA2A` returns (so authorization, rate limiting and CORS on that group cover it):

- v0.3 JSON-RPC methods on the same URL: `message/send`, `message/stream`, `tasks/get`, `tasks/cancel`,
  `tasks/resubscribe`,
  `agent/getAuthenticatedExtendedCard`. v1.0 methods are untouched.
- v0.3 HTTP+JSON routes under `{pattern}/v1/...` (and `{pattern}/{tenant}/v1/...`). A tenant named `v1` on a path-tenant
  host is shadowed by these routes.
- One blended Agent Card at `/.well-known/agent-card.json`: the v1.0 card plus the v0.3 members. v1.0 clients and the
  card
  signature are not affected. The added v0.3 members are not covered by the signature.

Not supported (answered with `-32003`): v0.3 push notification configs, and a `pushNotificationConfig` in a send.
`tasks/list`
does not exist in v0.3. gRPC is not served. A v0.3 call that sends `A2A-Version` 1.0 or later is refused with
`VersionNotSupported`; a v1.0 method without the header is still refused as before.

`capabilities.pushNotifications` is shared by both versions. The blended card keeps the v1.0 value; the extended card
for v0.3
says `false`.

Fields v0.3 cannot carry are counted by the meter `A2A`, counter `a2a.compat.v0_3.dropped`. v0.3 traffic is counted by
`a2a.compat.v0_3.requests` (tags `binding`, `operation`).

The package uses internals of the core packages, so all `RomanAmbinder.A2A.*` packages must have the same version.
`EnableV03Compat` checks this and throws a clear error when they differ.

Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a). Targets .NET 10.
