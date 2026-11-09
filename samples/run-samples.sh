#!/usr/bin/env bash
# Runs the samples and checks that each one works. From anywhere in the repository:
#
#   samples/run-samples.sh                  # every sample that needs no Docker
#   samples/run-samples.sh echo mcp         # only the named ones
#   samples/run-samples.sh --with-docker    # also MultiInstance (needs Docker)
#
# Names: chat, elicitation, typed, echo, mcp, testing, multi.
# Each sample listens on 127.0.0.1 only. EchoAgent gets a throwaway API key made here.
set -u

cd "$(dirname "$0")/.."

ECHO_URL="http://localhost:5100"
MCP_URL="http://127.0.0.1:5290"
LOGS="$(mktemp -d)"
declare -a RESULTS=()
BACKGROUND_PID=""

with_docker=false
selected=()
for arg in "$@"; do
  case "$arg" in
    --with-docker) with_docker=true ;;
    -h|--help) sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
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
  local key="sample-$RANDOM$RANDOM"
  dotnet run --project samples/EchoAgent -- "--EchoAgent:ApiKey=$key" >"$LOGS/echo-agent.log" 2>&1 &
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
  if ! wait_for "$MCP_URL/.well-known/agent-card.json"; then
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

run_multi() {
  echo "== multi (samples/MultiInstance, Docker)"
  local compose="samples/MultiInstance/docker-compose.yml"
  if ! docker info >/dev/null 2>&1; then
    record multi "SKIP: Docker is not running"
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

wanted chat        && run_self_contained chat samples/ChatAgent
wanted elicitation && run_self_contained elicitation samples/ElicitationAgent
wanted typed       && run_self_contained typed samples/TypedSkillAgent
wanted echo        && run_echo
wanted mcp         && run_mcp
wanted testing     && run_testing
if wanted multi; then
  if $with_docker || [ ${#selected[@]} -gt 0 ]; then run_multi; else record multi "SKIP: pass --with-docker"; fi
fi

echo
echo "Results (logs in $LOGS):"
failed=0
for r in "${RESULTS[@]}"; do
  printf '  %-16s %s\n' "${r%%|*}" "${r#*|}"
  case "${r#*|}" in FAIL*) failed=1 ;; esac
done
exit $failed
