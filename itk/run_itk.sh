#!/bin/bash
# Runs the a2a-itk cross-SDK traversal suite with this SDK's agent (this folder) as the system under test.
# A thin shim over a2a-itk's shared driver, like the itk/run_itk.sh of the official SDKs. See README.md.
#
# Needs Docker (or Podman), git and curl. The driver builds a2a-itk's image, which carries the Python, Go, Node, Java,
# Rust and .NET toolchains of the peers, so the first run is slow. It runs code downloaded from a2a-itk and from every
# peer SDK it lists; treat it that way.
#
# Env: A2A_ITK_REVISION  a2a-itk commit to run (default below)
#      ITK_NIGHTLY_RUN   TRUE for the nightly set (every peer in the matrix) instead of the PR set
#      ITK_LOG_LEVEL     DEBUG keeps the agents' logs in itk/logs/
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# Pinned so a change in the suite arrives as a reviewed change here.
export A2A_ITK_REVISION="${A2A_ITK_REVISION:-65bce0ab4c09615b06db18f5d390beb82e5c4a6a}"

# Peers and known failures are looked up under this matrix key. "dotnet" also brings the official A2A .NET SDK's own
# lines in as peers (include_own_lines in the PR set), so this SDK is checked against it too.
ITK_SDK_NAME=dotnet
ITK_MATRIX_SDK=dotnet
ITK_METRICS_NAME=roman-ambinder-a2a
ITK_SCENARIO_SET=shared
# instruction.proto is committed with a header; the check below compares it with the pinned revision instead.
ITK_COPY_PROTO=0

# The agent is published inside the container on first spawn, only when publish/ is absent; remove it so a later run
# cannot start an old build.
itk_extra_cleanup() { rm -rf publish bin obj; }

if [ ! -d a2a-itk ]; then
  git clone https://github.com/a2aproject/a2a-itk.git a2a-itk
fi
(cd a2a-itk && git fetch -q origin && git checkout -q "$A2A_ITK_REVISION")

# The committed proto must match the pinned revision's (minus its two header lines).
if ! diff <(tail -n +4 instruction.proto) a2a-itk/protos/instruction.proto > /dev/null; then
  echo "itk/instruction.proto differs from a2a-itk $A2A_ITK_REVISION. Update it:" >&2
  diff <(tail -n +4 instruction.proto) a2a-itk/protos/instruction.proto >&2 || true
  exit 2
fi

# The shared driver sets its own options (set -ex) and reads unset variables.
set +u +o pipefail
source a2a-itk/scripts/run_itk_shared.sh
