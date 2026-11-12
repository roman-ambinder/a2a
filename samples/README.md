# Samples

Every usage example of the SDK lives in this folder. The runnable samples check their own result: each one ends with a
line that starts with `OK:` and exits 0, or prints `FAIL:` lines and exits non-zero. They listen on `127.0.0.1` only,
and every key, password and token in them is a demo value.

`Snippets/` is the exception: it only compiles. It holds the short pieces of code the
[guides](../docs/site/guides/overview.md) show, one file per guide. When a runnable sample already does what a guide
shows, the guide takes the code from that sample instead.

## Run them

```bash
samples/run-samples.sh                    # every sample that needs no Docker
samples/run-samples.sh grpc v03           # only the named ones
samples/run-samples.sh --with-docker      # also multi, storage and outbox
```

The script prints one line per sample: PASS, FAIL (with the log file), or SKIP. To run one sample by hand, see its
README, or `dotnet run --project samples/<Name>`.

## The samples

| Name in run-samples | Sample | Shows | Packages | Guide |
|---|---|---|---|---|
| `echo` | [EchoAgent](EchoAgent/Program.cs) + [EchoClient](EchoClient/Program.cs) | A small agent with API key auth, streaming, push and optional card signing; a DI client that calls it | `Server.AspNetCore`, `Client`, `Client.DependencyInjection` | [Getting started](../docs/site/guides/getting-started.md) |
| `chat` | [ChatAgent](ChatAgent/Program.cs) | An `IChatClient` hosted as an agent, and a remote agent called as an AI tool | `Extensions.AI` | [Extensions.AI](../docs/site/guides/extensions-ai.md) |
| `agentframework` | [AgentFrameworkAgent](AgentFrameworkAgent/Program.cs) | A Microsoft Agent Framework `AIAgent` hosted as an agent, then used as an `AIAgent` again: a session, streaming, a background response | `Extensions.AgentFramework` | [Agent Framework](../docs/site/guides/agent-framework.md) |
| `elicitation` | [ElicitationAgent](ElicitationAgent/Program.cs) | An agent that asks for a typed form; accept, decline, cancel and a plain-text answer | `Server.AspNetCore`, `Client` | [Task safety](../docs/site/guides/task-safety.md) |
| `typed` | [TypedSkillAgent](TypedSkillAgent/Program.cs) | A skill with .NET input and output types, schemas on the card, bad input refused | `Server.TypedSkills` | [Typed skills](../docs/site/guides/typed-skills.md) |
| `mcp` | [McpBridge](McpBridge/README.md) | An agent's skills served as MCP tools, and MCP tools run by an agent | `Mcp` | [MCP bridge](../docs/site/guides/mcp-bridge.md) |
| `testing` | [TestingAgent](TestingAgent/ResearchAgentTests.cs) | Tests of an agent with a fake peer, event order checks, a fake clock and push | `TestKit` | [Testing](../docs/site/guides/testing.md) |
| `grpc` | [Grpc](Grpc/README.md) | gRPC next to JSON-RPC, and how a client picks the binding | `Server.Grpc`, `Client.Grpc` | [Bindings](../docs/site/guides/bindings.md) |
| `v03` | [V03Compat](V03Compat/README.md) | A v1.0 agent that answers v0.3 clients; a client for v0.3 agents | `Server.V0_3Compat`, `Client.V0_3Compat` | [v0.3 compatibility](../docs/site/guides/v03-compatibility.md) |
| `cli` | [Cli](Cli/README.md) | The `a2a` command line tool against EchoAgent, with its exit codes | `Cli` | [Command line tool](../docs/site/guides/cli.md) |
| `tenant` | [TenantAgent](TenantAgent/README.md) | Two tenants, skill authorization and the audit trail | `Server.AspNetCore` | [Multi-tenancy](../docs/site/guides/multi-tenancy.md), [skill authorization](../docs/site/guides/skill-authorization.md), [audit](../docs/site/guides/audit.md) |
| `observed` | [ObservedAgent](ObservedAgent/README.md) | OpenTelemetry traces and metrics, and a task over its token budget | `Server.AspNetCore` | [Observability](../docs/site/guides/observability.md), [execution limits](../docs/site/guides/execution-limits.md) |
| `memory` | [MemoryAgent](MemoryAgent/README.md) | Conversation memory, and a task the host continues later | `Server.AspNetCore` | [Conversation memory](../docs/site/guides/conversation-memory.md), [proactive continuation](../docs/site/guides/proactive-continuation.md) |
| `client` | [ClientFeatures](ClientFeatures/README.md) | OAuth2 client credentials (secret and `private_key_jwt`), Azure credential, AWS SigV4, API key, retries, peer identity, custom operations | `Client.DependencyInjection`, `Client.Identity.Azure`, `Client.Identity.Aws` | [Client credentials](../docs/site/guides/client-credentials.md), [identity providers](../docs/site/guides/identity-providers.md), [resilience](../docs/site/guides/client-resilience.md), [peer identity](../docs/site/guides/peer-identity.md), [custom operations](../docs/site/guides/custom-operations.md) |
| `multi` (Docker) | [MultiInstance](MultiInstance/README.md) | One agent app started twice, sharing Postgres and Redis | `Server.Postgres`, `Server.Redis` | [Durable stores](../docs/site/guides/durable-stores.md) |
| `storage` (Docker) | [ArtifactStorage](ArtifactStorage/README.md) | Large artifacts in S3 (MinIO) or Azure Blob (Azurite), downloaded through a presigned URL | `Server.Storage.S3`, `Server.Storage.AzureBlob` | [Artifact storage](../docs/site/guides/artifact-storage.md) |
| `outbox` (Docker) | [ClientOutbox](ClientOutbox/README.md) | Outgoing messages written in your own Postgres transaction, then harvested | `Client.Postgres` | [Client outbox](../docs/site/guides/client-outbox.md) |

`Commons/` is not a sample: it is a small library with the public demo API key the MultiInstance sample uses (EchoAgent
and EchoClient reference it too).

Package names are short: add the `A2A.` prefix for the project, and `RomanAmbinder.A2A.` for the NuGet ID. Every
package under `src/` except the template pack (`A2A.Templates`, checked by `src/A2A.Templates/test-templates.sh`) is used
by at least one runnable sample; `tests/A2A.Tests/SamplesCoverageTests.cs` checks it.

## Adding a sample

- One folder with a project (or a script), a `README.md`, and a self-check that ends with `OK:`.
- Listen on `127.0.0.1` only. Mark every key and password as a demo value.
- Add it to `run-samples.sh`, to `A2A.slnx`, to the table above, and link it from its guide.
- A new package needs a sample, or an entry in `NotYetCovered` in `SamplesCoverageTests` that names the planned one.
- Guide code goes in a `#region` of the sample when one unbroken block there shows exactly what the guide shows.
  Otherwise it goes in `Snippets/`. See `docs/site/README.md`.
