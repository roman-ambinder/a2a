#!/usr/bin/env bash
# Runs the a2a command line tool (src/A2A.Cli) against samples/EchoAgent and checks each result.
# From anywhere in the repository:
#
#   samples/Cli/walkthrough.sh
#
# Starts EchoAgent on 127.0.0.1:5330 with a throwaway API key, and stops it at the end.
# Installed as a tool (dotnet tool install -g RomanAmbinder.A2A.Cli), each "a2a ..." line below is the same command.
set -u

cd "$(dirname "$0")/../.."

URL="http://127.0.0.1:5330"
LOGS="$(mktemp -d)"
AGENT_PID=""
failures=0

stop_agent() {
  if [ -n "$AGENT_PID" ] && kill -0 "$AGENT_PID" 2>/dev/null; then
    kill "$AGENT_PID" 2>/dev/null
    wait "$AGENT_PID" 2>/dev/null
  fi
}
trap stop_agent EXIT

echo "Building EchoAgent and the a2a tool..."
dotnet build samples/EchoAgent -c Release >"$LOGS/build-agent.log" 2>&1 || { cat "$LOGS/build-agent.log"; exit 1; }
dotnet build src/A2A.Cli -c Release >"$LOGS/build-cli.log" 2>&1 || { cat "$LOGS/build-cli.log"; exit 1; }

# A throwaway key from the tool itself ('a2a keys new' works offline). The agent gets only the hash; the key goes to the
# commands in an environment variable, because options show in process lists and shell history.
KEYS="$(dotnet src/A2A.Cli/bin/Release/net10.0/A2A.Cli.dll keys new)"
export A2A_API_KEY="$(awk '$1 == "key:" { print $2 }' <<<"$KEYS")"
KEY_HASH="$(awk '$1 == "hash:" { print $2 }' <<<"$KEYS")"
dotnet samples/EchoAgent/bin/Release/net10.0/EchoAgent.dll "--BaseUrl=$URL" "--EchoAgent:ApiKeyHash=$KEY_HASH" \
  >"$LOGS/agent.log" 2>&1 &
AGENT_PID=$!
for _ in $(seq 1 60); do
  curl -fsS -o /dev/null "$URL/.well-known/agent-card.json" 2>/dev/null && break
  sleep 1
done

# The agent URL for every command. --allow-http is needed for an http:// URL; the key still goes only to loopback. It goes
# right after the command name, so that it stays an option when a command ends its options with "--".
export A2A_AGENT_URL="$URL/"
a2a() {
  local command="$1"
  shift
  dotnet run --project src/A2A.Cli -c Release --no-build -- "$command" --allow-http "$@"
}

# check <expected exit code> <what> <command...>: runs the command, prints its output, compares the exit code.
check() {
  local expected="$1" what="$2"
  shift 2
  echo
  echo "\$ a2a $*"
  a2a "$@" >"$LOGS/out" 2>&1
  local code=$?
  sed 's/^/  /' "$LOGS/out"
  if [ "$code" = "$expected" ]; then
    echo "  -> exit $code ($what)"
  else
    echo "  -> FAIL: exit $code, expected $expected ($what)"
    failures=$((failures + 1))
  fi
}

# The card fetch never carries credentials. The card declares the API key scheme, so later commands send A2A_API_KEY.
check 0 "card" card

# A direct reply: EchoAgent answers "ping" with "pong" and makes no task.
check 0 "direct reply" send ping
grep -q pong "$LOGS/out" || { echo "  -> FAIL: no 'pong'"; failures=$((failures + 1)); }

# A task that streams its answer word by word. --json prints one StreamResponse per line.
check 0 "stream" stream "hello from the a2a tool"
check 0 "stream as NDJSON" stream --json "one two three"
[ "$(wc -l <"$LOGS/out")" -ge 4 ] || { echo "  -> FAIL: expected several JSON lines"; failures=$((failures + 1)); }

# Start a long task without waiting (200 words at 150 ms each: 30 seconds), cancel it, then look at it.
# Cancel comes right after the start: a task that already completed cannot be canceled (exit 6), and each command here
# starts a new process, which takes seconds on a busy machine.
check 0 "send --no-wait" send --no-wait --json "$(printf 'word %.0s' $(seq 1 200))"
task_id="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["task"]["id"])' <"$LOGS/out" 2>/dev/null)"
if [ -z "$task_id" ]; then
  echo "  -> FAIL: no task id in the answer"
  failures=$((failures + 1))
else
  # "--" ends the options: a task id is random base64url, so about one in 64 starts with a dash.
  check 0 "cancel" cancel -- "$task_id"
  check 0 "get" get -- "$task_id"
  grep -q "^State: *Canceled" "$LOGS/out" || { echo "  -> FAIL: the task is not canceled"; failures=$((failures + 1)); }
  check 0 "list" list --page-size 5
fi

# Errors have their own exit codes, so a script can tell them apart.
check 5 "not found" get no-such-task
A2A_API_KEY="" check 4 "no API key" send ping

echo
echo "Logs in $LOGS"
if [ "$failures" -gt 0 ]; then
  echo "FAIL: $failures check(s) failed."
  exit 1
fi
echo "OK: card, send, stream, get, list and cancel worked, and errors gave their exit codes."
