#!/usr/bin/env python3
"""Fail when restore or publish output resolves an unserviced .NET runtime."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
import tempfile
from pathlib import Path


MINIMUM_VERSION_PROPERTY = re.compile(
    r"<VaultSyncMinimumRuntimeVersion>\s*(\d+\.\d+\.\d+)\s*"
    r"</VaultSyncMinimumRuntimeVersion>"
)
REPOSITORY_ROOT = Path(__file__).resolve().parents[1]


def version_tuple(value: str) -> tuple[int, ...]:
    return tuple(int(part) for part in value.split("."))


def configured_minimum(repo_root: Path) -> str:
    content = (repo_root / "Directory.Build.props").read_text(encoding="utf-8-sig")
    match = MINIMUM_VERSION_PROPERTY.search(content)
    if match is None:
        raise ValueError("VaultSyncMinimumRuntimeVersion is not configured")
    return match.group(1)


def resolve_runtimeconfig(path_text: str, repo_root: Path) -> Path:
    """Resolve runtime metadata without allowing arbitrary filesystem reads."""
    candidate = Path(path_text).expanduser().resolve(strict=True)
    allowed_roots = [repo_root.resolve(strict=True), Path(tempfile.gettempdir()).resolve(strict=True)]
    runner_temp = os.environ.get("RUNNER_TEMP")
    if runner_temp:
        allowed_roots.append(Path(runner_temp).resolve(strict=True))
    if not any(candidate.is_relative_to(root) for root in allowed_roots):
        raise ValueError(f"runtimeconfig must stay inside the repository or runner temp: {path_text}")
    if not candidate.is_file() or not candidate.name.endswith(".runtimeconfig.json"):
        raise ValueError(f"runtimeconfig must be an existing .runtimeconfig.json file: {path_text}")
    return candidate


def audit_runtimeconfig(path: Path, minimum: str) -> list[str]:
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    frameworks = data.get("runtimeOptions", {}).get("includedFrameworks", [])
    netcore = next(
        (item for item in frameworks if item.get("name") == "Microsoft.NETCore.App"),
        None,
    )
    if netcore is None:
        return [f"{path}: self-contained Microsoft.NETCore.App metadata is missing"]
    version = netcore.get("version", "")
    if version_tuple(version) < version_tuple(minimum):
        return [f"{path}: embeds Microsoft.NETCore.App {version}; require >= {minimum}"]
    return []


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--runtimeconfig", action="append", required=True)
    args = parser.parse_args()

    minimum = configured_minimum(REPOSITORY_ROOT)
    errors: list[str] = []
    for path_text in args.runtimeconfig:
        path = resolve_runtimeconfig(path_text, REPOSITORY_ROOT)
        errors.extend(audit_runtimeconfig(path, minimum))

    if errors:
        print("Runtime security audit failed:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print(f"Runtime security audit passed (minimum {minimum}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
