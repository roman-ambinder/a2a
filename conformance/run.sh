#!/usr/bin/env bash
# Runs the official A2A conformance suites against this SDK:
#   - a2a-tck        (a2aproject/a2a-tck)            JSON-RPC, HTTP+JSON, gRPC
#   - ACTS in a2a-itk (a2aproject/a2a-itk run_acts)  JSON-RPC, HTTP+JSON (rest), gRPC
# The suites are cloned at pinned commits into a cache folder outside this repo and run from their own folders.
# Results go to $CONFORMANCE_OUT; conformance/check.py compares them with conformance/expected-failures.txt.
#
# Requires: .NET 10 SDK, git, uv (https://docs.astral.sh/uv/), python3.
#   conformance/run.sh              # both suites
#   SUITES=tck conformance/run.sh   # one suite
# Exit code: 0 when every failure is in the expected-failures list, 1 otherwise, 2 on setup errors.
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CACHE="${CONFORMANCE_CACHE:-${RUNNER_TEMP:-$HOME/.cache}/a2a-conformance}"
OUT="${CONFORMANCE_OUT:-$ROOT/conformance/results}"
SUITES="${SUITES:-tck acts}"

# Pinned suite versions. Change these on purpose, then update conformance/README.md.
TCK_REPO=https://github.com/a2aproject/a2a-tck.git
TCK_REF=70f6f73c30208e80c889141907e094899e6e9f05   # after tag 1.0.0.alpha3. The next commit only moves to Python 3.15, where grpcio has no wheels (30+ min source build).
ITK_REPO=https://github.com/a2aproject/a2a-itk.git
ITK_REF=d53108aaf378a38eaff64ea39029a3d795591827   # main, 2026-10-26 (same pin as a2a-dotnet)

# Python with prebuilt grpcio wheels; uv downloads it if missing.
export UV_PYTHON="${UV_PYTHON:-3.12}"

PORT=9999; REDUCED_PORT=9100; AUTH_PORT=9200
setup_failed=0
pids=()
stop_agents() { for p in "${pids[@]}"; do kill "$p" 2>/dev/null || true; done; wait 2>/dev/null; pids=(); }
trap stop_agents EXIT

fetch() { # repo dir ref
  if [ ! -d "$2/.git" ]; then mkdir -p "$2" && git -C "$2" init -q && git -C "$2" remote add origin "$1"; fi
  if [ "$(git -C "$2" rev-parse HEAD 2>/dev/null)" != "$3" ]; then
    git -C "$2" fetch -q --depth 1 origin "$3" && git -C "$2" checkout -q --force FETCH_HEAD || return 1
  fi
  git -C "$2" clean -qfdx -e .venv
}

wait_for() {
  for _ in $(seq 1 120); do
    curl -sf -o /dev/null "http://127.0.0.1:$1/.well-known/agent-card.json" && return 0
    sleep 0.5
  done
  echo "Timed out waiting for the agent on port $1" >&2
  return 1
}

start_agent() { # port [VAR=value ...]
  local port=$1; shift
  env "$@" dotnet "$ROOT/conformance/dotnet/ConformanceAgent/bin/Release/net10.0/ConformanceAgent.dll" "$port" \
    >"$OUT/agent-$port.log" 2>&1 & pids+=($!)
  wait_for "$port"
}

mkdir -p "$OUT" "$CACHE"
rm -rf "${OUT:?}"/tck "${OUT:?}"/acts
dotnet build "$ROOT/conformance/dotnet/ConformanceAgent" -c Release -v quiet --nologo || exit 2

for suite in $SUITES; do
  case "$suite" in
    tck)
      fetch "$TCK_REPO" "$CACHE/a2a-tck" "$TCK_REF" || exit 2
      start_agent "$PORT" || exit 2
      mkdir -p "$OUT/tck"
      # Non-zero exit on failures is expected; check.py decides.
      (cd "$CACHE/a2a-tck" && uv sync --frozen -q && uv run --frozen python run_tck.py \
        --sut-host "http://127.0.0.1:$PORT" -- -p no:cacheprovider) || true
      cp "$CACHE/a2a-tck/reports/compatibility.json" "$CACHE/a2a-tck/reports/junitreport.xml" "$OUT/tck/" || setup_failed=1
      stop_agents
      ;;
    acts)
      fetch "$ITK_REPO" "$CACHE/a2a-itk" "$ITK_REF" || exit 2
      # Default agent, plus the two deviation agents ACTS needs (see a2a-itk docs/conformance/01_running.md).
      start_agent "$PORT" || exit 2
      start_agent "$REDUCED_PORT" ITK_ACTS_REDUCED_CAPABILITIES=1 || exit 2
      start_agent "$AUTH_PORT" ITK_ACTS_AUTH=1 || exit 2
      mkdir -p "$OUT/acts"
      (cd "$CACHE/a2a-itk" && uv sync --frozen -q && uv run --frozen python run_acts.py \
        --sut-url "http://127.0.0.1:$PORT" \
        --reduced-sut-url "http://127.0.0.1:$REDUCED_PORT" \
        --auth-sut-url "http://127.0.0.1:$AUTH_PORT" \
        --behaviors "$ROOT/conformance/acts/sut-behaviors.yaml" \
        --sdk roman-ambinder-a2a --language dotnet --transport all --out "$OUT/acts") || true
      ls "$OUT"/acts/acts-report-*.json >/dev/null 2>&1 || setup_failed=1
      stop_agents
      ;;
    *) echo "Unknown suite: $suite" >&2; exit 2 ;;
  esac
done

python3 -I "$ROOT/conformance/check.py" "$OUT" "$ROOT/conformance/expected-failures.txt"; rc=$?
if [ "$setup_failed" -ne 0 ]; then echo "A suite produced no report (see output above)." >&2; exit 2; fi
exit "$rc"
