#!/usr/bin/env python3
"""Validate and render public release consumers from one reviewable contract."""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import date
from pathlib import Path


METADATA_PATH = Path("release/release-metadata.json")
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")


def load_metadata(path: Path) -> dict[str, object]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1:
        raise ValueError("Unsupported public release metadata schemaVersion.")

    repository = data.get("repository")
    active = data.get("activeRelease")
    stable = data.get("currentStable")
    store = data.get("store")
    platforms = data.get("platforms")
    if not isinstance(repository, str) or not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
        raise ValueError("repository must be an owner/name GitHub identity.")
    if not isinstance(active, dict) or not isinstance(stable, dict) or not isinstance(store, dict):
        raise ValueError("activeRelease, currentStable, and store objects are required.")
    if not isinstance(platforms, list) or not platforms or not all(isinstance(item, str) and item for item in platforms):
        raise ValueError("platforms must contain non-empty names.")

    version = active.get("version")
    previous = active.get("previousVersion")
    predecessors = active.get("compatiblePredecessors")
    if not isinstance(version, str) or not VERSION_RE.fullmatch(version):
        raise ValueError("activeRelease.version is invalid.")
    if active.get("tag") != f"v{version}":
        raise ValueError("activeRelease.tag must be v plus activeRelease.version.")
    if active.get("channel") not in {"stable", "beta"}:
        raise ValueError("activeRelease.channel must be stable or beta.")
    if active.get("stage") not in {"planned", "candidate", "released"}:
        raise ValueError("activeRelease.stage is invalid.")
    if not isinstance(previous, str) or not VERSION_RE.fullmatch(previous) or previous == version:
        raise ValueError("activeRelease.previousVersion must be a different version.")
    if predecessors != [previous]:
        raise ValueError("compatiblePredecessors must contain the one qualified previousVersion.")
    if active.get("releaseBranch") != f"release/{version}":
        raise ValueError("releaseBranch must match release/<version>.")
    if active.get("integrationBranch") != "Dev" or active.get("stableBranch") != "Stable":
        raise ValueError("The branch contract must retain Dev integration and Stable releases.")

    target_date = active.get("targetDate")
    released_date = stable.get("releasedDate")
    try:
        date.fromisoformat(str(target_date))
        date.fromisoformat(str(released_date))
    except ValueError as error:
        raise ValueError("Release dates must use YYYY-MM-DD.") from error
    if stable.get("tag") != f"v{stable.get('version')}":
        raise ValueError("currentStable.tag must match currentStable.version.")
    if store.get("packageVersion") != f"{version}.0":
        raise ValueError("store.packageVersion must be the four-part active version.")
    return data


def _first_match(path: Path, pattern: str) -> str | None:
    match = re.search(pattern, path.read_text(encoding="utf-8-sig"), re.MULTILINE)
    return match.group(1) if match else None


def validate_consumers(root: Path, metadata: dict[str, object]) -> list[str]:
    active = metadata["activeRelease"]
    stable = metadata["currentStable"]
    store = metadata["store"]
    version = str(active["version"])
    errors: list[str] = []

    expected = {
        "src/VaultSync.UI/VaultSync.UI.csproj": (r"<Version>([^<]+)</Version>", version),
        "src/VaultSync.CLI/VaultSync.CLI.csproj": (r"<Version>([^<]+)</Version>", version),
        "installer/VaultSyncInstaller.iss": (r'#define MyAppVersion "([^"]+)"', version),
        "packaging/VaultSync.Store/Package.appxmanifest": (r'Version="([^"]+)"', str(store["packageVersion"])),
        "CHANGELOG.md": (r"^## \[([^]]+)\] - (?:Unreleased|\d{2}\.\d{2}\.\d{4})", version),
        "docs/WHATS_NEW.md": (r"^## \[([^]]+)\]", version),
    }
    for relative, (pattern, wanted) in expected.items():
        actual = _first_match(root / relative, pattern)
        if actual != wanted:
            errors.append(f"{relative}: expected {wanted!r}, found {actual!r}")

    cli_text = (root / "src/VaultSync.CLI/VaultSync.CLI.csproj").read_text(encoding="utf-8-sig")
    for field, suffix in (("PackageVersion", ""), ("AssemblyVersion", ".0"), ("FileVersion", ".0"), ("AssemblyInformationalVersion", "")):
        expected_value = f"{version}{suffix}"
        if f"<{field}>{expected_value}</{field}>" not in cli_text:
            errors.append(f"CLI {field} must be {expected_value}.")

    roadmap = (root / "ROADMAP.md").read_text(encoding="utf-8-sig")
    if f"## {version} —" not in roadmap:
        errors.append(f"ROADMAP.md has no {version} release section.")
    if f"**Stable target:** {active['targetDate']}" not in roadmap:
        errors.append("ROADMAP.md stable target does not match canonical metadata.")

    contract = (root / f"docs/RELEASE_{version}.md").read_text(encoding="utf-8-sig")
    for value in (version, str(active["targetDate"]), str(active["releaseBranch"])):
        if value not in contract:
            errors.append(f"Release contract is missing canonical value {value!r}.")

    updater = (root / "docs/UPDATER.md").read_text(encoding="utf-8-sig")
    if version not in updater or str(active["previousVersion"]) not in updater:
        errors.append("Updater documentation does not name the active and qualified predecessor versions.")

    public_path = root / "release-metadata.json"
    if not public_path.exists():
        errors.append("release-metadata.json is missing; run the renderer.")
    else:
        actual_public = json.loads(public_path.read_text(encoding="utf-8"))
        if actual_public != build_public_metadata(metadata):
            errors.append("release-metadata.json is stale.")

    index = (root / "index.html").read_text(encoding="utf-8-sig")
    if 'id="latest-release-version"' not in index or "release-metadata.json" not in index:
        errors.append("The website does not consume the public release metadata fallback.")
    if str(stable["version"]) not in json.dumps(build_public_metadata(metadata)):
        errors.append("Public metadata does not expose the current stable version.")
    return errors


def build_public_metadata(metadata: dict[str, object]) -> dict[str, object]:
    active = metadata["activeRelease"]
    stable = metadata["currentStable"]
    return {
        "schemaVersion": 1,
        "repository": metadata["repository"],
        "latestStable": stable,
        "nextRelease": {
            "version": active["version"],
            "targetDate": active["targetDate"],
            "stage": active["stage"],
        },
        "platforms": metadata["platforms"],
    }


def build_store_metadata(metadata: dict[str, object]) -> dict[str, object]:
    active = metadata["activeRelease"]
    store = metadata["store"]
    return {
        "schemaVersion": 1,
        "productVersion": active["version"],
        "packageVersion": store["packageVersion"],
        "storeId": store["storeId"],
        "status": store["status"],
    }


def write_json(path: Path, value: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def render(metadata: dict[str, object], output_root: Path) -> None:
    write_json(output_root / "release-metadata.json", build_public_metadata(metadata))
    write_json(output_root / "store-release-metadata.json", build_store_metadata(metadata))
    active = metadata["activeRelease"]
    summary = (
        f"# VaultSync {active['version']} release summary\n\n"
        f"- Channel: {active['channel']}\n"
        f"- Stage: {active['stage']}\n"
        f"- Tag: {active['tag']}\n"
        f"- Target date: {active['targetDate']}\n"
        f"- Qualified patch predecessor: {active['previousVersion']}\n"
    )
    (output_root / "release-summary.md").write_text(summary, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "render"))
    parser.add_argument("--metadata", type=Path, default=METADATA_PATH)
    parser.add_argument("--repo-root", type=Path, default=Path("."))
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()

    try:
        metadata = load_metadata(args.metadata)
        if args.command == "render":
            if args.output_root is None:
                raise ValueError("render requires --output-root.")
            render(metadata, args.output_root)
            print(f"Rendered public, Store, and release-summary metadata to {args.output_root}.")
        else:
            errors = validate_consumers(args.repo_root, metadata)
            if errors:
                raise ValueError("Release metadata drift:\n- " + "\n- ".join(errors))
            print("Release metadata consumers match the canonical contract.")
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
