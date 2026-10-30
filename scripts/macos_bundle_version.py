#!/usr/bin/env python3
"""Map VaultSync release versions to Apple's numeric bundle fields."""

from __future__ import annotations

import re
import sys


_VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-beta\.([1-9][0-9]*))?\Z")
_BETA_LIMIT = 998
_BUILD_STRIDE = 1000


def bundle_versions(version: str) -> tuple[str, str]:
    """Return short and build versions; stable builds sort after their betas."""
    match = _VERSION.fullmatch(version)
    if match is None:
        raise ValueError("Expected major.minor.patch or major.minor.patch-beta.N")

    major, minor, patch = (int(value) for value in match.group(1, 2, 3))
    beta = match.group(4)
    if beta is not None and int(beta) > _BETA_LIMIT:
        raise ValueError(f"Beta iteration must be at most {_BETA_LIMIT}")

    short_version = f"{major}.{minor}.{patch}"
    sequence = int(beta) if beta is not None else _BUILD_STRIDE - 1
    build_version = f"{major}.{minor}.{patch * _BUILD_STRIDE + sequence}"
    return short_version, build_version


if __name__ == "__main__":
    try:
        if len(sys.argv) != 2:
            raise ValueError("Usage: macos_bundle_version.py <version>")
        print(*bundle_versions(sys.argv[1]))
    except ValueError as error:
        print(error, file=sys.stderr)
        sys.exit(2)
