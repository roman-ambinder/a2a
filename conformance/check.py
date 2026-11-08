"""Summarize conformance results and compare failures with the expected-failures list.

Usage: python3 -I conformance/check.py <results-dir> <expected-failures.txt>

Reads <results-dir>/tck/compatibility.json (a2a-tck) and <results-dir>/acts/acts-report-*.json (a2a-itk ACTS).
A failure is a TCK requirement with status FAIL on a binding, or an ACTS test with result fail or error.
Exit 1 when a failure is not in the list. Listed entries that now pass are reported, not failed.
Standard library only.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

# One spelling for bindings in the expected-failures list and in the summary.
BINDINGS = {"jsonrpc": "jsonrpc", "grpc": "grpc", "http_json": "rest", "rest": "rest"}


def load_expected(path: Path) -> dict[tuple[str, str, str], str]:
    """Lines: '<suite> <binding|*> <test-id>  # reason'."""
    expected: dict[tuple[str, str, str], str] = {}
    for raw in path.read_text(encoding="utf-8").splitlines():
        line, _, reason = raw.partition("#")
        fields = line.split()
        if not fields:
            continue
        if len(fields) != 3:
            raise SystemExit(f"bad line in {path}: {raw!r}")
        suite, binding, test = fields
        expected[(suite, binding, test)] = reason.strip()
    return expected


def tck_results(out: Path):
    report = out / "tck" / "compatibility.json"
    if not report.exists():
        return
    data = json.loads(report.read_text(encoding="utf-8"))
    for req_id, req in data.get("per_requirement", {}).items():
        for transport, status in req.get("transports", {}).items():
            yield "tck", BINDINGS.get(transport, transport), req_id, req.get("level", "?").lower(), status, "; ".join(req.get("errors", []))[:300]


def acts_results(out: Path):
    latest: dict[str, Path] = {}
    for report in sorted((out / "acts").glob("acts-report-*.json")):
        transport = json.loads(report.read_text(encoding="utf-8")).get("transport", report.name)
        latest[transport] = report  # file names end in a timestamp, so the last one wins
    for transport, report in sorted(latest.items()):
        data = json.loads(report.read_text(encoding="utf-8"))
        for suite in data.get("suites", []):
            for test in suite.get("tests", []):
                result = test.get("result", "?")
                status = {"pass": "PASS", "fail": "FAIL", "error": "FAIL", "skip": "SKIPPED"}.get(result, result.upper())
                message = (test.get("failure") or {}).get("message", "") if isinstance(test.get("failure"), dict) else str(test.get("failure", ""))
                yield "acts", BINDINGS.get(transport, transport), test.get("id", "?"), test.get("level", "?"), status, message[:300]


def main() -> int:
    out, expected_path = Path(sys.argv[1]), Path(sys.argv[2])
    expected = load_expected(expected_path)
    rows = list(tck_results(out)) + list(acts_results(out))
    if not rows:
        print("No results found under", out)
        return 2

    def listed(suite: str, binding: str, test: str) -> str | None:
        for key in ((suite, binding, test), (suite, "*", test)):
            if key in expected:
                return expected[key]
        return None

    counts: dict[tuple[str, str], dict[str, int]] = {}
    unexpected, known, fixed = [], [], []
    for suite, binding, test, level, status, message in rows:
        c = counts.setdefault((suite, binding), {"PASS": 0, "FAIL": 0, "SKIPPED": 0, "must_fail": 0})
        c[status if status in c else "SKIPPED"] += 1
        reason = listed(suite, binding, test)
        if status == "FAIL":
            if level == "must":
                c["must_fail"] += 1
            (known if reason is not None else unexpected).append((suite, binding, test, level, reason or message))
        elif status == "PASS" and reason is not None:
            fixed.append((suite, binding, test))

    print("\nCONFORMANCE SUMMARY")
    print(f"{'suite':6} {'binding':8} {'pass':>5} {'fail':>5} {'skip':>5} {'must-fail':>9}")
    for (suite, binding), c in sorted(counts.items()):
        print(f"{suite:6} {binding:8} {c['PASS']:5} {c['FAIL']:5} {c['SKIPPED']:5} {c['must_fail']:9}")

    if known:
        print(f"\nExpected failures ({len(known)}):")
        for suite, binding, test, level, reason in sorted(known):
            print(f"  {suite} {binding} {test} [{level}]: {reason}")
    if fixed:
        print(f"\nListed as expected failures but now passing ({len(fixed)}); remove them from {expected_path.name}:")
        for suite, binding, test in sorted(fixed):
            print(f"  {suite} {binding} {test}")
    if unexpected:
        print(f"\nUNEXPECTED FAILURES ({len(unexpected)}):")
        for suite, binding, test, level, message in sorted(unexpected):
            print(f"  {suite} {binding} {test} [{level}]: {message}")
        return 1
    print("\nNo unexpected failures.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
