# A2A.Extensions.AgentFramework

Microsoft Agent Framework support for A2A. NuGet ID: `RomanAmbinder.A2A.Extensions.AgentFramework`.

**Status: in development, experimental (`A2AEXP006`).** This version has the package skeleton and `A2AAgentKeys` only.
The design is in [docs/superpowers/specs/2026-11-12-agent-framework-adapter-design.md](../../docs/superpowers/specs/2026-11-12-agent-framework-adapter-design.md).

When it is done it will:

- host any Agent Framework `AIAgent` as an A2A agent on this SDK's server;
- let you use a remote A2A agent as an `AIAgent`.

## Do not mix with Microsoft's A2A packages

This package builds on the A2A packages of this SDK (`RomanAmbinder.A2A.*`). Microsoft's `Microsoft.Agents.AI.A2A` and
`Microsoft.Agents.AI.Hosting.A2A` build on the NuGet package `A2A`, which defines `A2A.AgentCard`, `A2A.Message`,
`A2A.Part`, `A2A.Artifact` and `A2A.AgentTask`: the same full type names as `A2A.Protocol`. A project that references
both gets ambiguous types. Use one stack or the other in a project.

The package depends only on `Microsoft.Agents.AI.Abstractions` from Agent Framework (a stable release), not on
`Microsoft.Agents.AI` or the preview hosting packages.

## Native AOT

The package is AOT compatible. `Microsoft.Agents.AI.Abstractions` 1.28.0 declares itself trimmable and AOT compatible,
and a native AOT publish that calls the Agent Framework APIs this package uses (agent runs, sessions and their state bag,
the session store key, continuation tokens, tool approvals) gave no trim or AOT warnings.

## Experimental upstream APIs

Agent Framework marks some APIs this package will use as evaluation-only: the session store (`AgentSessionStore`,
`AgentSessionStoreKey`, `MAAI001`) and continuation tokens (`AgentRunOptions.ContinuationToken`,
`AgentResponse.ContinuationToken`, `MEAI001`). Code that uses stored sessions or background responses must opt in to
those IDs as well as `A2AEXP006`.
