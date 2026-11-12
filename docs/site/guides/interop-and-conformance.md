# Interop and conformance

This SDK is tested against the official Python SDK, over all three bindings, in both directions. It also runs two official A2A conformance suites on all three bindings: the TCK (from `a2a-tck`) and ACTS (from `a2a-itk`). Other tests check that every method behaves the same on each binding, and that Agent Card signing matches the Python and JavaScript SDKs byte for byte. Read this to see what is covered and to run it yourself.

## Interop with the official Python SDK

The tests use `a2a-sdk` 1.3.0 (protocol 1.0). The versions are pinned in `interop/python/requirements.txt`.

| Client | Server | Bindings |
|---|---|---|
| .NET (`interop/dotnet/InteropClient`) | Python (`interop/python/py_server.py`) | JSON-RPC, HTTP+JSON, gRPC |
| Python (`interop/python/py_client.py`) | .NET (`interop/dotnet/InteropAgent`) | JSON-RPC, HTTP+JSON, gRPC |

Both agents implement the same four scenarios. The first word of the message picks one.

| Word | Behavior |
|---|---|
| `hello` | direct reply, no task |
| `task` | working, then an artifact, an appended chunk, then completed |
| `input` | input-required, then completes on the next message |
| `slow` | runs until canceled |

Each agent serves HTTP on `<port>` and cleartext gRPC (HTTP/2) on `<port+1>`.

Each client runs the same checks:

- direct reply
- blocking send
- streaming send
- streaming direct reply
- multi-turn
- get task with `historyLength`
- list tasks
- unknown task returns `TaskNotFound`
- return immediately, then subscribe, then cancel

## Run it locally

You need the .NET 10 SDK and Python 3.

```sh
python3 -m venv interop/python/.venv
interop/python/.venv/bin/pip install -r interop/python/requirements.txt
interop/run.sh
```

The script builds both .NET projects, starts the Python agent (port 5200) and the .NET agent (port 5300), then runs the .NET client and the Python client once per binding. It prints `INTEROP: all runs passed` or the number of failed runs. The exit code is the number of failed client runs.

| Variable | Default | Meaning |
|---|---|---|
| `PYTHON` | `interop/python/.venv/bin/python` | the Python that has the requirements installed |
| `PY_PORT` | `5200` | port of the Python agent |
| `NET_PORT` | `5300` | port of the .NET agent |

To drive one agent by hand, run the .NET client against any agent that implements the scenarios:

```sh
dotnet run --project interop/dotnet/InteropClient -- http://127.0.0.1:5200/ GRPC
```

The second argument is `JSONRPC`, `HTTP+JSON` or `GRPC`.

## In CI

The `interop` job in the CI workflow installs Python 3.12 and the pinned requirements, then runs `interop/run.sh`. It runs on every push to `main` and on every pull request. The step is named "JSON-RPC + HTTP+JSON", but the script runs all three bindings.

## Model and proto

The JSON model in `A2A.Protocol` is written by hand; the gRPC types are generated from `docs/a2a.proto`. `tests/A2A.Grpc.Tests/ProtoModelDriftTests.cs` checks that they agree: every proto message has a model type with exactly its JSON fields, every case of the union messages (`Part`, `SecurityScheme`, `SendMessageResponse`, `StreamResponse`) reads into the model and writes back to an equal proto message (the protobuf parser judges), and enum value names match. A message, field, case or value added to the proto fails the test until the model has it.

## Binding parity

The parity suite is `tests/A2A.Tests/Parity/ParityTests.cs`. It starts the SDK's own server and calls each of the 11 operations through the client over JSON-RPC, HTTP+JSON and gRPC. The same case must give the same result on all three. Test names are `{Operation}_{case}`.

| Case | What it checks |
|---|---|
| `happy_path` | every operation works |
| `metadata` | request metadata reaches the agent (operations whose request has metadata) |
| `historyLength` | the limit applies (operations that return a task with history) |
| `foreign_owner` | another caller gets `TaskNotFound`, the same answer as for a missing task |
| `handler_denial` | an authorization handler denial gives the same error |
| `tenant_match`, `tenant_mismatch` | a route tenant and a body tenant must agree (HTTP bindings and JSON-RPC; gRPC has no route tenant) |

`tests/A2A.Tests/Parity/RegressionTests.cs` holds regressions for bugs reported against other A2A SDKs. `tests/A2A.Tests/InteropHardeningTests.cs` does the same for wire-format issues.

Run only the parity tests:

```sh
dotnet test tests/A2A.Tests/A2A.Tests.csproj --filter "FullyQualifiedName~Parity"
```

gRPC-specific behavior, such as filters, security and telemetry, has its own project: `tests/A2A.Grpc.Tests`.

## Conformance suites

Two official suites run against `conformance/dotnet/ConformanceAgent`, on JSON-RPC, HTTP+JSON and gRPC. They are cloned at pinned commits.

| Suite | Source | Pinned commit |
|---|---|---|
| TCK | [a2aproject/a2a-tck](https://github.com/a2aproject/a2a-tck) | `70f6f73` (after tag `1.0.0.alpha3`) |
| ACTS | [a2aproject/a2a-itk](https://github.com/a2aproject/a2a-itk) (`run_acts.py`) | `d53108a` |

The TCK pin is one commit behind its `main`. That commit only moves the TCK to Python 3.15, where `grpcio` has no prebuilt wheels and takes over 30 minutes to compile. The test code is the same. The ITK cross-SDK traversal suite in `a2a-itk` is not run. The interop tests above cover two-way interop with the official Python SDK.

### Results

Counts come from `conformance/check.py`: one per requirement (TCK) or test (ACTS), per binding.

| Suite | Binding | Pass | Fail | Skip | MUST failures |
|---|---|---|---|---|---|
| ACTS | JSON-RPC | 101 | 0 | 0 | 0 |
| ACTS | HTTP+JSON | 94 | 0 | 0 | 0 |
| ACTS | gRPC | 88 | 0 | 0 | 0 |
| TCK | JSON-RPC | 72 | 3 | 6 | 2 |
| TCK | HTTP+JSON | 70 | 4 | 4 | 3 |
| TCK | gRPC | 55 | 3 | 2 | 2 |
| TCK | Agent card | 8 | 0 | 1 | 0 |

Every TCK failure is explained below. None is an open SDK bug. Skips are capability tests that need an agent without the capability, or optional features. The TCK also lists some requirements as not tested or manual (for example in-task auth and client behavior). Those have no automated check.

### Expected failures

| TCK requirement | Bindings | Why |
|---|---|---|
| `CORE-SEND-003` | all | TCK bug ([#202](https://github.com/a2aproject/a2a-tck/issues/202), fix in [PR #203](https://github.com/a2aproject/a2a-tck/pull/203)). The requirement says the call must fail, but the entry has no `expected_error`, so the runner demands success. The SDK returns `ContentTypeNotSupported`. `HTTP_JSON-STATUS-001` and ACTS `CORE-SEND-004` check this and pass. |
| `CORE-MULTI-002a` | all | TCK bug, same issue and fix. The requirement says to reject a `contextId` the agent cannot accept, and the SDK does. |
| `CORE-MULTI-002` | all | SDK choice, `MAY` in spec 3.4.1. Unknown client `contextId`s are rejected by default. `ClientContextIdPolicy.AdoptIfUnknown` accepts them. |
| `HTTP_JSON-ERR-001` | HTTP+JSON | TCK bug (fix in [PR #238](https://github.com/a2aproject/a2a-tck/pull/238)). One test wants `application/json` on errors. Spec 11.6 shows `application/a2a+json`, and another TCK check expects that too. |

The list is `conformance/expected-failures.txt`. When the upstream fixes land and the pin moves, `check.py` reports which entries to remove.

### How the agent differs from a default server

Some settings of the system under test change the results:

- **TCK pass: message-ID dedupe is off** (`SUT_DISABLE_MESSAGE_DEDUPE=1`). The TCK helpers reuse one `messageId` for every task they create ([a2a-tck #267](https://github.com/a2aproject/a2a-tck/issues/267)). The SDK treats `messageId` as an idempotency key (spec 3.3.1 allows this). With dedupe on, the TCK gets replays of old tasks or `IdempotencyKeyMismatch`, and about 30 requirements fail or go untested. A default server keeps dedupe on, and the ACTS pass runs with SDK defaults.
- **Default pass: only the extended card reads the bearer token.** ACTS starts some tasks with a token and reads them without one, and the SDK scopes tasks to their owner. ACTS also runs two more agent instances: one where every operation needs a token, and one with reduced capabilities.
- The gRPC interface URL is `host:port` with no scheme, because both suites pass it straight to a gRPC channel.
- Webhooks to private addresses are allowed, because the suites' receivers run on `127.0.0.1`.

### Run the suites

You need the .NET 10 SDK, git, [uv](https://docs.astral.sh/uv/) and Python 3.

```sh
conformance/run.sh
```

To run only ACTS:

```sh
SUITES=acts conformance/run.sh
```

The script clones the suites into a cache folder outside the repo and runs them from there. It runs downloaded code, so treat it that way. Reports go to `conformance/results/`.

### In CI

The `conformance` job runs `conformance/run.sh` and uploads the reports as `conformance-results`. It fails on any failure that is not in `expected-failures.txt`. `check.py` also lists entries that now pass, so you can remove them.

More detail is in the [conformance README](https://github.com/roman-ambinder/a2a/blob/main/conformance/README.md).

## Agent Card signing vectors

`tests/A2A.Tests/Vectors/card-signing/` holds signed cards produced by a2a-python 1.2.1. For seven cards (`minimal`, `full`, `empty-optionals`, `false-capabilities`, `unicode`, `extension-params`, `mixed-version`) it has the raw input, the exact bytes a2a-python signs, the card as a2a-python serves it, and the public keys.

`CanonicalizationVectorTests` checks that:

- <xref:A2A.Signing.AgentCardCanonicalizer> produces the same bytes as a2a-python for every input.
- every Python signature, ES256 and RS256, verifies in this SDK.
- the model round trip keeps the canonical form.
- members outside the v1.0 schema never reach a verified card.

Python is not needed to run these tests. The a2a-js rule was compared by reading its source. No JavaScript vectors were generated.

The [vectors README](https://github.com/roman-ambinder/a2a/blob/main/tests/A2A.Tests/Vectors/README.md) explains the rule, lists the known differences from a2a-python, and has the script to regenerate the files. See [Agent Card signing](agent-card-signing.md) for what the signature covers.

## What can go wrong

- `interop/run.sh` times out waiting for an agent. It waits 60 seconds for each card at `/.well-known/agent-card.json`. Check that the ports are free.
- `interop/run.sh` fails with exit code 100 or 101. 100 is a failed .NET build. 101 is an agent that did not start.
- Python imports fail. Run the script with the venv's Python, or set `PYTHON`.
- The conformance job fails on a new failure. Fix the SDK, or, for a suite bug, add a line with an issue link to `conformance/expected-failures.txt`.
- TCK requirements fail or go untested. Message-ID dedupe is on. The TCK needs it off; `conformance/run.sh` does that for its agent.

## See also

- [Bindings](bindings.md)
- [Agent Card signing](agent-card-signing.md)
- [Spec decisions](spec-decisions.md)
