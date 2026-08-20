#!/usr/bin/env python3
"""Validate and render public release consumers from one reviewable contract."""

from __future__ import annotations

import argparse
import json
import re
import sys
from datetime import date
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[1]
METADATA_PATH = REPO_ROOT / "release/release-metadata.json"
PUBLIC_METADATA_NAME = "release-metadata.json"
VERSION_RE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")


def resolve_within(root: Path, candidate: Path, purpose: str) -> Path:
    safe_root = root.resolve(strict=True)
    resolved = candidate.resolve()
    if not resolved.is_relative_to(safe_root):
        raise ValueError(f"{purpose} must stay inside {safe_root}.")
    return resolved


def require_object(data: dict[str, object], name: str) -> dict[str, object]:
    value = data.get(name)
    if not isinstance(value, dict):
        raise ValueError(f"{name} must be an object.")
    return value


def validate_active_release(active: dict[str, object]) -> str:
    version = active.get("version")
    previous = active.get("previousVersion")
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
    if active.get("compatiblePredecessors") != [previous]:
        raise ValueError("compatiblePredecessors must contain the one qualified previousVersion.")
    if active.get("releaseBranch") != f"release/{version}":
        raise ValueError("releaseBranch must match release/<version>.")
    if active.get("integrationBranch") != "Dev" or active.get("stableBranch") != "Stable":
        raise ValueError("The branch contract must retain Dev integration and Stable releases.")
    return version


def validate_dates(active: dict[str, object], stable: dict[str, object]) -> None:
    try:
        date.fromisoformat(str(active.get("targetDate")))
        date.fromisoformat(str(stable.get("releasedDate")))
    except ValueError as error:
        raise ValueError("Release dates must use YYYY-MM-DD.") from error


def load_metadata(path: Path, allowed_root: Path = REPO_ROOT) -> dict[str, object]:
    safe_path = resolve_within(allowed_root, path, "Metadata path")
    data = json.loads(safe_path.read_text(encoding="utf-8"))
    if data.get("schemaVersion") != 1:
        raise ValueError("Unsupported public release metadata schemaVersion.")

    repository = data.get("repository")
    active = require_object(data, "activeRelease")
    stable = require_object(data, "currentStable")
    store = require_object(data, "store")
    platforms = data.get("platforms")
    if not isinstance(repository, str) or not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
        raise ValueError("repository must be an owner/name GitHub identity.")
    if not isinstance(platforms, list) or not platforms or not all(isinstance(item, str) and item for item in platforms):
        raise ValueError("platforms must contain non-empty names.")

    version = validate_active_release(active)
    validate_dates(active, stable)
    if stable.get("tag") != f"v{stable.get('version')}":
        raise ValueError("currentStable.tag must match currentStable.version.")
    if store.get("packageVersion") != f"{version}.0":
        raise ValueError("store.packageVersion must be the four-part active version.")
    return data


def repo_file(root: Path, relative: str) -> Path:
    path = resolve_within(root, root / relative, "Repository consumer path")
    if not path.is_file():
        raise ValueError(f"Required repository consumer is missing: {relative}")
    return path


def _first_match(root: Path, relative: str, pattern: str) -> str | None:
    match = re.search(pattern, repo_file(root, relative).read_text(encoding="utf-8-sig"), re.MULTILINE)
    return match.group(1) if match else None


def validate_version_consumers(root: Path, metadata: dict[str, object], errors: list[str]) -> None:
    active = metadata["activeRelease"]
    store = metadata["store"]
    version = str(active["version"])
    expected = {
        "src/VaultSync.UI/VaultSync.UI.csproj": (r"<Version>([^<]+)</Version>", version),
        "src/VaultSync.CLI/VaultSync.CLI.csproj": (r"<Version>([^<]+)</Version>", version),
        "installer/VaultSyncInstaller.iss": (r'#define MyAppVersion "([^"]+)"', version),
        "packaging/VaultSync.Store/Package.appxmanifest": (r'Version="([^"]+)"', str(store["packageVersion"])),
        "CHANGELOG.md": (r"^## \[([^]]+)\] - (?:Unreleased|\d{2}\.\d{2}\.\d{4})", version),
        "docs/WHATS_NEW.md": (r"^## \[([^]]+)\]", version),
    }
    for relative, (pattern, wanted) in expected.items():
        actual = _first_match(root, relative, pattern)
        if actual != wanted:
            errors.append(f"{relative}: expected {wanted!r}, found {actual!r}")

    cli_text = repo_file(root, "src/VaultSync.CLI/VaultSync.CLI.csproj").read_text(encoding="utf-8-sig")
    for field, suffix in (("PackageVersion", ""), ("AssemblyVersion", ".0"), ("FileVersion", ".0"), ("AssemblyInformationalVersion", "")):
        expected_value = f"{version}{suffix}"
        if f"<{field}>{expected_value}</{field}>" not in cli_text:
            errors.append(f"CLI {field} must be {expected_value}.")


def validate_document_consumers(root: Path, metadata: dict[str, object], errors: list[str]) -> None:
    active = metadata["activeRelease"]
    version = str(active["version"])
    roadmap = repo_file(root, "ROADMAP.md").read_text(encoding="utf-8-sig")
    if f"## {version} —" not in roadmap:
        errors.append(f"ROADMAP.md has no {version} release section.")
    if f"**Stable target:** {active['targetDate']}" not in roadmap:
        errors.append("ROADMAP.md stable target does not match canonical metadata.")

    contract = repo_file(root, f"docs/RELEASE_{version}.md").read_text(encoding="utf-8-sig")
    for value in (version, str(active["targetDate"]), str(active["releaseBranch"])):
        if value not in contract:
            errors.append(f"Release contract is missing canonical value {value!r}.")

    updater = repo_file(root, "docs/UPDATER.md").read_text(encoding="utf-8-sig")
    if version not in updater or str(active["previousVersion"]) not in updater:
        errors.append("Updater documentation does not name the active and qualified predecessor versions.")



def validate_public_consumers(root: Path, metadata: dict[str, object], errors: list[str]) -> None:
    stable = metadata["currentStable"]
    public_path = repo_file(root, PUBLIC_METADATA_NAME)
    actual_public = json.loads(public_path.read_text(encoding="utf-8"))
    if actual_public != build_public_metadata(metadata):
        errors.append(f"{PUBLIC_METADATA_NAME} is stale.")

    index = repo_file(root, "index.html").read_text(encoding="utf-8-sig")
    if 'id="latest-release-version"' not in index or PUBLIC_METADATA_NAME not in index:
        errors.append("The website does not consume the public release metadata fallback.")
    if str(stable["version"]) not in json.dumps(build_public_metadata(metadata)):
        errors.append("Public metadata does not expose the current stable version.")


def validate_consumers(root: Path, metadata: dict[str, object]) -> list[str]:
    safe_root = root.resolve(strict=True)
    errors: list[str] = []
    validate_version_consumers(safe_root, metadata, errors)
    validate_document_consumers(safe_root, metadata, errors)
    validate_public_consumers(safe_root, metadata, errors)
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


def output_file(output_root: Path, name: str) -> Path:
    if Path(name).name != name:
        raise ValueError("Generated release output names cannot contain paths.")
    path = resolve_within(output_root, output_root / name, "Generated release output")
    path.parent.mkdir(parents=True, exist_ok=True)
    return path


def write_json(output_root: Path, name: str, value: dict[str, object]) -> None:
    path = output_file(output_root, name)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def render(metadata: dict[str, object], output_root: Path) -> None:
    output_root.mkdir(parents=True, exist_ok=True)
    write_json(output_root, PUBLIC_METADATA_NAME, build_public_metadata(metadata))
    write_json(output_root, "store-release-metadata.json", build_store_metadata(metadata))
    active = metadata["activeRelease"]
    summary = (
        f"# VaultSync {active['version']} release summary\n\n"
        f"- Channel: {active['channel']}\n"
        f"- Stage: {active['stage']}\n"
        f"- Tag: {active['tag']}\n"
        f"- Target date: {active['targetDate']}\n"
        f"- Qualified patch predecessor: {active['previousVersion']}\n"
    )
    output_file(output_root, "release-summary.md").write_text(summary, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("check", "render"))
    parser.add_argument("--metadata", type=Path, default=METADATA_PATH)
    parser.add_argument("--repo-root", type=Path, default=Path("."))
    parser.add_argument("--output-root", type=Path)
    args = parser.parse_args()

    try:
        repo_root = args.repo_root.resolve(strict=True)
        metadata_path = args.metadata if args.metadata.is_absolute() else repo_root / args.metadata
        metadata = load_metadata(metadata_path, repo_root)
        if args.command == "render":
            if args.output_root is None:
                raise ValueError("render requires --output-root.")
            output_root = resolve_within(repo_root, repo_root / args.output_root, "Output root")
            render(metadata, output_root)
            print(f"Rendered public, Store, and release-summary metadata to {output_root}.")
        else:
            errors = validate_consumers(repo_root, metadata)
            if errors:
                raise ValueError("Release metadata drift:\n- " + "\n- ".join(errors))
            print("Release metadata consumers match the canonical contract.")
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
