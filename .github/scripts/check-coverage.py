#!/usr/bin/env python3
"""Checks the QP-4/QP-5 coverage gates against Cobertura reports produced by
`dotnet test --collect:"XPlat Code Coverage"`.

Usage: check-coverage.py <unit-coverage-glob> <unit-threshold> <integration-coverage-glob> <integration-threshold>

Exits non-zero (failing the CI job) if either threshold isn't met. When run inside GitHub Actions
(GITHUB_OUTPUT set), also writes unit_coverage/integration_coverage/passed outputs.
"""

import glob
import os
import sys
import xml.etree.ElementTree as ET


def line_rate_percent(pattern: str) -> float:
    files = glob.glob(pattern, recursive=True)
    if not files:
        print(f"::error::no coverage file found matching {pattern}")
        sys.exit(1)
    rate = float(ET.parse(files[0]).getroot().get("line-rate"))
    return rate * 100


def main() -> None:
    if len(sys.argv) != 5:
        print(f"usage: {sys.argv[0]} <unit-glob> <unit-threshold> <integration-glob> <integration-threshold>")
        sys.exit(2)

    unit_glob, unit_threshold, integration_glob, integration_threshold = sys.argv[1:]
    unit_threshold = float(unit_threshold)
    integration_threshold = float(integration_threshold)

    unit = line_rate_percent(unit_glob)
    integration = line_rate_percent(integration_glob)

    unit_ok = unit >= unit_threshold
    integration_ok = integration >= integration_threshold
    passed = unit_ok and integration_ok

    print(f"Unit coverage:        {unit:.2f}% ({'OK' if unit_ok else 'FAIL'}, >= {unit_threshold:.0f}% required)")
    print(
        f"Integration coverage: {integration:.2f}% "
        f"({'OK' if integration_ok else 'FAIL'}, >= {integration_threshold:.0f}% required)"
    )

    github_output = os.environ.get("GITHUB_OUTPUT")
    if github_output:
        with open(github_output, "a") as f:
            f.write(f"unit_coverage={unit:.2f}\n")
            f.write(f"integration_coverage={integration:.2f}\n")
            f.write(f"passed={'true' if passed else 'false'}\n")

    if not passed:
        sys.exit(1)


if __name__ == "__main__":
    main()
