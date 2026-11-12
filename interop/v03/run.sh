#!/usr/bin/env bash
# v0.3 compat interop, over JSON-RPC, HTTP+JSON and gRPC, with push notifications. Exit code = number of failed client runs.
#   1. Python a2a-sdk 0.3 client -> .NET agent with EnableV03Compat
#   2. .NET compat client (V03CompatClientFactory) -> Python a2a-sdk 0.3 agent
#   3. Python a2a-sdk 1.x client (interop/python/py_client.py) -> the same .NET compat agent
#
# Requires: .NET 10 SDK, a Python 3.10+ with interop/v03/python/requirements.txt installed (a2a-sdk 0.3.26),
# and for direction 3 a separate Python with interop/python/requirements.txt (a2a-sdk 1.x).
#   PYTHON=/path/to/python03 PYTHON10=/path/to/python1x interop/v03/run.sh
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
PY="${PYTHON:-$ROOT/interop/v03/python/.venv/bin/python}"
PY10="${PYTHON10:-$ROOT/interop/python/.venv/bin/python}"
PY_PORT="${PY_PORT:-5400}"
NET_PORT="${NET_PORT:-5500}"
pids=()

cleanup() { for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done; }
trap cleanup EXIT

wait_for() {
  for _ in $(seq 1 120); do
    curl -sf -o /dev/null "$1/.well-known/agent-card.json" && return 0
    sleep 0.5
  done
  echo "Timed out waiting for $1" >&2
  return 1
}

dotnet build "$ROOT/interop/v03/dotnet/InteropV03Agent" -c Release -v quiet --nologo || exit 100
dotnet build "$ROOT/interop/v03/dotnet/InteropV03Client" -c Release -v quiet --nologo || exit 100

"$PY" "$ROOT/interop/v03/python/py_server_v03.py" "$PY_PORT" & pids+=($!)
dotnet run --no-build -c Release --project "$ROOT/interop/v03/dotnet/InteropV03Agent" -- "$NET_PORT" & pids+=($!)
wait_for "http://127.0.0.1:$PY_PORT" || exit 101
wait_for "http://127.0.0.1:$NET_PORT" || exit 101

fails=0
# gRPC runs on PY_PORT + 1 and NET_PORT + 1 (HTTP/2): the cards list those addresses.
for binding in JSONRPC HTTP+JSON GRPC; do
  "$PY" "$ROOT/interop/v03/python/py_client_v03.py" "http://127.0.0.1:$NET_PORT" "$binding" || fails=$((fails + 1))
  dotnet run --no-build -c Release --project "$ROOT/interop/v03/dotnet/InteropV03Client" -- "http://127.0.0.1:$PY_PORT/" "$binding" || fails=$((fails + 1))
  # The same agent must still serve v1.0 clients from its blended card.
  "$PY10" "$ROOT/interop/python/py_client.py" "http://127.0.0.1:$NET_PORT" "$binding" || fails=$((fails + 1))
done

echo
if [ "$fails" -eq 0 ]; then echo "INTEROP v0.3: all runs passed"; else echo "INTEROP v0.3: $fails run(s) failed"; fi
exit "$fails"
