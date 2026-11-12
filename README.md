# A2A .NET SDK

A .NET SDK for the [Agent2Agent (A2A) protocol](https://a2a-protocol.org/latest/specification/) **v1.0**: host an agent that other agents can call, or call agents from your own code.

- **Server and client**, over three bindings: JSON-RPC, HTTP+JSON and gRPC.
- **Streaming** (SSE, with resume), **push notifications**, **Agent Card signing**.
- **Production parts**: durable Postgres stores, Redis for several instances, retention, artifact storage in S3 or Azure Blob, OpenTelemetry tracing and metrics.
- **Targets .NET 10.** `A2A.Protocol` and `A2A.Client` also target netstandard2.0 (.NET Framework 4.7.2+), with fewer protections. See [Product overview](docs/site/guides/overview.md).

📖 **Docs:** [roman-ambinder.github.io/a2a](https://roman-ambinder.github.io/a2a/). Start with [Getting started](docs/site/guides/getting-started.md).

## Contents

- [Install](#install)
- [Host an agent](#host-an-agent)
- [Call an agent](#call-an-agent)
- [Guides](#guides)
- [Samples](#samples)
- [Build and test](#build-and-test)
- [Contributing and security](#contributing-and-security)

## Install

```bash
dotnet add package RomanAmbinder.A2A.Server.AspNetCore   # host an agent
dotnet add package RomanAmbinder.A2A.Client              # call agents
dotnet tool install -g RomanAmbinder.A2A.Cli             # the `a2a` command line tool
```

Every NuGet ID is `RomanAmbinder.` + the package name, so it never clashes with the official `A2A` packages. Assemblies and namespaces are `A2A.*`. Use the same version for all the `RomanAmbinder.A2A.*` packages you install.

### Which packages do I need?

| I want to… | Install (`RomanAmbinder.` + name) |
|---|---|
| Host an agent in ASP.NET Core | `A2A.Server.AspNetCore` |
| Call an agent | `A2A.Client` (add `A2A.Client.DependencyInjection` for `AddA2AClient` and named clients) |
| Call an agent with Azure managed identity or AWS IAM | `A2A.Client.Identity.Azure` or `A2A.Client.Identity.Aws` |
| Use gRPC | `A2A.Server.Grpc` (host) or `A2A.Client.Grpc` (call) |
| Host an `IChatClient` as an agent, or use an agent as an AI tool | `A2A.Extensions.AI` |
| Host a Microsoft Agent Framework `AIAgent`, or use an agent as an `AIAgent` (experimental) | `A2A.Extensions.AgentFramework` |
| Bridge to MCP (Model Context Protocol) | `A2A.Mcp` |
| Give skills .NET input and output types (experimental) | `A2A.Server.TypedSkills` |
| Keep tasks across restarts | `A2A.Server.Postgres` |
| Run several server instances | `A2A.Server.Redis` (plus `A2A.Server.Postgres`) |
| Keep large artifacts in object storage | `A2A.Server.Storage.S3` or `A2A.Server.Storage.AzureBlob` |
| Keep a durable list of messages my agent sends to peers | `A2A.Client.Postgres` |
| Talk to v0.3 agents or clients | `A2A.Client.V0_3Compat` or `A2A.Server.V0_3Compat` (over gRPC too: `A2A.Client.Grpc.V0_3Compat`, `A2A.Server.Grpc.V0_3Compat`) |
| Test an agent | `A2A.TestKit` |
| Try an agent from the command line | `A2A.Cli` (`dotnet tool install -g`) |

The other packages (`A2A.Protocol`, `A2A.Security.Http`, `A2A.Grpc`, `A2A.V0_3Compat`, `A2A.Server`) come in as dependencies. What each package contains is in [Architecture](docs/site/guides/architecture.md).

You can also start from a project template: see [src/A2A.Templates](src/A2A.Templates/README.md).

## Host an agent

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAuthentication(/* your scheme */);
builder.Services.AddAuthorization();
builder.Services.AddA2A(a => a
    .AddAgentExecutor<MyAgent>()
    .AddAgentCard(AgentCard.Create("My Agent", "Says hello.")
        .AddSkill("hello", "Hello", "Says hello back", tags: ["greeting"])));

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapA2A("/a2a").RequireAuthorization();  // JSON-RPC and HTTP+JSON at /a2a, the Agent Card at /.well-known/agent-card.json

class MyAgent : IAgentExecutor
{
    public async Task ExecuteAsync(AgentRequestContext ctx, TaskUpdater updater, CancellationToken ct)
    {
        await updater.StartWorkAsync(ct);
        await updater.AddArtifactAsync($"You said: {ctx.UserText}", "answer", ct);
        await updater.CompleteAsync(ct);
    }
}
```

This code is the compiled snippet in [samples/Snippets/GettingStarted.cs](samples/Snippets/GettingStarted.cs). For an agent that only answers with text, a function is enough:

```csharp
builder.Services.AddA2A(a => a
    .AddAgentCard("My Agent", "Says hello.")
    .AddAgentExecutor(ctx => $"You said: {ctx.UserText}")
    .RequirePolicy("callers"));   // who may call, for every A2A endpoint; then app.MapA2A("/a2a") needs nothing more
```

What you get with no other configuration:

- **Secure by default.** Without a caller policy on the builder (`RequirePolicy`, `AddApiKeyAuthentication`, `AddBearerAuthentication`) or `.RequireAuthorization()`, the app refuses to start. For local experiments, `ConfigureForDevelopment()` allows anonymous callers in Development only. See [Getting started](docs/site/guides/getting-started.md) and [Authentication and ownership](docs/site/guides/auth-and-ownership.md).
- **Limits and rate limiting** are on, with defaults you can change. See [Limits and rate limiting](docs/site/guides/limits-and-rate-limiting.md).
- **In-memory stores.** Tasks are lost on restart. For production, see [Durable stores](docs/site/guides/durable-stores.md) and the [Production checklist](docs/site/guides/production-checklist.md).

## Call an agent

```csharp
var client = await A2AClient.ConnectAsync("https://agent.example.com/");
string answer = await client.AskAsync("hello"); // the reply's text; throws if the task does not complete

await foreach (var chunk in client.StreamTextAsync("stream this"))
    Console.Write(chunk);

var chat = client.StartConversation();               // multi-turn: the ids are kept for you
var reply = await chat.SendAsync("book a flight");
while (reply.NeedsInput) reply = await chat.SendAsync(Console.ReadLine() ?? "");
```

`ConnectAsync` reads the agent's card, picks a binding the card offers, and uses an HTTP handler that keeps no cookies and follows no redirects. It retries safe failures. Credentials, card trust and client options go in its options: `ConnectAsync(url, o => o.Credentials.Add(new ApiKeyCredentialProvider(key)))`. For credentials (OAuth2, API keys, mutual TLS, Azure, AWS), see [Client credentials](docs/site/guides/client-credentials.md). For DI and named clients, see [samples/EchoClient](samples/EchoClient/Program.cs).

## Guides

The guides are written to be read on the [docs site](https://roman-ambinder.github.io/a2a/), where their code samples are shown. On GitHub, a code sample appears as an include line that points to a file in [samples/](samples/README.md).

**Understand the SDK** (start here)

1. [Product overview](docs/site/guides/overview.md): what it is, who it is for, a map of the features
2. [Core concepts](docs/site/guides/concepts.md): agents, cards, messages, tasks, artifacts
3. [Architecture](docs/site/guides/architecture.md): packages, server parts, extension points
4. [How a request flows](docs/site/guides/request-lifecycle.md): one call, step by step
5. [Getting started](docs/site/guides/getting-started.md): host an agent and call it

**Build an agent or a client**

- Protocol: [Bindings](docs/site/guides/bindings.md) · [Streaming and resume](docs/site/guides/streaming.md) · [Push notifications](docs/site/guides/push-notifications.md) · [Proactive continuation](docs/site/guides/proactive-continuation.md) · [Client outbox](docs/site/guides/client-outbox.md) · [Custom operations](docs/site/guides/custom-operations.md) · [Metadata keys](docs/site/guides/metadata.md)
- Security: [Authentication and ownership](docs/site/guides/auth-and-ownership.md) · [Client credentials](docs/site/guides/client-credentials.md) · [Identity providers](docs/site/guides/identity-providers.md) · [Agent Card signing](docs/site/guides/agent-card-signing.md) · [Peer identity](docs/site/guides/peer-identity.md)
- Skills: [Skill selection](docs/site/guides/skill-selection.md) · [Typed skills](docs/site/guides/typed-skills.md) · [Skill authorization](docs/site/guides/skill-authorization.md)
- Integrations: [Microsoft.Extensions.AI](docs/site/guides/extensions-ai.md) · [Microsoft Agent Framework](docs/site/guides/agent-framework.md) · [Conversation memory](docs/site/guides/conversation-memory.md) · [MCP bridge](docs/site/guides/mcp-bridge.md) · [v0.3 compatibility](docs/site/guides/v03-compatibility.md)

**Run in production**

[Production checklist](docs/site/guides/production-checklist.md) · [Multi-tenancy](docs/site/guides/multi-tenancy.md) · [Durable stores](docs/site/guides/durable-stores.md) · [Retention](docs/site/guides/retention.md) · [Artifact storage](docs/site/guides/artifact-storage.md) · [Observability](docs/site/guides/observability.md) · [Audit trail](docs/site/guides/audit.md) · [Limits and rate limiting](docs/site/guides/limits-and-rate-limiting.md) · [Execution limits](docs/site/guides/execution-limits.md) · [Client resilience](docs/site/guides/client-resilience.md) · [Task safety](docs/site/guides/task-safety.md)

**Test and reference**

[Testing your agent](docs/site/guides/testing.md) · [Interop and conformance](docs/site/guides/interop-and-conformance.md) · [Command line tool](docs/site/guides/cli.md) · [Spec decisions](docs/site/guides/spec-decisions.md) · [Versioning](docs/site/guides/versioning.md) · [Changelog](CHANGELOG.md)

## Samples

Every sample is listed, with what it shows and its guide, in [samples/README.md](samples/README.md). Each one checks its own result. To run them all:

```bash
samples/run-samples.sh
```

`--with-docker` adds the samples that need Docker. Names such as `echo mcp` run only those samples.

## Build and test

```bash
dotnet build A2A.slnx
dotnet test --project tests/A2A.Tests
```

The store contract tests, coverage, mutation testing and the public API rules are in [CONTRIBUTING.md](CONTRIBUTING.md). `docs/a2a.proto` is the normative proto this SDK was built against. The interop tests against the official Python SDK are in [interop/](interop/README.md).

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) to build, test, release and send a pull request. To report a vulnerability, follow [SECURITY.md](SECURITY.md).
