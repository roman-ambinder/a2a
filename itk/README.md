# ITK agent

The agent that [a2a-itk](https://github.com/a2aproject/a2a-itk) runs as this SDK's system under test in its
cross-SDK traversal suite. ITK starts a cluster of agents built from several SDKs and versions (Python, Go,
TypeScript, Java, Rust and the official .NET SDK, v1.0 and v0.3 lines), sends one nested instruction through all of
them over JSON-RPC, HTTP+JSON or gRPC, and checks the trace that comes back. `interop/` checks this SDK against the
Python SDK only; this checks it against every SDK in ITK's matrix.

The contract is a2a-itk's `docs/interoperability/05_sdk-integration.md`:

- Started as `dotnet Itk.dll --httpPort N --grpcPort M`. HTTP/1.1 on N serves JSON-RPC and HTTP+JSON at `/jsonrpc`,
  v1.0 and v0.3 on the same URL (`EnableV03Compat`), and the Agent Card at `/.well-known/agent-card.json`.
  Cleartext HTTP/2 on M serves gRPC, v1.0 and v0.3.
- An incoming message whose part holds an `itk.Instruction` (`Proto/instruction.proto`, a copy of a2a-itk's) is run:
  `ReturnResponse` answers (and with `hold_task` stays `WORKING` until canceled), `SeriesOfSteps` runs its children in
  order, and `CallAgent` forwards the nested instruction with this SDK's client, over the named transport, with the
  `send_message`, `push_notification` or `resubscribe` behaviour.
- Peers are called through `V03CompatClientFactory`, which reads v1.0 and v0.3 cards, so one code path reaches both
  lines of every peer.

## Run

Needs Docker (or Podman), git, curl and bash 4 or later (on macOS, `brew install bash`; the script finds it). It runs code downloaded from a2a-itk and from every peer SDK, in a container.

```bash
itk/run_itk.sh
```

```bash
ITK_NIGHTLY_RUN=TRUE itk/run_itk.sh   # every peer in the matrix, one pair at a time
```

The run leaves its results in `itk/raw_results.json`. To print them and get a non-zero exit code unless every
scenario passed (the nightly path of the driver exits 0 even when scenarios fail):

```bash
python3 itk/check_results.py
```

CI runs the PR set on every pull request (job `itk` in `ci.yml`) and the nightly set every night
(`.github/workflows/itk-nightly.yml`).

The a2a-itk revision is pinned in `run_itk.sh` (`A2A_ITK_REVISION`). The script stops when `Proto/instruction.proto` no
longer matches that revision's copy. Combinations that are known to fail are recorded in a2a-itk's
`known_failures.yaml` under `sut_sdk: dotnet`, the matrix key this SDK runs under.

## Plaintext gRPC addresses

ITK agents list cleartext gRPC as a bare `host:port`, which this SDK's client reads as TLS by default. The agent calls
gRPC peers with `UseGrpc(null, useTlsForSchemeLessAddresses: false)` (and the same for `UseGrpcV03`), so a bare address
is called over cleartext HTTP/2. Its own card uses the same bare form, as the other agents expect.
