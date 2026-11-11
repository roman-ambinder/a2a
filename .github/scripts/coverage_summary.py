#!/usr/bin/env python3
"""Merges Cobertura files from several test projects into one Markdown table of line coverage per package.

A line counts as covered when any test project hit it.

Usage: coverage_summary.py <directory> [--baseline FILE] [--write-baseline FILE]

--baseline FILE        Compare each package with the line coverage recorded in FILE (see .github/coverage-baseline.json).
                       Exit 1 when a package falls more than the file's tolerance below its recorded value, or when a
                       recorded package has no coverage data at all (a test project failed or was dropped).
--write-baseline FILE  Write this run's coverage as a baseline file. CI uploads it, so raising the baseline is a download
                       and a commit. Values are rounded down to one decimal, so the new baseline never asks for more
                       than this run measured.
"""
import argparse
import json
import math
import pathlib
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

DEFAULT_TOLERANCE = 0.5
# A package this many points above its baseline gets a hint to raise it.
RAISE_HINT = 1.0


def is_product(package: str) -> bool:
    return package.startswith("A2A.") and not package.endswith(".Tests")


def collect(root: str) -> tuple[dict[str, tuple[int, int]], int]:
    lines: dict[str, dict[tuple[str, str], bool]] = defaultdict(dict)
    files = list(pathlib.Path(root).rglob("*.cobertura.xml"))
    for file in files:
        for package in ET.parse(file).getroot().iterfind(".//package"):
            name = package.get("name", "")
            if not is_product(name):
                continue
            for cls in package.iterfind(".//class"):
                for line in cls.iterfind("./lines/line"):
                    key = (cls.get("filename", ""), line.get("number", ""))
                    lines[name][key] = lines[name].get(key, False) or int(line.get("hits", "0")) > 0
    return {name: (sum(hits.values()), len(hits)) for name, hits in lines.items() if hits}, len(files)


def percent(covered: int, total: int) -> float:
    return 100 * covered / total


def floor1(value: float) -> float:
    # The small epsilon keeps values such as 0.3 * 100 = 30.000000000000004 from flooring to 29.9.
    return math.floor(value * 10 + 1e-9) / 10


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Line coverage per package from Cobertura files.")
    parser.add_argument("directory")
    parser.add_argument("--baseline", type=pathlib.Path)
    parser.add_argument("--write-baseline", type=pathlib.Path)
    args = parser.parse_args(argv)

    packages, file_count = collect(args.directory)

    baseline: dict[str, float] = {}
    tolerance = DEFAULT_TOLERANCE
    if args.baseline:
        data = json.loads(args.baseline.read_text(encoding="utf-8"))
        baseline = {name: float(value) for name, value in data.get("packages", {}).items()}
        tolerance = float(data.get("tolerance", DEFAULT_TOLERANCE))

    print("## Line coverage\n")
    if not packages:
        print(f"No coverage data found ({file_count} files).")
        return 1 if baseline else 0

    if baseline:
        print(f"Compared with [`{args.baseline}`]({args.baseline}); a package fails when it is more than "
              f"{tolerance:g} points below its baseline.\n")
        print("| Package | Covered | Lines | % | Baseline | Change | |\n|---|---:|---:|---:|---:|---:|---|")
    else:
        print("| Package | Covered | Lines | % |\n|---|---:|---:|---:|")

    failures: list[str] = []
    raisable: list[str] = []
    total = covered = 0
    for name in sorted(set(packages) | set(baseline)):
        if name not in packages:
            print(f"| {name} | | | | {baseline[name]:.1f} | | ❌ no coverage data |")
            failures.append(f"{name}: no coverage data (baseline {baseline[name]:.1f}%)")
            continue
        package_covered, package_total = packages[name]
        total += package_total
        covered += package_covered
        value = percent(package_covered, package_total)
        row = f"| {name} | {package_covered} | {package_total} | {value:.1f} |"
        if baseline:
            if name not in baseline:
                row += " | | new, no baseline |"
            else:
                expected = baseline[name]
                change = value - expected
                status = ""
                if value < expected - tolerance:
                    status = "❌ below baseline"
                    failures.append(f"{name}: {value:.1f}% is below the baseline of {expected:.1f}%")
                elif change >= RAISE_HINT:
                    status = "⬆️ baseline can be raised"
                    raisable.append(name)
                row += f" {expected:.1f} | {change:+.1f} | {status} |"
        print(row)
    total_row = f"| **Total** | **{covered}** | **{total}** | **{percent(covered, total):.1f}** |"
    print(total_row + (" | | |" if baseline else ""))

    if args.write_baseline:
        candidate = {
            "tolerance": tolerance,
            "packages": {name: floor1(percent(c, t)) for name, (c, t) in sorted(packages.items())},
        }
        args.write_baseline.parent.mkdir(parents=True, exist_ok=True)
        args.write_baseline.write_text(json.dumps(candidate, indent=2) + "\n", encoding="utf-8")

    if raisable:
        print(f"\nAbove the baseline by {RAISE_HINT:g} point or more: "
              f"{', '.join(raisable)}. The `coverage` artifact has `baseline-candidate.json`; copying it over "
              "`.github/coverage-baseline.json` raises the baseline.")
    if failures:
        print("\n### Coverage fell below the baseline\n")
        for failure in failures:
            print(f"- {failure}")
        print("\nAdd tests for the uncovered code, or, if the drop is intended (for example code moved to a package "
              "that is tested elsewhere), lower the value in `.github/coverage-baseline.json` in the same pull request "
              "and say why.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
