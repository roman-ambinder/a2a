# RomanAmbinder.A2A.V0_3Compat

Shared code of the A2A protocol v0.3 compatibility packages: the two v0.3 wire codecs (JSON-RPC and HTTP+JSON) and the
Agent Card conversion in both directions. It has no public API. Use it through
[`RomanAmbinder.A2A.Server.V0_3Compat`](https://github.com/roman-ambinder/a2a/tree/main/src/A2A.Server.V0_3Compat) (serve v0.3 clients from a v1.0 agent) or
`RomanAmbinder.A2A.Client.V0_3Compat` (talk to v0.3-only agents).

Fields that v0.3 cannot carry are dropped and counted by the meter `A2A`, counter `a2a.compat.v0_3.dropped` (tag
`field`). Requests are counted by `a2a.compat.v0_3.requests` (tags `binding`, `operation`).

See the [v0.3 compatibility guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/v03-compatibility.md).
Part of the [A2A .NET SDK](https://github.com/roman-ambinder/a2a). Targets .NET 10.
