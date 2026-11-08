# A2A benchmarks

Run all benchmarks (from the repo root):

    dotnet run -c Release --project tests/A2A.Benchmarks -- --job short --exporters json --filter '*'

Compare with the committed baseline (fails on allocation regressions above 10%; time is informational):

    python3 tests/A2A.Benchmarks/compare.py BenchmarkDotNet.Artifacts/results tests/A2A.Benchmarks/baseline.json --threshold 0.10

Add `--time-threshold 0.25` to also fail on mean-time regressions (local use only).

## CI gate

The `Benchmarks (regression check)` job in `.github/workflows/ci.yml` blocks CI. It runs on `ubuntu-latest` and fails when:

- a benchmark allocates more than 10% above `baseline.json`, or
- a benchmark is in the results but not in the baseline, or the other way round.

Mean time is printed but does not fail the job. On shared runners the same commit measured 13.6 ms and 31.7 ms for
`StreamingTaskBenchmark.ThousandEvents` in two runs, so a timing gate would be noise. Allocations moved by about 1%
between the same runs, so 10% leaves room for runner noise and still catches real regressions.

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
