#!/usr/bin/env python3
"""Compare BenchmarkDotNet results with a committed baseline.

usage: compare.py <results-dir> <baseline.json> [--threshold 0.10] [--time-threshold 0.25]
       compare.py <results-dir> <baseline.json> --write-baseline   (regenerate baseline.json)

Reads *-report-full-compressed.json (or *-report-full.json) files. Benchmark key is Type.Method.
Exits 1 when allocation exceeds the baseline by more than --threshold (default 0.10), or a benchmark is missing on either side.
Mean time is always printed but is informational: it only fails when --time-threshold is given (timings are noisy on shared runners).
"""
import argparse
import glob
import json
import os
import sys


def load_results(results_dir):
    files = glob.glob(os.path.join(results_dir, "*-report-full-compressed.json"))
    if not files:
        files = glob.glob(os.path.join(results_dir, "*-report-full.json"))
    if not files:
        sys.exit(f"error: no BenchmarkDotNet *-report-full*.json files in {results_dir}")
    out = {}
    no_statistics = []
    for path in sorted(files):
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        for b in data.get("Benchmarks", []):
            key = f"{b['Type']}.{b['Method']}"
            # BenchmarkDotNet writes null when the benchmark failed to run; there is nothing to compare.
            if not b.get("Statistics") or not b.get("Memory"):
                no_statistics.append(key)
                continue
            out[key] = {
                "meanNs": float(b["Statistics"]["Mean"]),
                "allocatedBytes": int(b["Memory"]["BytesAllocatedPerOperation"]),
            }
    if no_statistics:
        sys.exit("error: these benchmarks produced no statistics (they failed to run, see the benchmark run's output for the "
                 "exception):\n" + "\n".join(f"  {key}" for key in no_statistics))
    return out


def pct(current, base):
    if base == 0:
        return 0.0 if current == 0 else float("inf")
    return (current - base) / base * 100.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("results_dir")
    ap.add_argument("baseline")
    ap.add_argument("--threshold", type=float, default=0.10)
    ap.add_argument("--time-threshold", type=float, default=None,
                    help="also fail when mean time exceeds baseline by this fraction (default: off, informational)")
    ap.add_argument("--write-baseline", action="store_true")
    args = ap.parse_args()

    current = load_results(args.results_dir)
    if args.write_baseline:
        with open(args.baseline, "w", encoding="utf-8") as f:
            json.dump(current, f, indent=2, sort_keys=True)
            f.write("\n")
        print(f"wrote {len(current)} benchmarks to {args.baseline}")
        return 0

    with open(args.baseline, encoding="utf-8") as f:
        baseline = json.load(f)

    failed = False
    for key in sorted(set(baseline) - set(current)):
        print(f"ERROR: {key} is in the baseline but missing from the results")
        failed = True
    for key in sorted(set(current) - set(baseline)):
        print(f"ERROR: {key} is in the results but missing from the baseline")
        failed = True

    limit = args.threshold * 100.0
    header = f"{'benchmark':<48} {'metric':<10} {'baseline':>16} {'current':>16} {'change':>9}"
    print(header)
    print("-" * len(header))
    for key in sorted(set(baseline) & set(current)):
        for metric, label in (("meanNs", "mean ns"), ("allocatedBytes", "alloc B")):
            b, c = baseline[key][metric], current[key][metric]
            change = pct(c, b)
            if metric == "meanNs":
                if args.time_threshold is None:
                    print(f"{key:<48} {label:<10} {b:>16,.0f} {c:>16,.0f} {change:>+8.1f}%  (informational)")
                    continue
                bad = change > args.time_threshold * 100.0
            else:
                bad = change > limit
            failed |= bad
            print(f"{key:<48} {label:<10} {b:>16,.0f} {c:>16,.0f} {change:>+8.1f}%{'  REGRESSION' if bad else ''}")

    if failed:
        print("\nFAILED: allocation (or enabled time) regression, or missing benchmark")
        return 1
    print(f"\nOK: allocations within {limit:.0f}% of baseline" + ("" if args.time_threshold is not None else " (time informational)"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
