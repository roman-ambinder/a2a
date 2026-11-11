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
dotnet test tests/A2A.Mcp.Tests
dotnet test tests/A2A.Stores.Tests
dotnet test tests/A2A.TestKit.Tests
dotnet test samples/TestingAgent
```

`tests/A2A.Stores.Tests` needs Docker. It starts Postgres, Redis, MinIO and Azurite with Testcontainers. Without Docker it skips, unless `A2A_REQUIRE_DOCKER=1` is set. Then a missing Docker fails the run. CI sets it.

See also [Build and test](README.md#build-and-test) in the README.

## Coverage

[coverage.yml](.github/workflows/coverage.yml) runs every suite above with line coverage after each merge to `main`, the
store tests included. It compares each package with [.github/coverage-baseline.json](.github/coverage-baseline.json) and
fails when a package is more than the file's `tolerance` (in percentage points) below its value, or has no coverage data.
The check is not part of `all-green` and does not block a release, but a red run on `main` needs a fix.

- Coverage went down on purpose (code removed, or moved to a package tested elsewhere)? Lower the value in the baseline
  file in the same pull request and say why.
- Coverage went up? The run's `coverage` artifact has `baseline-candidate.json`. Copy it over the baseline file to raise
  the floor. The run summary says which packages can be raised.
- A pull request that changes the baseline file, the workflow or `coverage_summary.py` runs the check itself. To check
  any other branch before merging, start the workflow by hand (Actions, Coverage, Run workflow, pick the branch). To
  see the numbers locally:

```bash
dotnet test --project tests/A2A.Tests/A2A.Tests.csproj -c Release -p:A2ACoverage=true --coverage --coverage-output-format cobertura --results-directory coverage
python3 .github/scripts/coverage_summary.py coverage --baseline .github/coverage-baseline.json
```

A local run of one suite reports less than CI, which merges all suites, so expect some packages to fall below the baseline.

## Mutation testing

[mutation.yml](.github/workflows/mutation.yml) runs [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/)
every week on the security-critical code. Each config in [tests/mutation](tests/mutation) names one target: the SSRF guard
(`security-http`), Agent Card signing, canonicalization and webhook signatures (`protocol-signing`), and AWS SigV4
signing (`identity-aws`). It reports only and fails nothing. The score and the surviving mutants are in the run summary
and the `mutation-<target>` artifact (open `mutation-report.html`). A surviving mutant is a change to the code that no
test noticed: add a test that fails on it, or, if the change cannot alter behaviour, leave it.

Each config runs only the test classes named in its `test-case-filter`, which keeps a run to minutes instead of hours.
Tests outside the filter do not count, so the score is a lower bound. When you add a test class for one of these
targets, add it to the filter too.

Do not trust the score of a file whose mutants change `static readonly` data. With per-test coverage off, Stryker runs
every mutant in one test process, so such a field is built once, with whichever mutant is active at that moment, and
keeps that value for the rest of the run. It goes wrong both ways:

- Mutants inside the field are reported as surviving even when a test catches them (`AgentCardCanonicalizer`'s schema,
  `JwsAlgorithms.Supported`).
- If the field is first built under a mutant that breaks it, every later mutant looks killed. `AwsSigV4RequestSigner`'s
  list of header names does this: a run reports 100% although a hand-made change can pass every test.

Check such a file by making the change by hand and running the tests.

To run one target locally (Stryker is in the tool manifest; run it from the test project's folder, not the root):

```bash
dotnet tool restore
cd tests/A2A.Tests && dotnet stryker --config-file ../mutation/security-http.json
```

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
