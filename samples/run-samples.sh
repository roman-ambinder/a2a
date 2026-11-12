#!/usr/bin/env bash
# Runs the samples and checks that each one works. From anywhere in the repository:
#
#   samples/run-samples.sh                  # every sample that needs no Docker
#   samples/run-samples.sh echo mcp         # only the named ones
#   samples/run-samples.sh --with-docker    # also the Docker samples: multi, storage, outbox
#
# Names: chat, agentframework, elicitation, typed, echo, mcp, testing, grpc, v03, cli, tenant, observed, memory,
#        client, multi, storage, outbox.
# Each sample listens on 127.0.0.1 only. EchoAgent gets the hash of a throwaway API key made here.
set -u

cd "$(dirname "$0")/.."

ECHO_URL="http://localhost:5100"
MCP_URL="http://127.0.0.1:5290"
LOGS="${SAMPLES_LOG_DIR:-$(mktemp -d)}" # SAMPLES_LOG_DIR keeps the logs in a known folder (CI uploads it)
mkdir -p "$LOGS"
declare -a RESULTS=()
BACKGROUND_PID=""

with_docker=false
selected=()
for arg in "$@"; do
  case "$arg" in
    --with-docker) with_docker=true ;;
    -h|--help) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) selected+=("$arg") ;;
  esac
done

wanted() {
  [ ${#selected[@]} -eq 0 ] && return 0
  local s; for s in "${selected[@]}"; do [ "$s" = "$1" ] && return 0; done
  return 1
}

record() { RESULTS+=("$1|$2"); }

stop_background() {
  if [ -n "$BACKGROUND_PID" ] && kill -0 "$BACKGROUND_PID" 2>/dev/null; then
    kill "$BACKGROUND_PID" 2>/dev/null
    wait "$BACKGROUND_PID" 2>/dev/null
  fi
  BACKGROUND_PID=""
}
trap stop_background EXIT

# Waits until $1 answers (up to 90 s: the first run also builds the project).
wait_for() {
  local i
  for i in $(seq 1 90); do
    curl -sf -o /dev/null "$1" && return 0
    [ -n "$BACKGROUND_PID" ] && ! kill -0 "$BACKGROUND_PID" 2>/dev/null && return 1
    sleep 1
  done
  return 1
}

# Waits until file $1 has a line containing $2 (up to 30 s), while the background sample runs.
wait_for_line() {
  local i
  for i in $(seq 1 30); do
    grep -qF -- "$2" "$1" 2>/dev/null && return 0
    [ -n "$BACKGROUND_PID" ] && ! kill -0 "$BACKGROUND_PID" 2>/dev/null && return 1
    sleep 1
  done
  return 1
}

# Runs a sample that starts its own agent, calls it and exits.
run_self_contained() {
  local name="$1" project="$2"
  echo "== $name ($project)"
  if dotnet run --project "$project" >"$LOGS/$name.log" 2>&1; then
    record "$name" PASS
  else
    record "$name" "FAIL (see $LOGS/$name.log)"
  fi
  tail -n 5 "$LOGS/$name.log"
}

run_echo() {
  echo "== echo (samples/EchoAgent + samples/EchoClient)"
  # A throwaway API key in the A2A form (a2a_ and 32 random bytes, base64url) and its SHA-256 hash, for the agent.
  local key hash
  key="a2a_$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')"
  hash="sha256:$(printf '%s' "$key" | sha256sum | cut -d' ' -f1)"
  dotnet run --project samples/EchoAgent -- "--EchoAgent:ApiKeyHash=$hash" >"$LOGS/echo-agent.log" 2>&1 &
  BACKGROUND_PID=$!
  if ! wait_for "$ECHO_URL/.well-known/agent-card.json"; then
    record echo "FAIL: agent did not start (see $LOGS/echo-agent.log)"
    stop_background
    return
  fi
  if dotnet run --project samples/EchoClient -- "$ECHO_URL/" "hello from run-samples" "$key" >"$LOGS/echo-client.log" 2>&1; then
    record echo PASS
  else
    record echo "FAIL (see $LOGS/echo-client.log)"
  fi
  tail -n 5 "$LOGS/echo-client.log"
  stop_background
}

# Starts McpBridge in mode $1, sends request $3 to $2, and checks that the reply contains $4.
run_mcp_mode() {
  local mode="$1" path="$2" body="$3" expect="$4"
  dotnet run --project samples/McpBridge -- "$mode" >"$LOGS/mcp-$mode.log" 2>&1 &
  BACKGROUND_PID=$!
  # The card answers as soon as the server is up, but in call mode the sample connects its MCP client after that.
  # It prints "Press Ctrl+C to stop." once it is ready for requests (samples/McpBridge/Program.cs).
  if ! wait_for "$MCP_URL/.well-known/agent-card.json" || ! wait_for_line "$LOGS/mcp-$mode.log" "Press Ctrl+C to stop."; then
    record "mcp $mode" "FAIL: did not start (see $LOGS/mcp-$mode.log)"
    stop_background
    return
  fi
  local reply
  reply=$(curl -s "$MCP_URL$path" -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
    -H 'A2A-Version: 1.0' -d "$body")
  if printf '%s' "$reply" | grep -q -- "$expect"; then
    record "mcp $mode" PASS
  else
    record "mcp $mode" "FAIL: reply has no '$expect'"
    printf '%s\n' "$reply" | head -c 400; echo
  fi
  stop_background
}

run_mcp() {
  echo "== mcp (samples/McpBridge, both modes)"
  run_mcp_mode expose /mcp \
    '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"text_shout","arguments":{"message":"hello bridge"}}}' \
    'HELLO BRIDGE'
  run_mcp_mode call /a2a \
    '{"jsonrpc":"2.0","id":1,"method":"SendMessage","params":{"message":{"messageId":"m1","role":"ROLE_USER","parts":[{"data":{"a":2,"b":3}}]}}}' \
    'TASK_STATE_COMPLETED'
}

run_testing() {
  echo "== testing (samples/TestingAgent)"
  if dotnet test --project samples/TestingAgent/TestingAgent.csproj >"$LOGS/testing.log" 2>&1; then
    record testing PASS
  else
    record testing "FAIL (see $LOGS/testing.log)"
  fi
  tail -n 4 "$LOGS/testing.log"
}

run_cli() {
  echo "== cli (samples/Cli/walkthrough.sh: the a2a tool against EchoAgent)"
  if samples/Cli/walkthrough.sh >"$LOGS/cli.log" 2>&1 && grep -q '^OK:' "$LOGS/cli.log"; then
    record cli PASS
  else
    record cli "FAIL (see $LOGS/cli.log)"
  fi
  tail -n 1 "$LOGS/cli.log"
}

# A2A_REQUIRE_DOCKER=1 (CI) makes a missing Docker a failure instead of a skip.
docker_missing() {
  if [ "${A2A_REQUIRE_DOCKER:-}" = 1 ]; then record "$1" "FAIL: Docker is not running"; else record "$1" "SKIP: Docker is not running"; fi
}

run_multi() {
  echo "== multi (samples/MultiInstance, Docker)"
  local compose="samples/MultiInstance/docker-compose.yml"
  if ! docker info >/dev/null 2>&1; then
    docker_missing multi
    return
  fi
  if ! docker compose -f "$compose" up --build -d >"$LOGS/multi-up.log" 2>&1; then
    record multi "FAIL: compose up (see $LOGS/multi-up.log)"
    return
  fi
  wait_for "http://127.0.0.1:5101/.well-known/agent-card.json" && wait_for "http://127.0.0.1:5102/.well-known/agent-card.json"
  if dotnet run --project samples/MultiInstance/Client >"$LOGS/multi.log" 2>&1 && grep -q '^OK:' "$LOGS/multi.log"; then
    record multi PASS
  else
    record multi "FAIL (see $LOGS/multi.log)"
  fi
  tail -n 3 "$LOGS/multi.log"
  docker compose -f "$compose" down -v >/dev/null 2>&1
}

# Starts a compose file, runs one sample project once per argument set, then removes the containers and volumes.
# run_compose <name> <compose file> <project> [args for run 1] [args for run 2] ...
run_compose() {
  local name="$1" compose="$2" project="$3"
  shift 3
  echo "== $name ($project, Docker)"
  if ! docker info >/dev/null 2>&1; then
    docker_missing "$name"
    return
  fi
  if ! docker compose -f "$compose" up -d --wait >"$LOGS/$name-up.log" 2>&1; then
    record "$name" "FAIL: compose up (see $LOGS/$name-up.log)"
    return
  fi
  local runs=("$@") ok=1 run
  [ ${#runs[@]} -eq 0 ] && runs=("")
  for run in "${runs[@]}"; do
    # shellcheck disable=SC2086 # each run's arguments are split on purpose
    if ! dotnet run --project "$project" -- $run >>"$LOGS/$name.log" 2>&1; then ok=0; fi
  done
  if [ $ok = 1 ]; then record "$name" PASS; else record "$name" "FAIL (see $LOGS/$name.log)"; fi
  grep '^OK:\|^FAIL' "$LOGS/$name.log" | tail -n 4
  docker compose -f "$compose" down -v >/dev/null 2>&1
}

run_storage() { run_compose storage samples/ArtifactStorage/docker-compose.yml samples/ArtifactStorage s3 azure; }
run_outbox() { run_compose outbox samples/ClientOutbox/docker-compose.yml samples/ClientOutbox; }

wanted chat        && run_self_contained chat samples/ChatAgent
wanted agentframework && run_self_contained agentframework samples/AgentFrameworkAgent
wanted elicitation && run_self_contained elicitation samples/ElicitationAgent
wanted typed       && run_self_contained typed samples/TypedSkillAgent
wanted echo        && run_echo
wanted mcp         && run_mcp
wanted testing     && run_testing
wanted grpc        && run_self_contained grpc samples/Grpc
wanted v03         && run_self_contained v03 samples/V03Compat
wanted cli         && run_cli
wanted tenant      && run_self_contained tenant samples/TenantAgent
wanted observed    && run_self_contained observed samples/ObservedAgent
wanted memory      && run_self_contained memory samples/MemoryAgent
wanted client      && run_self_contained client samples/ClientFeatures
for name in multi storage outbox; do
  if wanted "$name"; then
    if $with_docker || [ ${#selected[@]} -gt 0 ]; then "run_$name"; else record "$name" "SKIP: pass --with-docker"; fi
  fi
done

echo
echo "Results (logs in $LOGS):"
failed=0
for r in "${RESULTS[@]}"; do
  printf '  %-16s %s\n' "${r%%|*}" "${r#*|}"
  case "${r#*|}" in FAIL*) failed=1 ;; esac
done
exit $failed
