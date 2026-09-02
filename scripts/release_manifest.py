#!/usr/bin/env python3
"""Generate and validate VaultSync's canonical release artifact manifest."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from urllib.parse import quote, urlparse


SCHEMA_VERSION = 1
MANIFEST_NAME = "vaultsync-release-manifest.json"
VERSION_PATTERN = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$")
COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$", re.IGNORECASE)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def classify_asset(name: str) -> tuple[str, str, str]:
    lower = name.lower()
    patch = re.fullmatch(
        r"vaultsync-patch-(windows|macos-apple-silicon|macos-intel|linux-x64|linux-arm64)\.(json|zip)",
        lower,
    )
    if patch:
        target, extension = patch.groups()
        platform, architecture = {
            "windows": ("windows", "x64"),
            "macos-apple-silicon": ("macos", "arm64"),
            "macos-intel": ("macos", "x64"),
            "linux-x64": ("linux", "x64"),
            "linux-arm64": ("linux", "arm64"),
        }[target]
        kind = "patch-manifest" if extension == "json" else "patch-archive"
        return platform, architecture, kind

    if re.fullmatch(r"vaultsync-setup-.+\.exe", lower):
        return "windows", "x64", "installer"
    if re.fullmatch(r"vaultsync-store-.+-x64\.(msixupload|appxupload)", lower):
        return "windows", "x64", "store-upload"
    if re.fullmatch(r"vaultsync-.+-macos-apple-silicon\.dmg", lower):
        return "macos", "arm64", "disk-image"
    if re.fullmatch(r"vaultsync-.+-macos-intel\.dmg", lower):
        return "macos", "x64", "disk-image"

    linux = re.fullmatch(r"vaultsync-.+-linux-(x64|arm64)\.(tar\.gz|deb|appimage)", lower)
    if linux:
        architecture, extension = linux.groups()
        kind = {"tar.gz": "archive", "deb": "debian-package", "appimage": "appimage"}[extension]
        return "linux", architecture, kind

    raise ValueError(f"Unexpected release asset: {name}")


def expected_asset_keys(
    *,
    include_windows_patches: bool,
    include_macos_patches: bool,
    include_linux_patches: bool,
    include_store_upload: bool,
) -> set[tuple[str, str, str]]:
    expected = {
        ("windows", "x64", "installer"),
        ("macos", "arm64", "disk-image"),
        ("macos", "x64", "disk-image"),
        ("linux", "x64", "archive"),
        ("linux", "x64", "debian-package"),
        ("linux", "x64", "appimage"),
        ("linux", "arm64", "archive"),
        ("linux", "arm64", "debian-package"),
    }
    if include_windows_patches:
        expected.update(
            {
                ("windows", "x64", "patch-manifest"),
                ("windows", "x64", "patch-archive"),
            }
        )
    if include_macos_patches:
        expected.update(
            {
                ("macos", "arm64", "patch-manifest"),
                ("macos", "arm64", "patch-archive"),
                ("macos", "x64", "patch-manifest"),
                ("macos", "x64", "patch-archive"),
            }
        )
    if include_linux_patches:
        expected.update(
            {
                ("linux", "x64", "patch-manifest"),
                ("linux", "x64", "patch-archive"),
                ("linux", "arm64", "patch-manifest"),
                ("linux", "arm64", "patch-archive"),
            }
        )
    if include_store_upload:
        expected.add(("windows", "x64", "store-upload"))
    return expected


def collect_assets(root: Path) -> list[Path]:
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ValueError(f"Asset root is not a directory: {root}")

    assets: list[Path] = []
    names: set[str] = set()
    for candidate in sorted(root.rglob("*"), key=lambda path: path.name.lower()):
        if candidate.is_symlink():
            raise ValueError(f"Release assets cannot be symbolic links: {candidate}")
        if not candidate.is_file() or candidate.name == MANIFEST_NAME:
            continue
        key = candidate.name.casefold()
        if key in names:
            raise ValueError(f"Duplicate release asset name: {candidate.name}")
        names.add(key)
        classify_asset(candidate.name)
        assets.append(candidate)
    return assets


def build_manifest(
    asset_root: Path,
    *,
    version: str,
    channel: str,
    commit: str,
    repository: str,
    predecessors: list[str],
    include_windows_patches: bool = False,
    include_macos_patches: bool = False,
    include_linux_patches: bool = False,
    include_store_upload: bool = False,
) -> dict[str, object]:
    validate_release_identity(version, channel, commit, repository, predecessors)
    tag = f"v{version}"
    asset_entries: list[dict[str, object]] = []
    actual_keys: set[tuple[str, str, str]] = set()
    for path in collect_assets(asset_root):
        platform, architecture, package_kind = classify_asset(path.name)
        key = (platform, architecture, package_kind)
        if key in actual_keys:
            raise ValueError(f"Duplicate release asset role: {platform}/{architecture}/{package_kind}")
        actual_keys.add(key)
        asset_entries.append(
            {
                "name": path.name,
                "platform": platform,
                "architecture": architecture,
                "packageKind": package_kind,
                "sizeBytes": path.stat().st_size,
                "sha256": sha256_file(path),
                "downloadUrl": f"https://github.com/{repository}/releases/download/{tag}/{quote(path.name)}",
            }
        )

    expected = expected_asset_keys(
        include_windows_patches=include_windows_patches,
        include_macos_patches=include_macos_patches,
        include_linux_patches=include_linux_patches,
        include_store_upload=include_store_upload,
    )
    if actual_keys != expected:
        missing = sorted(expected - actual_keys)
        unexpected = sorted(actual_keys - expected)
        raise ValueError(f"Release asset matrix mismatch; missing={missing}, unexpected={unexpected}")

    manifest: dict[str, object] = {
        "schemaVersion": SCHEMA_VERSION,
        "release": {
            "version": version,
            "channel": channel,
            "tag": tag,
            "commit": commit.lower(),
            "repository": repository,
            "compatiblePredecessors": predecessors,
        },
        "assets": sorted(asset_entries, key=lambda asset: str(asset["name"]).lower()),
    }
    validate_manifest(manifest, asset_root=asset_root)
    return manifest


def validate_release_identity(
    version: str,
    channel: str,
    commit: str,
    repository: str,
    predecessors: list[str],
) -> None:
    if not VERSION_PATTERN.fullmatch(version):
        raise ValueError(f"Invalid release version: {version}")
    if channel not in {"stable", "beta"}:
        raise ValueError(f"Invalid release channel: {channel}")
    if (channel == "stable") != ("-" not in version):
        raise ValueError("Stable versions cannot have a suffix and beta versions must have one")
    if not COMMIT_PATTERN.fullmatch(commit):
        raise ValueError("Release commit must be a full 40-character Git SHA")
    if not REPOSITORY_PATTERN.fullmatch(repository):
        raise ValueError(f"Invalid GitHub repository: {repository}")
    if not predecessors or len(predecessors) != len(set(predecessors)):
        raise ValueError("Compatible predecessors must be a non-empty unique list")
    if version in predecessors or any(not VERSION_PATTERN.fullmatch(item) for item in predecessors):
        raise ValueError("Compatible predecessors must be valid versions different from the target")


def validate_manifest(manifest: object, *, asset_root: Path | None = None) -> None:
    if not isinstance(manifest, dict) or set(manifest) != {"schemaVersion", "release", "assets"}:
        raise ValueError("Manifest must contain only schemaVersion, release, and assets")
    if manifest["schemaVersion"] != SCHEMA_VERSION:
        raise ValueError(f"Unsupported release manifest schema: {manifest['schemaVersion']}")

    release = manifest["release"]
    if not isinstance(release, dict) or set(release) != {
        "version", "channel", "tag", "commit", "repository", "compatiblePredecessors"
    }:
        raise ValueError("Release identity fields do not match schema v1")
    validate_release_identity(
        str(release["version"]),
        str(release["channel"]),
        str(release["commit"]),
        str(release["repository"]),
        release["compatiblePredecessors"] if isinstance(release["compatiblePredecessors"], list) else [],
    )
    if release["tag"] != f"v{release['version']}":
        raise ValueError("Release tag must be v followed by the exact version")

    assets = manifest["assets"]
    if not isinstance(assets, list) or not assets:
        raise ValueError("Manifest must contain at least one release asset")
    names: set[str] = set()
    for asset in assets:
        validate_asset_entry(asset, release, names, asset_root)


def validate_published_assets(manifest: object, published_assets: object) -> None:
    validate_manifest(manifest)
    if not isinstance(manifest, dict) or not isinstance(published_assets, list):
        raise ValueError("Published asset comparison requires a manifest and GitHub asset array")

    expected = {asset["name"]: asset for asset in manifest["assets"]}
    actual = index_published_assets(published_assets)

    if set(actual) != set(expected):
        missing = sorted(set(expected) - set(actual))
        unexpected = sorted(set(actual) - set(expected))
        raise ValueError(f"Published release asset set differs from manifest; missing={missing}, unexpected={unexpected}")

    for name, expected_asset in expected.items():
        validate_published_asset(name, expected_asset, actual[name])


def index_published_assets(published_assets: list[object]) -> dict[str, dict[str, object]]:
    actual: dict[str, dict[str, object]] = {}
    for asset in published_assets:
        if not isinstance(asset, dict) or not isinstance(asset.get("name"), str):
            raise ValueError("GitHub release asset metadata is invalid")
        name = asset["name"]
        if name == MANIFEST_NAME:
            continue
        if name in actual:
            raise ValueError(f"GitHub release contains a duplicate asset name: {name}")
        actual[name] = asset
    return actual


def validate_published_asset(
    name: str,
    expected_asset: dict[str, object],
    actual_asset: dict[str, object],
) -> None:
    digest = str(actual_asset.get("digest") or "").removeprefix("sha256:")
    comparisons = (
        ("size", actual_asset.get("size"), expected_asset["sizeBytes"]),
        ("digest", digest, expected_asset["sha256"]),
        ("URL", actual_asset.get("url"), expected_asset["downloadUrl"]),
    )
    for label, actual_value, expected_value in comparisons:
        if actual_value != expected_value:
            raise ValueError(f"Published release asset {label} differs from manifest: {name}")


def validate_asset_entry(asset: object, release: dict[str, object], names: set[str], asset_root: Path | None) -> None:
    fields = {"name", "platform", "architecture", "packageKind", "sizeBytes", "sha256", "downloadUrl"}
    if not isinstance(asset, dict) or set(asset) != fields:
        raise ValueError("Release asset fields do not match schema v1")
    name = validate_asset_metadata(asset, release, names)

    if asset_root is not None:
        root = asset_root.resolve(strict=True)
        matches = [candidate for candidate in root.rglob(name) if candidate.is_file()]
        if len(matches) != 1:
            raise ValueError(f"Release asset must resolve exactly once beneath the asset root: {name}")
        path = matches[0].resolve(strict=True)
        if not path.is_relative_to(root) or path.is_symlink() or not path.is_file():
            raise ValueError(f"Release asset is outside the asset root: {name}")
        if path.stat().st_size != asset["sizeBytes"] or sha256_file(path) != asset["sha256"]:
            raise ValueError(f"Release asset bytes do not match the manifest: {name}")


def validate_asset_metadata(asset: dict[str, object], release: dict[str, object], names: set[str]) -> str:
    name = asset["name"]
    if not isinstance(name, str) or not name or Path(name).name != name:
        raise ValueError(f"Unsafe release asset name: {name}")
    if name == MANIFEST_NAME or name.casefold() in names:
        raise ValueError(f"Duplicate or self-referencing release asset: {name}")
    names.add(name.casefold())

    expected_role = classify_asset(name)
    if tuple(asset[field] for field in ("platform", "architecture", "packageKind")) != expected_role:
        raise ValueError(f"Release asset classification mismatch: {name}")
    if not isinstance(asset["sizeBytes"], int) or asset["sizeBytes"] <= 0:
        raise ValueError(f"Release asset size must be positive: {name}")
    if not isinstance(asset["sha256"], str) or not SHA256_PATTERN.fullmatch(asset["sha256"]):
        raise ValueError(f"Invalid SHA-256 digest: {name}")

    expected_url = f"https://github.com/{release['repository']}/releases/download/{release['tag']}/{quote(name)}"
    parsed_url = urlparse(str(asset["downloadUrl"]))
    if parsed_url.scheme != "https" or parsed_url.hostname != "github.com" or asset["downloadUrl"] != expected_url:
        raise ValueError(f"Unsafe or inconsistent release asset URL: {name}")
    return name


def write_manifest(asset_root: Path, manifest: dict[str, object]) -> Path:
    root = asset_root.resolve(strict=True)
    if not root.is_dir():
        raise ValueError(f"Asset root is not a directory: {root}")
    path = (root / MANIFEST_NAME).resolve(strict=False)
    if path.parent != root:
        raise ValueError("Release manifest target escaped the asset root")
    path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return path


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    generate = subparsers.add_parser("generate")
    generate.add_argument("--asset-root", type=Path, required=True)
    generate.add_argument("--output", type=Path, required=True)
    generate.add_argument("--version", required=True)
    generate.add_argument("--channel", choices=("stable", "beta"), required=True)
    generate.add_argument("--commit", required=True)
    generate.add_argument("--repository", default="ATAC-Helicopter/VaultSync")
    generate.add_argument("--previous", action="append", required=True)
    generate.add_argument("--include-windows-patches", action="store_true")
    generate.add_argument("--include-macos-patches", action="store_true")
    generate.add_argument("--include-linux-patches", action="store_true")
    generate.add_argument("--include-store-upload", action="store_true")
    validate = subparsers.add_parser("validate")
    validate.add_argument("--manifest", type=Path, required=True)
    validate.add_argument("--asset-root", type=Path)
    validate_published = subparsers.add_parser("validate-published")
    validate_published.add_argument("--manifest", type=Path, required=True)
    validate_published.add_argument("--github-assets", type=Path, required=True)
    args = parser.parse_args()

    try:
        if args.command == "generate":
            output = args.output.resolve(strict=False)
            asset_root = args.asset_root.resolve(strict=True)
            if output.name != MANIFEST_NAME or not output.is_relative_to(asset_root):
                raise ValueError(f"Output must be named {MANIFEST_NAME} inside the asset root")
            manifest = build_manifest(
                asset_root,
                version=args.version,
                channel=args.channel,
                commit=args.commit,
                repository=args.repository,
                predecessors=args.previous,
                include_windows_patches=args.include_windows_patches,
                include_macos_patches=args.include_macos_patches,
                include_linux_patches=args.include_linux_patches,
                include_store_upload=args.include_store_upload,
            )
            written_path = write_manifest(asset_root, manifest)
            print(f"Wrote {written_path} with {len(manifest['assets'])} assets.")
        elif args.command == "validate":
            manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
            validate_manifest(manifest, asset_root=args.asset_root)
            print(f"Validated {args.manifest}.")
        else:
            manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
            published_assets = json.loads(args.github_assets.read_text(encoding="utf-8-sig"))
            validate_published_assets(manifest, published_assets)
            print(f"Validated published assets against {args.manifest}.")
        return 0
    except (OSError, ValueError) as error:
        print(f"Release manifest error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
