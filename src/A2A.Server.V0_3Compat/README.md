# RomanAmbinder.A2A.Server.V0_3Compat

Serve A2A protocol v0.3 clients from an A2A v1.0 agent. Opt in inside `MapA2A`:

```csharp
app.MapA2A("/", o => o.EnableV03Compat()).RequireAuthorization();
```

What it adds, all inside the group `MapA2A` returns (so authorization, rate limiting and CORS on that group cover it):

- v0.3 JSON-RPC methods on the same URL: `message/send`, `message/stream`, `tasks/get`, `tasks/cancel`,
  `tasks/resubscribe`, `agent/getAuthenticatedExtendedCard` and the four `tasks/pushNotificationConfig/*` methods.
  v1.0 methods are untouched.
- v0.3 HTTP+JSON routes under `{pattern}/v1/...` (and `{pattern}/{tenant}/v1/...`). A tenant named `v1` on a path-tenant
  host is shadowed by these routes.
- One blended Agent Card at `/.well-known/agent-card.json`, also served at `/.well-known/agent.json` (the v0.2.x path):
  the v1.0 card plus the v0.3 members. v1.0 clients and the card signature are not affected. The added v0.3 members are
  not covered by the signature.
- v0.3 push notifications. A config created over v0.3 gets the task as a v0.3 JSON-RPC `Task` on each webhook delivery,
  as the v0.3 Python SDK sends. v1.0 configs on the same task still get v1.0 events.

v0.2.x clients are served over JSON-RPC (v0.3 is a superset of their methods). `tasks/list` does not exist in v0.3. gRPC
is not served. A v0.3 call that sends an `A2A-Version` other than `0.3` or `0.3.x` is refused with `VersionNotSupported`;
a v1.0 method without the header is still refused as before. A JSON-RPC request without `"jsonrpc": "2.0"` is
`InvalidRequest`.

`capabilities.pushNotifications` is shared by both versions: the blended card and the extended v0.3 card keep the v1.0
value.

Fields v0.3 cannot carry are counted by the meter `A2A`, counter `a2a.compat.v0_3.dropped`. v0.3 traffic is counted by
`a2a.compat.v0_3.requests` (tags `binding`, `operation`).

The package uses internals of the core packages, so all `RomanAmbinder.A2A.*` packages must have the same version.
`EnableV03Compat` checks this and throws a clear error when they differ.

See the [v0.3 compatibility guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/v03-compatibility.md).
Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a). Targets .NET 10.
