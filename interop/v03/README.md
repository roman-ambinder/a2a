# v0.3 compat interop

Checks the v0.3 compat layer against the official Python SDK `a2a-sdk` 0.3.26 (protocol v0.3), over JSON-RPC and HTTP+JSON (gRPC and ListTasks do not exist in the v0.3 layer).

| Client | Server |
|---|---|
| Python 0.3 (`py_client_v03.py`) | .NET with `EnableV03Compat` (`InteropV03Agent`) |
| .NET `V03CompatClientFactory` (`InteropV03Client`) | Python 0.3 (`py_server_v03.py`) |
| Python 1.x (`interop/python/py_client.py`) | the same .NET compat agent (blended card, v1.0 path) |

Same scenarios as [interop/](../README.md): `hello`, `task`, `input`, `slow`. Checks: direct reply, blocking and streaming send, multi-turn, get task with `historyLength`, unknown task, return-immediately + resubscribe + cancel, extended card (if offered). The .NET client also asserts that it spoke v0.3 on the wire.

Python 0.3 workarounds (in the scripts, with comments): the 0.3 server never sends the CANCELED event to `tasks/resubscribe` subscribers, and a resubscribe sends no task snapshot first.

## Run

Two venvs: `a2a-sdk` 0.3 and 1.x cannot share one.

```bash
python3 -m venv interop/v03/python/.venv
interop/v03/python/.venv/bin/pip install -r interop/v03/python/requirements.txt
python3 -m venv interop/python/.venv
interop/python/.venv/bin/pip install -r interop/python/requirements.txt
interop/v03/run.sh        # PYTHON=... PYTHON10=... to use other interpreters
```

Ports 5400 (Python) and 5500 (.NET); override with `PY_PORT` and `NET_PORT`. CI runs this in the `interop-v03` job.
