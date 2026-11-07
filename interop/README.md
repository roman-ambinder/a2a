# Interop tests

Checks that this SDK works with the official [Python A2A SDK](https://pypi.org/project/a2a-sdk/) (`a2a-sdk` 1.3.0, protocol 1.0), in both directions and over both bindings:

| Client | Server | Bindings |
|---|---|---|
| .NET (`InteropClient`) | Python (`py_server.py`) | JSON-RPC, HTTP+JSON |
| Python (`py_client.py`) | .NET (`InteropAgent`) | JSON-RPC, HTTP+JSON |

Both agents implement the same scenarios, picked by the first word of the message:
`hello` (direct reply, no task), `task` (working → artifact → appended chunk → completed),
`input` (input-required, then completes on the next message), `slow` (runs until canceled).

Each client checks: direct reply, blocking send, streaming send, streaming direct reply, multi-turn,
get task with `historyLength`, list tasks, unknown task → TaskNotFound, and return-immediately + subscribe + cancel.

## Run

```bash
python3 -m venv interop/python/.venv
interop/python/.venv/bin/pip install -r interop/python/requirements.txt
interop/run.sh
```

CI runs this on every push and PR (`interop` job).
