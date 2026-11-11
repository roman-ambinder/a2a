#!/usr/bin/env bash
# Checks the dotnet new templates end to end: packs the SDK into a local feed, installs the template pack in an isolated
# hive, makes a project from each template, builds it against the local packages, and runs the client against both agents.
#
#   src/A2A.Templates/test-templates.sh                 # packs everything first
#   src/A2A.Templates/test-templates.sh path/to/nupkgs  # uses packages packed before (CI: the pack job's artifacts)
set -euo pipefail

cd "$(dirname "$0")/../.."
WORK="$(mktemp -d)"
FEED="${1:-$WORK/feed}"
if [ $# -eq 0 ]; then
  mkdir -p "$FEED"
  for p in src/*/*.csproj; do dotnet pack "$p" -c Release -o "$FEED" -v quiet -nologo; done
fi
FEED="$(cd "$FEED" && pwd)"
TEMPLATES="$(ls "$FEED"/RomanAmbinder.A2A.Templates.*.nupkg | head -n 1)"

export NUGET_PACKAGES="$WORK/packages" # never a stale package of the same version from the global cache
HIVE="$WORK/hive"
cat > "$WORK/nuget.config" <<XML
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$FEED" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
XML

dotnet new install "$TEMPLATES" --debug:custom-hive "$HIVE" >/dev/null
for t in a2a-agent a2a-client a2a-chat-agent; do
  dotnet new "$t" -n "My${t//-/}" -o "$WORK/$t" --debug:custom-hive "$HIVE" >/dev/null
  dotnet build "$WORK/$t" -c Release --configfile "$WORK/nuget.config" -v quiet -nologo
done
for t in a2a-agent a2a-chat-agent; do
  dotnet new "$t" -n "MyKeyed${t//-/}" -o "$WORK/$t-apikey" --auth apikey --debug:custom-hive "$HIVE" >/dev/null
  dotnet build "$WORK/$t-apikey" -c Release --configfile "$WORK/nuget.config" -v quiet -nologo
done

# The analyzers ship in A2A.Server.AspNetCore: a host without UseA2A() gets A2A0001 at build time.
cp -r "$WORK/a2a-agent" "$WORK/broken-agent"
rm -rf "$WORK/broken-agent/bin" "$WORK/broken-agent/obj"
sed -i.bak '/app.UseA2A();/d' "$WORK/broken-agent/Program.cs"
if ! dotnet build "$WORK/broken-agent" -c Release --configfile "$WORK/nuget.config" -nologo 2>&1 | grep -q "warning A2A0001"; then
  echo "FAIL: the A2A analyzers did not report A2A0001 for a host without UseA2A()"
  exit 1
fi

PID=""
stop() { [ -n "$PID" ] && kill "$PID" 2>/dev/null && wait "$PID" 2>/dev/null; PID=""; }
trap 'stop; rm -rf "$WORK"' EXIT

check_agent() { # $1: template folder, $2: text the answer must contain
  ASPNETCORE_ENVIRONMENT=Development dotnet run --project "$WORK/$1" -c Release --no-build --no-launch-profile \
    --urls http://127.0.0.1:5199 >"$WORK/$1.log" 2>&1 &
  PID=$!
  for _ in $(seq 1 60); do curl -sf -o /dev/null http://127.0.0.1:5199/.well-known/agent-card.json && break; sleep 1; done
  out="$(dotnet run --project "$WORK/a2a-client" -c Release --no-build -- http://127.0.0.1:5199/ hello)"
  echo "$out"
  stop
  grep -q "answer: $2" <<<"$out" && grep -q "stream: $2" <<<"$out" || { cat "$WORK/$1.log"; echo "FAIL: $1"; exit 1; }
}

check_agent a2a-agent "You said: hello"
check_agent a2a-chat-agent "You said: hello"

# --auth apikey: the agent gets a key's hash (here through environment variables), the client sends the key.
KEY="a2a_$(head -c 32 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')"
export A2A__ApiKeys__Keys__0__CallerId=template-test
export A2A__ApiKeys__Keys__0__Hash="sha256:$(printf '%s' "$KEY" | sha256sum | cut -d' ' -f1)"
for t in a2a-agent-apikey a2a-chat-agent-apikey; do
  ASPNETCORE_ENVIRONMENT=Development dotnet run --project "$WORK/$t" -c Release --no-build --no-launch-profile \
    --urls http://127.0.0.1:5199 >"$WORK/$t.log" 2>&1 &
  PID=$!
  for _ in $(seq 1 60); do curl -sf -o /dev/null http://127.0.0.1:5199/.well-known/agent-card.json && break; sleep 1; done
  if A2A_API_KEY="" dotnet run --project "$WORK/a2a-client" -c Release --no-build -- http://127.0.0.1:5199/ hello \
    >"$WORK/$t-nokey.log" 2>&1; then
    stop; cat "$WORK/$t-nokey.log"; echo "FAIL: $t answered a client without a key"; exit 1
  fi
  out="$(A2A_API_KEY="$KEY" dotnet run --project "$WORK/a2a-client" -c Release --no-build -- http://127.0.0.1:5199/ hello)"
  echo "$out"
  stop
  grep -q "answer: You said: hello" <<<"$out" || { cat "$WORK/$t.log"; echo "FAIL: $t"; exit 1; }
done
unset A2A__ApiKeys__Keys__0__CallerId A2A__ApiKeys__Keys__0__Hash

# Outside Development, an agent made without --auth apikey asks for bearer tokens (AddBearerAuthentication). With no
# AddJwtBearer() registered yet, it refuses to start and says what to add, instead of answering every call with a 500.
if ASPNETCORE_ENVIRONMENT=Production timeout 60 dotnet run --project "$WORK/a2a-agent" -c Release --no-build \
  --no-launch-profile --urls http://127.0.0.1:5199 >"$WORK/prod.log" 2>&1; then
  cat "$WORK/prod.log"; echo "FAIL: the production agent started without an authentication scheme"; exit 1
fi
grep -q "AddJwtBearer" "$WORK/prod.log" || { cat "$WORK/prod.log"; echo "FAIL: the production agent did not say what to add"; exit 1; }
echo "OK: templates build (also --auth apikey), API-key agents refuse a client without the key and answer one with it, the analyzers report a missing UseA2A(), both agents answer the client in Development, and in Production an agent without a scheme refuses to start."
