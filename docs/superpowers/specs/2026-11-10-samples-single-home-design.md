# samples/ is the single home of every usage example

Date: 2026-11-10. Status: approved for implementation.

## 1. Goal

Every C# usage example in the repository lives under `samples/`:

- The guide code that is now in `docs/site/snippets` moves into `samples/`.
- Every shipped package and every guide that has code gets at least one example under `samples/`.
- Where a feature can run on a laptop, the example is a runnable app that checks its own result.

## 2. Where things are today (checked 2026-11-10)

- `samples/` has 8 runnable apps: `EchoAgent`, `EchoClient`, `ChatAgent`, `ElicitationAgent`, `TypedSkillAgent`,
  `McpBridge`, `TestingAgent` (xunit tests), `MultiInstance` (Agent + Client, Docker). `samples/run-samples.sh` runs
  them by name (`chat, elicitation, typed, echo, mcp, testing, multi`); `multi` needs `--with-docker`.
- `docs/site/snippets` is one compile-only library, `A2A.DocSnippets.csproj` (warnings as errors), with 29 files and
  180 `#region` blocks. Guides include them with `[!code-csharp[](../snippets/File.cs#Region)]`.
- `docs/site/check-guides.py` refuses C# fences in guides, and checks that each include names a file and region that
  exist.
- CI builds the snippets (`ci.yml`, step "Build doc snippets"), runs `TestingAgent` tests, smoke-runs `EchoAgent`, and
  AOT-publishes `EchoAgent` and `EchoClient`. CI does not run `run-samples.sh`.
- `HelloWorldGateTests` holds `EchoAgent` to 5 and `EchoClient` to 4 lines that contain `A2A`.
- No sample project references these packages: `A2A.Cli`, `A2A.Grpc`, `A2A.Server.Grpc`, `A2A.Client.Grpc`,
  `A2A.V0_3Compat`, `A2A.Server.V0_3Compat`, `A2A.Client.V0_3Compat`, `A2A.Client.Postgres`,
  `A2A.Server.Storage.S3`, `A2A.Server.Storage.AzureBlob`.
- No sample uses: multi-tenancy, audit, OpenTelemetry, execution limits, skill authorization, conversation memory,
  proactive continuation, peer identity, OAuth2 client credentials, mTLS, client resilience, custom operations.

## 3. Decisions

**D1. Hybrid layout.** A runnable sample is the first home of a region. A guide region points into a runnable sample
when that sample already does exactly what the region shows. Everything else (short fragments such as a card with
`Streaming = true`) goes to one compile-only project, `samples/Snippets/Snippets.csproj`. A runnable sample is never bent out of
shape to host a region: if the code would only be there for the guide, it goes to `samples/Snippets`.

**D2. `samples/Snippets` replaces `docs/site/snippets`.** The project is renamed from `A2A.DocSnippets` to
`Snippets` (file `Snippets.csproj`, root namespace `Snippets`). Same rules as today: library, `net10.0`, not packable,
warnings as errors, in `A2A.slnx`. One file per guide stays the naming rule. `docs/site/snippets` is deleted; nothing
is left behind there.

**D3. Guides include from `samples/`.** The include path becomes relative to the guide, for example
`[!code-csharp[](../../../samples/Streaming/Program.cs#Send)]` or
`[!code-csharp[](../../../samples/Snippets/Streaming.cs#Capability)]`. `check-guides.py` resolves these paths and keeps
its file and region checks. Its message for a C# fence points to `samples/`.

**D4. Every runnable sample checks itself.** It listens on `127.0.0.1` only, needs no real secret, prints a last line
that starts with `OK:` and exits 0 on success (non-zero otherwise). Each one gets a name in `run-samples.sh`, a
`README.md` (what it shows, how to run it, expected output) and an entry in `A2A.slnx`. Docker samples run only with
`--with-docker`, like `multi`.

**D5. Coverage is checked, not promised.** A new test fails when a packable project under `src/` is not referenced by
any `samples/**/*.csproj`. A short allow-list holds packages that are reached only through another package and say
why (for example `A2A.Grpc`, `A2A.V0_3Compat` if they are only used through the server and client packages).

**D6. One index.** `samples/README.md` has a table: sample, what it shows, packages, guide. The repo `README.md` links
to it instead of listing samples one by one.

## 4. New runnable samples

Each is one app unless stated. "Self-check" is what it asserts before it prints `OK:`.

| Sample | Shows | Packages | Self-check | Docker |
|---|---|---|---|---|
| `Grpc` | `MapA2AGrpc()` host and a `GrpcTransport` client in one process | `Server.Grpc`, `Client.Grpc` | a message round-trips over gRPC | no |
| `V03Compat` | a v1.0 agent with `EnableV03Compat()`, called by `V03CompatClientFactory` | `Server.V0_3Compat`, `Client.V0_3Compat` | a v0.3 client gets a reply from the v1.0 agent | no |
| `Cli` | a script that runs the `a2a` tool against `EchoAgent`: show and verify the card, send, stream, get a task | `Cli` | each command exits 0, the send reply is checked | no |
| `ArtifactStorage` | large artifacts in S3 (MinIO) or Azure Blob (Azurite), picked by an argument | `Server.Storage.S3`, `Server.Storage.AzureBlob` | the client downloads the artifact and the bytes match | yes |
| `ClientOutbox` | a message written to the outbox in the app's own transaction, then delivered to an agent | `Client.Postgres` | the agent received the message once | yes |
| `TenantAgent` | multi-tenancy, skill authorization, audit trail | `Server.AspNetCore` | tenant A cannot see tenant B's task; an unauthorized skill is refused; the audit records both | no |
| `ObservedAgent` | OpenTelemetry tracing and metrics (console exporter, no collector), execution limits | `Server.AspNetCore` | a span for the call is recorded; a run over its limit ends as configured | no |
| `MemoryAgent` | conversation memory, proactive continuation | `Server.AspNetCore`, `Client` | a second turn sees the first; a continued task completes | no |
| `ClientFeatures` | OAuth2 client credentials (against a demo token endpoint hosted in the same process), API key, resilience, peer identity, custom operations | `Client`, `Client.DependencyInjection` | each call succeeds; a transient fault is retried | no |

The exact API names above come from `README.md` and the guide titles. Each one is checked against the source when its
sample is written.

## 5. Not in scope

- mTLS as a runnable sample: it needs certificates made at run time. It stays a region in `samples/Snippets`.
- Public API changes. If a sample cannot be written without one, that is a separate spec.
- AOT publish and line budgets for the new samples. The AOT gate and `HelloWorldGateTests` stay on `EchoAgent` and
  `EchoClient` only.

## 6. CI

- The "Build doc snippets" step builds `samples/Snippets/Snippets.csproj` instead.
- A new job runs `samples/run-samples.sh` (no Docker) on every push. Docker samples (`multi`, `ArtifactStorage`,
  `ClientOutbox`) run in a separate nightly job (`schedule`), not on every push.
- The coverage test from D5 runs with the other tests in `tests/A2A.Tests`.

## 7. Plan, in order

1. **Spike: DocFX includes from outside `docs/site`.** `docfx.json` today lists content under `docs/site` only. Check
   that `[!code-csharp[](../../../samples/...#Region)]` renders, with `--warningsAsErrors`. If it does not, the
   fallback is a build step that copies `samples/**/*.cs` into the DocFX tree before the build. Nothing else starts
   until this is settled.
   *Result (2026-11-10): works. A guide include of `../../../samples/...#Region` renders with DocFX 2.81.0 and
   `--warningsAsErrors`, the page is byte-identical to the old include, and DocFX reads the file under `samples/` (a
   changed line showed up on the page). `check-guides.py` already resolves such paths. No copy step is needed.*
2. Move `docs/site/snippets` to `samples/Snippets`, fix every include, update `check-guides.py`, `docs/site/README.md`
   and `ci.yml`. The site must build with no change in its output.
   *Done (2026-11-10). The old namespace `A2A.DocSnippets.*` made the `A2A` namespace visible; the new
   `Snippets.*` namespace does not, so `Snippets.csproj` has `<Using Include="A2A" />`. The rebuilt site matches the
   old one page for page.*
3. Repoint regions into existing runnable samples where D1 allows.
4. Add the coverage test (D5) with the allow-list set to today's gaps, so it passes. Each later step removes entries.
5. Non-Docker package samples: `Grpc`, `V03Compat`, `Cli`.
6. Feature samples: `TenantAgent`, `ObservedAgent`, `MemoryAgent`, `ClientFeatures`. Move their guide regions into
   them where D1 allows.
7. Docker samples: `ArtifactStorage`, `ClientOutbox`.
8. `samples/README.md` index, `README.md` links, CI job for `run-samples.sh`.

Each step is its own commit and leaves the build, tests and docs site green.

## 8. Risks

- **R1. DocFX paths** (step 1). Closed: see the step 1 result.
- **R2. Guide churn.** About 180 includes change paths. The include check in `check-guides.py` catches a broken one.
- **R3. CI time.** Running every sample adds build and start-up time. Measured once step 8 lands.
- **R4. Demo code copied into production.** The token endpoint in `ClientFeatures` and the fixed keys are demo values;
  each sample says so in its README and in a comment at the place they are set, as `MultiInstance` does today.

## 9. Resolved questions (2026-11-10)

- **Q1.** Server features: three apps, `TenantAgent`, `ObservedAgent`, `MemoryAgent`, as in section 4.
- **Q2.** mTLS: compile-only region in `samples/Snippets`.
- **Q3.** Docker samples in CI: nightly.
- **Q4.** The snippets project is renamed to `Snippets`.
