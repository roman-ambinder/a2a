#!/usr/bin/env python3
"""Prints an a2a-itk run's results (itk/raw_results.json) and exits 1 unless every scenario passed.

The nightly path of a2a-itk's driver records failures as metrics and exits 0, so CI needs this check. A peer the
cluster dropped (it failed to build or start) is printed as a warning, and fails the check with --strict-peers.
Usage: python3 itk/check_results.py [path] [--strict-peers]
"""
import json
import sys


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    strict_peers = '--strict-peers' in sys.argv
    path = args[0] if args else 'itk/raw_results.json'
    try:
        with open(path, encoding='utf-8') as f:
            data = json.load(f)
    except (OSError, ValueError) as e:
        print(f'Cannot read ITK results from {path}: {e}')
        return 1

    results = data.get('results')
    if not isinstance(results, dict) or not results:
        print(f'No scenario results in {path}. Keys: {sorted(data)}')
        return 1

    failed = []
    for name, value in results.items():
        passed = isinstance(value, dict) and bool(value.get('passed'))
        print(f'{"PASSED" if passed else "FAILED"}  {name}')
        if not passed:
            failed.append(name)
            if isinstance(value, dict) and value.get('sdks'):
                print(f'        agents: {", ".join(value["sdks"])}')

    startup = data.get('startup') or {}
    if startup.get('cluster_error'):
        print(f'CLUSTER STARTUP FAILED: {startup["cluster_error"]}')
        return 1
    dropped = startup.get('dropped_peers') or {}
    for peer, reason in sorted(dropped.items()):
        print(f'WARNING: peer {peer} was dropped: {str(reason)[:300]}')

    print(f'{len(results) - len(failed)} of {len(results)} scenarios passed; {len(dropped)} peer(s) dropped.')
    if failed:
        return 1
    return 1 if strict_peers and dropped else 0


if __name__ == '__main__':
    sys.exit(main())
