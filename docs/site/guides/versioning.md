# Versioning

The SDK follows [Semantic Versioning](https://semver.org/). Read this to know what can change between versions, and how to use the APIs that may change soon.

## The rules

- Before 1.0.0, a minor version may break the API. A patch version never does.
- From 1.0.0, a breaking change comes only in a major version.

## What is covered

- The public API listed in `PublicAPI.Shipped.txt` in each package. The build checks this with Microsoft.CodeAnalysis.PublicApiAnalyzers.
- The wire behavior of A2A v1.0.

Not covered: anything marked `[Experimental]`, and internal types.

## Deprecation

From 1.0.0, an API is marked `[Obsolete]` for at least one minor version before it is removed.

## Experimental APIs

Some APIs follow a contract that is not settled yet. They carry `[Experimental]` with a diagnostic ID. The compiler gives an error until you opt in. An experimental API can change or go away in any minor version.

| ID | Feature | Why it is unstable |
| --- | --- | --- |
| `A2AEXP001` | Skill selection, typed skills | Wire shape is part of an open epic: [A2A#1989](https://github.com/a2aproject/A2A/issues/1989) (and [A2A#2178](https://github.com/a2aproject/A2A/issues/2178) for structured arguments) |
| `A2AEXP002` | Actor chain | Wire shape is in an open issue: [A2A#2028](https://github.com/a2aproject/A2A/issues/2028) |
| `A2AEXP003` | Artifact integrity hash | Open issue: [A2A#1140](https://github.com/a2aproject/A2A/issues/1140) |
| `A2AEXP004` | gRPC extension service | The wire shape is not in the A2A spec |
| `A2AEXP005` | A2A 1.1 | The 1.1 spec is a release candidate (`v1.1.0-rc`) |
| `A2AEXP006` | Agent Framework adapter | Agent Framework hosting and sessions are preview upstream |

To opt in, add the ID to your project file:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);A2AEXP001</NoWarn>
</PropertyGroup>
```

Or silence one place with `#pragma warning disable A2AEXP001`.

### A2AEXP001

`SkillSelection`, `SkillSelectionMessageExtensions`, `A2AClient.SelectSkill` and `AgentRequestContext.RequestedSkillId`. See [Skill selection](skill-selection.md).

Typed skills use the same ID: `TypedSkillExtension`, `A2AClient.CallSkillAsync`, `TypedSkillCallException`, and the public types of `A2A.Server.TypedSkills`. See [Typed skills](typed-skills.md).

### A2AEXP002

`ActorChain`, `ActorChainEntry`, `ActorChainMessageExtensions` and `A2ALimits.MaxActorChainHops`. Token exchange (`ITokenExchanger`) is not experimental. See [Authentication and ownership](auth-and-ownership.md#delegation).

### A2AEXP003

`ArtifactHash`, `ArtifactVerifier`, `ArtifactVerification`, `ArtifactVerificationStatus` and `TaskUpdater.AddHashedArtifactAsync`. The artifact seal is not experimental. See [Streaming and resume](streaming.md#artifact-chunks).

### A2AEXP004

`A2AGrpcExtensionService` and `A2AGrpcExtensions.MapA2AGrpcExtensions`. See [Bindings](bindings.md#map-grpc).

### A2AEXP005

The types A2A 1.1 adds: `TimelineEntry`, `TaskMessageUpdateEvent`, `StreamError`, `StreamRequest`, `MessageUpdateEvent` and `StreamErrorEvent`, and `A2AServerOptions.EnableProtocolV11`.

The live exchange uses the same ID: `A2AClient.OpenLiveAsync`, `A2ALiveConnection`, `ILiveTransport`, `ILiveInput`, `LiveInputItem`, `MalformedStreamRequest`, `AgentRequestContext.LiveInput`, and `IA2ARequestHandler.SendLiveMessageAsync` / `SendLiveUplinkAsync`.

The 1.1 fields on existing types (for example `AgentTask.Generation`, `AgentTask.Timeline`, `SendMessageConfiguration.IfGenerationMatch`) are not marked, because the attribute cannot be put on properties for netstandard2.0, but they follow the same rule: they can change until 1.1 is final.

## Store contracts

Custom backends derive from the store base classes: `TaskStore`, `EventStore`, `PushConfigStore`, `PushOutbox`,
`IdempotencyStore`, `ExecutionCoordinator` and `ArtifactObjectStore`. Their abstract members are the ones that shipped
in 0.2.0, and that set does not grow. A store that builds against one version keeps building against the next.

A member added to one of these classes later comes in one of two forms:

- **A virtual member with a fallback.** The fallback must be safe when several instances share one backend. Override
  it when your backend can do better.
- **An optional capability interface,** when no safe fallback exists. Your store implements the interface to offer the
  capability. A feature that needs a capability your store lacks will fail at startup with an error that names it, not
  at first use.

Capability interfaces today: `IBatchTaskCommit`, `ITaskCommitLookup`, `IContextSummaryStore` and
`IPushOutboxStatistics`. Each one is optional, and a store without it keeps working.

One virtual member is older than this rule and its fallback is not safe with several instances:
`ExecutionCoordinator.TryRegisterExclusiveAsync` checks and then registers, which is not atomic. A coordinator that
several instances share must override it with an atomic check.

The reasoning is in
[ADR-001](https://github.com/roman-ambinder/a2a/blob/main/docs/adr/0001-store-contract-evolution.md).

### A2AEXP006

Every public type of `A2A.Extensions.AgentFramework`, starting with `A2AAgentKeys`. Agent Framework's session store
(`AgentSessionStore`, `AgentSessionStoreKey`) is marked evaluation-only upstream (`MAAI001`), and so are its continuation
tokens (`AgentRunOptions.ContinuationToken`, `AgentResponse.ContinuationToken`, `MEAI001`). Code that uses the
adapter's stored sessions or background responses opts in to those IDs too.

## How a release works

- Entries move from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`.
- `CHANGELOG.md` gets the new version.
