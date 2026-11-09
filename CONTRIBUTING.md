# Contributing

Thanks for helping. To report a vulnerability, see [SECURITY.md](SECURITY.md). Do not open a public issue for it.

## Prerequisites

- .NET SDK 10.0.100 or a later feature band ([global.json](global.json)).
- The .NET 8 runtime. The libraries target `net8.0` and `net10.0`.
- Docker, for the store tests.

## Build

```bash
dotnet build A2A.slnx -c Release
```

In `src/`, warnings are errors and XML docs are required for public API.

## Test

CI runs these suites ([ci.yml](.github/workflows/ci.yml)):

```bash
dotnet test tests/A2A.Tests
dotnet test tests/A2A.Grpc.Tests
dotnet test tests/A2A.V0_3Compat.Tests
dotnet test tests/A2A.Extensions.AI.Tests
dotnet test tests/A2A.Stores.Tests
dotnet test tests/A2A.TestKit.Tests
dotnet test samples/TestingAgent
```

`tests/A2A.Stores.Tests` needs Docker. It starts Postgres, Redis, MinIO and Azurite with Testcontainers. Without Docker it skips, unless `A2A_REQUIRE_DOCKER=1` is set. Then a missing Docker fails the run. CI sets it.

See also [Build and test](README.md#build-and-test) in the README.

## Public API

Every project in `src/` has `PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt`. They are checked by Microsoft.CodeAnalysis.PublicApiAnalyzers ([Directory.Build.props](Directory.Build.props)).

When you add or change a public member, the build fails with RS0016 (RS0017 for a removed entry), because warnings are errors. Add the new signature to `PublicAPI.Unshipped.txt` of that project. If the old signature is in `PublicAPI.Shipped.txt`, add it to `PublicAPI.Unshipped.txt` with the `*REMOVED*` prefix; see `src/A2A.Server/PublicAPI.Unshipped.txt` for examples. If it is only in `PublicAPI.Unshipped.txt`, edit that line.

Do not edit `PublicAPI.Shipped.txt` in a feature change. Entries move from Unshipped to Shipped in the release commit only.

Each file is one list for all target frameworks. There are no per-framework folders. Package validation (`EnablePackageValidation`) catches public API differences between target frameworks.

## Store contracts

Backends plug in through the abstract classes `TaskStore`, `PushOutbox`, `ExecutionCoordinator`, `EventStore`, `IdempotencyStore` and `ArtifactObjectStore`. The rules for changing them after the first release are in [ADR-001](docs/adr/0001-store-contract-evolution.md). Its status is still Proposed.

After the first release:

1. A new member is `virtual` with a real fallback, when that fallback is safe with several instances.
2. If no safe fallback exists, the member goes on an optional capability interface. A feature that needs a missing capability fails at startup with a clear error.

After the first release, do not add a new abstract member, and do not add a virtual member that throws `NotSupportedException`. Until then, the abstract surface can still change.

## Pull requests

- Target `main`.
- CI must pass.
- Add a [CHANGELOG.md](CHANGELOG.md) entry for user-visible changes.
