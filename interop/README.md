# Interop tests

Checks that this SDK works with the official [Python A2A SDK](https://pypi.org/project/a2a-sdk/) (`a2a-sdk` 1.3.0, protocol 1.0), in both directions and over all three bindings:

| Client | Server | Bindings |
|---|---|---|
| .NET (`InteropClient`) | Python (`py_server.py`) | JSON-RPC, HTTP+JSON, gRPC |
| Python (`py_client.py`) | .NET (`InteropAgent`) | JSON-RPC, HTTP+JSON, gRPC |

Both agents implement the same scenarios, picked by the first word of the message:
`hello` (direct reply, no task), `task` (working → artifact → appended chunk → completed),
`input` (input-required, then completes on the next message), `slow` (runs until canceled).

Each agent serves HTTP on `<port>` and cleartext gRPC (HTTP/2) on `<port+1>`. `run.sh` starts the Python agent on port
5200 and the .NET agent on 5300 (override with `PY_PORT` and `NET_PORT`), all on `127.0.0.1`.

Each client checks: direct reply, blocking send, streaming send, streaming direct reply, multi-turn,
get task with `historyLength`, list tasks, unknown task → TaskNotFound, and return-immediately + subscribe + cancel.

## Run

```bash
python3 -m venv interop/python/.venv
interop/python/.venv/bin/pip install -r interop/python/requirements.txt
interop/run.sh            # PYTHON=... to use another interpreter
```

The exit code is the number of failed client runs. CI runs this on every pull request and every push to `main`
(`interop` job). For the v0.3 compat layer, see [v03/](v03/README.md). See also the
[interop and conformance guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/interop-and-conformance.md).
