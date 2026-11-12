# v0.3 compat interop

Checks the v0.3 compat layer against the official Python SDK `a2a-sdk` 0.3.26 (protocol v0.3), over JSON-RPC, HTTP+JSON and gRPC (`a2a.v1.A2AService`, on the HTTP port + 1). ListTasks is not checked: it is not in v0.3 JSON-RPC or gRPC, and the 0.3 Python SDK does not implement the REST route.

| Client | Server |
|---|---|
| Python 0.3 (`py_client_v03.py`) | .NET with `EnableV03Compat` (`InteropV03Agent`) |
| .NET `V03CompatClientFactory` (`InteropV03Client`) | Python 0.3 (`py_server_v03.py`) |
| Python 1.x (`interop/python/py_client.py`) | the same .NET compat agent (blended card, v1.0 path) |

Same scenarios as [interop/](../README.md): `hello`, `task`, `input`, `slow`. Checks: direct reply, blocking and streaming send, multi-turn, get task with `historyLength`, unknown task, return-immediately + resubscribe + cancel, extended card (if offered), and push notifications: a config set through each binding, then the webhook body (a v0.3 `Task`, with the token header) received by a local receiver. The .NET client also asserts that it spoke v0.3 on the wire, gRPC calls included.

The 0.3 Python agent implements push config get on every binding, but list and delete only on JSON-RPC, so the .NET client checks those there only. Python logs "Some push notifications failed to send" after a push check: its sender keeps posting later events after the check has stopped its receiver.

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

Ports 5400 (Python) and 5500 (.NET), and 5401 and 5501 for gRPC; override with `PY_PORT` and `NET_PORT` (gRPC uses the next port). CI runs this in the `interop-v03` job.
See the [v0.3 compatibility guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/v03-compatibility.md).
