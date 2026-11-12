# Conformance

Runs the official A2A conformance suites against this SDK, on JSON-RPC, HTTP+JSON and gRPC. See also the
[interop and conformance guide](https://github.com/roman-ambinder/a2a/blob/main/docs/site/guides/interop-and-conformance.md).

| Suite | Source | Pinned commit |
|---|---|---|
| TCK | [a2aproject/a2a-tck](https://github.com/a2aproject/a2a-tck) | `70f6f73` (after tag `1.0.0.alpha3`) |
| ACTS | [a2aproject/a2a-itk](https://github.com/a2aproject/a2a-itk) (`run_acts.py`) | `d53108a` |

The TCK pin is one commit behind its `main`. That commit only moves the TCK to Python 3.15, where `grpcio` has no prebuilt wheels and takes over 30 minutes to compile. The test code is the same.

The ITK cross-SDK traversal suite in `a2a-itk` is not run. `interop/` covers two-way interop with the official Python SDK.

## Run

Needs the .NET 10 SDK, git, [uv](https://docs.astral.sh/uv/) and python3.

```bash
conformance/run.sh
```

```bash
SUITES=acts conformance/run.sh   # or SUITES=tck: one suite only
```

The script clones the suites at the pinned commits into a cache folder outside this repo (`CONFORMANCE_CACHE`, default
`~/.cache/a2a-conformance`) and runs them from there. It runs downloaded code, so treat it that way. Reports go to `conformance/results/` (`CONFORMANCE_OUT`). The agent listens on port 9999; ACTS also starts instances
on 9100 (reduced capabilities) and 9200 (auth). Exit code: 0 when every failure is expected, 1 otherwise, 2 on setup
errors.

CI runs this in the `conformance` job. The job fails on any failure that is not in [expected-failures.txt](expected-failures.txt). `check.py` also lists entries that now pass, so they can be removed.

## Results

Counts come from `check.py`: one per requirement (TCK) or test (ACTS), per binding.

| Suite | Binding | Pass | Fail | Skip | MUST failures |
|---|---|---|---|---|---|
| ACTS | JSON-RPC | 101 | 0 | 0 | 0 |
| ACTS | HTTP+JSON | 94 | 0 | 0 | 0 |
| ACTS | gRPC | 88 | 0 | 0 | 0 |
| TCK | JSON-RPC | 72 | 3 | 6 | 2 |
| TCK | HTTP+JSON | 70 | 4 | 4 | 3 |
| TCK | gRPC | 55 | 3 | 2 | 2 |
| TCK | Agent card | 8 | 0 | 1 | 0 |

Last run: 2026-11-12, at the pinned commits above, against `main` at `dbf82b4` (after the v0.3 gRPC and push changes, the streaming idle-timeout fix and the gRPC channel pool). `check.py` reported no unexpected failures, and every count matched the table. ACTS rated each binding CONFORMANT (MUST, SHOULD and MAY all passed). Every TCK failure in the table is in [expected-failures.txt](expected-failures.txt).

Every TCK failure is explained below. None is an open SDK bug.

Skips are capability tests that need an agent without the capability, or optional features. The TCK also lists requirements as `NOT TESTED` or manual (for example in-task auth and client behavior); those have no automated check.

## Expected failures

| TCK requirement | Bindings | Why |
|---|---|---|
| `CORE-SEND-003` | all | TCK bug ([#202](https://github.com/a2aproject/a2a-tck/issues/202), fix in [PR #203](https://github.com/a2aproject/a2a-tck/pull/203)). The requirement says the call must fail, but the entry has no `expected_error`, so the runner demands success. The SDK returns `ContentTypeNotSupported`. `HTTP_JSON-STATUS-001` and ACTS `CORE-SEND-004` check this and pass. |
| `CORE-MULTI-002a` | all | TCK bug, same issue and fix as above. The requirement says to reject a `contextId` the agent cannot accept, and the SDK does. |
| `CORE-MULTI-002` | all | SDK choice, `MAY` in spec 3.4.1. Unknown client `contextId`s are rejected by default. `ClientContextIdPolicy.AdoptIfUnknown` accepts them. |
| `HTTP_JSON-ERR-001` | HTTP+JSON | TCK bug (fix in [PR #238](https://github.com/a2aproject/a2a-tck/pull/238)). One test wants `application/json` on errors. Spec 11.6 shows `application/a2a+json`, and another TCK check expects that too. |

When those fixes land and the pin moves, `check.py` will report which entries to remove.

The false failures known from the TCK tracker (a2a-tck #141, #146, #201, #242, #244, #264) did not show up at this pin.

## How the agent differs from a default server

`dotnet/ConformanceAgent` is the system under test. Differences that affect results:

- **TCK pass only: message-ID dedupe is off** (`SUT_DISABLE_MESSAGE_DEDUPE=1`). The TCK helpers reuse one `messageId` for every task they create ([a2a-tck #267](https://github.com/a2aproject/a2a-tck/issues/267)), and the SDK treats `messageId` as an idempotency key (spec 3.3.1 allows this). With dedupe on, the TCK gets replays of old tasks or `IdempotencyKeyMismatch`, and about 30 requirements fail or go untested. ACTS runs with SDK defaults.
- **Default pass: only the extended card reads the bearer token.** ACTS starts some tasks with a token and reads them without one, and the SDK scopes tasks to their owner.
- ACTS also runs two more agent instances: `ITK_ACTS_AUTH=1` (every operation needs a token) and `ITK_ACTS_REDUCED_CAPABILITIES=1`.
- The gRPC interface URL is `host:port` with no scheme, because both suites pass it straight to a gRPC channel.
- Webhooks to private addresses are allowed, because the suites' receivers run on `127.0.0.1`.
