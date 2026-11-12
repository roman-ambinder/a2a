# A2A benchmarks

BenchmarkDotNet benchmarks for the SDK's hot paths (send, streaming, JSON-RPC envelopes, card verification, concurrent
load). CI uses them as an allocation regression gate.

Run all benchmarks (from the repo root):

    dotnet run -c Release --project tests/A2A.Benchmarks -- --job short --exporters json --filter '*'

Compare with the committed baseline (fails on allocation regressions above 10%; time is informational):

    python3 tests/A2A.Benchmarks/compare.py BenchmarkDotNet.Artifacts/results tests/A2A.Benchmarks/baseline.json --threshold 0.10

Add `--time-threshold 0.25` to also fail on mean-time regressions (local use only).

## CI gate

The `Benchmarks (regression check)` job in `.github/workflows/ci.yml` blocks CI. It runs on `ubuntu-latest` and fails when:

- a benchmark allocates more than 10% above `baseline.json`, or
- a benchmark is in the results but not in the baseline, or the other way round.

One short run is noisy, so when the first comparison fails the job runs the benchmarks once more and fails only if the
second comparison fails too. The results are uploaded as the `benchmark-results` artifact.

Mean time is printed but does not fail the job. On shared runners the same commit measured 13.6 ms and 31.7 ms for
`StreamingTaskBenchmark.ThousandEvents` in two runs, so a timing gate would be noise. Allocations moved by about 1%
between the same runs, so 10% leaves room for runner noise and still catches real regressions.

## Concurrent load

`ConcurrentLoadBenchmark` measures many callers at once against one shared in-process server. The other benchmarks have one caller at a time.

- `ConcurrentSendMessage`: 64 callers start together, each does one SendMessage round trip. Results are per request.
- `ConcurrentStreams`: 16 callers start together, each reads one SendStreamingMessage stream of 100 events. Results are per stream.

Time for these two is especially noisy on shared runners, because it depends on how many cores the runner gives the callers. Read it as a rough hint. Allocations stay stable between runs, so they are what the gate checks.

## Task copies

`TaskCopyBenchmark` measures what one long task costs in list copies (ADR-004). Each operation runs one task to the end with
a blocking SendMessage.

- `StatusMessages4000` / `StatusMessages2000`: status updates, each with a message, so the history and the timeline grow
  by one entry per event.
- `Appends2000` / `Appends1000`: appends to one artifact through the in-memory store wrapped without `IBatchTaskCommit`,
  so each append is its own commit.

Each case has a twin with half the events. When a task costs O(1) per event, doubling the events about doubles the
allocation; an O(n) copy per event makes it grow about 3 to 3.5 times.

## Regenerate the baseline

`baseline.json` must come from the CI runner, not a dev machine (allocations differ by OS and CPU). Do this when you
add, rename, or remove a benchmark, or when an allocation change is intended.

1. Push your branch. If the benchmarks job fails only because of the expected change, use that run. Otherwise start one:

       gh workflow run CI --ref <branch>

2. Find the run id and wait for the benchmarks job to finish:

       gh run list --workflow CI --branch <branch> --limit 1
       gh run watch <run-id>

3. Download the results and write the baseline (from the repo root):

       gh run download <run-id> -n benchmark-results -D /tmp/bench-results
       python3 tests/A2A.Benchmarks/compare.py /tmp/bench-results tests/A2A.Benchmarks/baseline.json --write-baseline

4. Commit `baseline.json` and say in the PR why the numbers changed.

Do not edit the numbers by hand.
