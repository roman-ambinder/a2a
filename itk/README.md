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
- An incoming message whose part holds an `itk.Instruction` (`instruction.proto`, a copy of a2a-itk's) is run:
  `ReturnResponse` answers (and with `hold_task` stays `WORKING` until canceled), `SeriesOfSteps` runs its children in
  order, and `CallAgent` forwards the nested instruction with this SDK's client, over the named transport, with the
  `send_message`, `push_notification` or `resubscribe` behaviour.
- Peers are called through `V03CompatClientFactory`, which reads v1.0 and v0.3 cards, so one code path reaches both
  lines of every peer.

## Run

Needs Docker (or Podman), git and curl. It runs code downloaded from a2a-itk and from every peer SDK, in a container.

```bash
itk/run_itk.sh
```

```bash
ITK_NIGHTLY_RUN=TRUE itk/run_itk.sh   # every peer in the matrix, one pair at a time
```

The a2a-itk revision is pinned in `run_itk.sh` (`A2A_ITK_REVISION`). The script stops when `instruction.proto` no
longer matches that revision's copy. Combinations that are known to fail are recorded in a2a-itk's
`known_failures.yaml` under `sut_sdk: dotnet`, the matrix key this SDK runs under.

## Known differences from other ITK agents

- **Plaintext gRPC addresses.** ITK agents list cleartext gRPC as a bare `host:port`, which this SDK's client reads as
  TLS (`GrpcTransport.ParseAddress`). For a gRPC hop the agent calls a bare loopback address over plain HTTP/2 instead
  (`ItkAgent.PlaintextLoopbackGrpc`). Its own card uses the same bare form, as the other agents expect.
