# Interop and conformance

This SDK is tested against the official Python SDK, over all three bindings, in both directions. Other tests check that every method behaves the same on each binding, and that Agent Card signing matches the Python and JavaScript SDKs byte for byte. Read this to see what is covered and to run it yourself.

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

The `interop` job in the CI workflow installs Python 3.12 and the pinned requirements, then runs `interop/run.sh`. It runs on every push and pull request. The step is named "JSON-RPC + HTTP+JSON", but the script runs all three bindings.

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

## See also

- [Bindings](bindings.md)
- [Agent Card signing](agent-card-signing.md)
- [Spec decisions](spec-decisions.md)
