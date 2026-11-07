#!/usr/bin/env bash
# Two-way interop: .NET client <-> official Python a2a-sdk agent, Python client <-> .NET agent,
# over JSON-RPC, HTTP+JSON, and gRPC. Exit code = number of failed client runs.
#
# Requires: .NET 10 SDK, a Python with interop/python/requirements.txt installed.
#   PYTHON=/path/to/python interop/run.sh
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PY="${PYTHON:-$ROOT/interop/python/.venv/bin/python}"
PY_PORT="${PY_PORT:-5200}"
NET_PORT="${NET_PORT:-5300}"
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

dotnet build "$ROOT/interop/dotnet/InteropAgent" -c Release -v quiet --nologo || exit 100
dotnet build "$ROOT/interop/dotnet/InteropClient" -c Release -v quiet --nologo || exit 100

"$PY" "$ROOT/interop/python/py_server.py" "$PY_PORT" & pids+=($!)
dotnet run --no-build -c Release --project "$ROOT/interop/dotnet/InteropAgent" -- "$NET_PORT" & pids+=($!)
wait_for "http://127.0.0.1:$PY_PORT" || exit 101
wait_for "http://127.0.0.1:$NET_PORT" || exit 101

fails=0
for binding in JSONRPC HTTP+JSON GRPC; do
  dotnet run --no-build -c Release --project "$ROOT/interop/dotnet/InteropClient" -- "http://127.0.0.1:$PY_PORT/" "$binding" || fails=$((fails + 1))
  "$PY" "$ROOT/interop/python/py_client.py" "http://127.0.0.1:$NET_PORT" "$binding" || fails=$((fails + 1))
done

echo
if [ "$fails" -eq 0 ]; then echo "INTEROP: all runs passed"; else echo "INTEROP: $fails run(s) failed"; fi
exit "$fails"
