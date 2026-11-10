#!/usr/bin/env python3
"""Merges Cobertura files from several test projects into one Markdown table of line coverage per package.

A line counts as covered when any test project hit it. Usage: coverage_summary.py <directory>
"""
import pathlib
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict


def is_product(package: str) -> bool:
    return package.startswith("A2A.") and not package.endswith(".Tests")


def main(root: str) -> int:
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

    print("## Line coverage\n")
    if not lines:
        print(f"No coverage data found ({len(files)} files).")
        return 0
    print("| Package | Covered | Lines | % |\n|---|---:|---:|---:|")
    total = covered = 0
    for name in sorted(lines):
        package_total = len(lines[name])
        package_covered = sum(lines[name].values())
        total += package_total
        covered += package_covered
        print(f"| {name} | {package_covered} | {package_total} | {100 * package_covered / package_total:.1f} |")
    print(f"| **Total** | **{covered}** | **{total}** | **{100 * covered / total:.1f}** |")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
