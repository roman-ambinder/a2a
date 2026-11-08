# A2A benchmarks

Run all benchmarks (from the repo root):

    dotnet run -c Release --project tests/A2A.Benchmarks -- --job short --exporters json --filter '*'

Compare with the committed baseline (fails on allocation regressions above 10%; time is informational):

    python3 tests/A2A.Benchmarks/compare.py BenchmarkDotNet.Artifacts/results tests/A2A.Benchmarks/baseline.json --threshold 0.10

Add `--time-threshold 0.25` to also fail on mean-time regressions.

Regenerate the baseline from the latest results:

    python3 tests/A2A.Benchmarks/compare.py BenchmarkDotNet.Artifacts/results tests/A2A.Benchmarks/baseline.json --write-baseline

The committed baseline comes from an ubuntu-latest CI run (the `benchmark-results` artifact of the `benchmarks` job), and the
CI job fails on an allocation regression above 10%. After an intended allocation change, regenerate it from the CI artifact of
the change, not from a local run (allocations differ by OS and CPU):

    gh run download <run-id> -n benchmark-results -D results
    python3 tests/A2A.Benchmarks/compare.py results tests/A2A.Benchmarks/baseline.json --write-baseline
