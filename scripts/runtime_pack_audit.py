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
    if not isinstance(value, str) or re.fullmatch(r"\d+\.\d+\.\d+", value) is None:
        raise ValueError(f"Invalid stable runtime version: {value!r}")
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
    if not isinstance(frameworks, list) or not frameworks:
        return [f"{path}: self-contained Microsoft.NETCore.App metadata is missing"]
    errors: list[str] = []
    names: set[str] = set()
    for framework in frameworks:
        errors.extend(audit_framework(path, framework, minimum, names))
    if "Microsoft.NETCore.App" not in names:
        errors.append(f"{path}: self-contained Microsoft.NETCore.App metadata is missing")
    return errors


def audit_framework(path: Path, framework: object, minimum: str, names: set[str]) -> list[str]:
    if not isinstance(framework, dict):
        return [f"{path}: invalid included framework metadata"]
    name = framework.get("name")
    if not isinstance(name, str) or name not in {
        "Microsoft.NETCore.App", "Microsoft.AspNetCore.App", "Microsoft.WindowsDesktop.App"
    }:
        return [f"{path}: unknown included framework {name!r}; no servicing policy"]
    if name in names:
        return [f"{path}: duplicate included framework {name}"]
    names.add(name)
    version = framework.get("version", "")
    try:
        actual, required = version_tuple(version), version_tuple(minimum)
    except ValueError as error:
        return [f"{path}: {name}: {error}"]
    if actual[:2] != required[:2] or actual < required:
        return [f"{path}: embeds {name} {version}; require >= {minimum} on the same runtime line"]
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
