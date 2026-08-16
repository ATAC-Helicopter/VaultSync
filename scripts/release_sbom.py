#!/usr/bin/env python3
"""Generate and validate SPDX 2.3 SBOMs bound to canonical release assets."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
from pathlib import Path


SPDX_VERSION = "SPDX-2.3"
DATA_LICENSE = "CC0-1.0"
SELF_CONTAINED_KINDS = {"installer", "store-upload", "disk-image", "archive", "debian-package", "appimage"}
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")


def load_json(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as stream:
        value = json.load(stream)
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object: {path}")
    return value


def safe_id(value: str) -> str:
    return re.sub(r"[^A-Za-z0-9.-]", "-", value).strip("-") or "unknown"


def load_nuget_packages(assets_path: Path | None, runtime_identifier: str) -> list[tuple[str, str]]:
    if assets_path is None:
        return []
    if assets_path.is_dir():
        assets_path = assets_path / f"{runtime_identifier}.json"
    assets = load_json(assets_path)
    libraries = assets.get("libraries", {})
    target_keys: set[str] | None = None
    for target_name, target in assets.get("targets", {}).items():
        if target_name.endswith(f"/{runtime_identifier}") and isinstance(target, dict):
            target_keys = set(target)
            break
    packages: set[tuple[str, str]] = set()
    for key, value in libraries.items():
        if not isinstance(value, dict) or value.get("type") != "package" or "/" not in key:
            continue
        if target_keys is not None and key not in target_keys:
            continue
        name, version = key.rsplit("/", 1)
        packages.add((name, version))
    return sorted(packages, key=lambda item: (item[0].casefold(), item[1]))


def dependency_package(name: str, version: str) -> dict:
    identity = hashlib.sha256(f"{name}@{version}".encode()).hexdigest()[:16]
    return {
        "SPDXID": f"SPDXRef-NuGet-{identity}",
        "name": name,
        "versionInfo": version,
        "downloadLocation": f"https://www.nuget.org/packages/{name}/{version}",
        "filesAnalyzed": False,
        "licenseConcluded": "NOASSERTION",
        "licenseDeclared": "NOASSERTION",
        "copyrightText": "NOASSERTION",
        "externalRefs": [{
            "referenceCategory": "PACKAGE-MANAGER",
            "referenceType": "purl",
            "referenceLocator": f"pkg:nuget/{name}@{version}",
        }],
    }


def artifact_package(asset: dict, version: str) -> dict:
    return {
        "SPDXID": "SPDXRef-ReleaseArtifact",
        "name": asset["name"],
        "versionInfo": version,
        "downloadLocation": asset["downloadUrl"],
        "filesAnalyzed": False,
        "packageFileName": asset["name"],
        "primaryPackagePurpose": "APPLICATION",
        "checksums": [{"algorithm": "SHA256", "checksumValue": asset["sha256"]}],
        "licenseConcluded": "NOASSERTION",
        "licenseDeclared": "NOASSERTION",
        "copyrightText": "Copyright (c) 2025-2026 Flavio Giacchetti",
        "externalRefs": [{
            "referenceCategory": "PACKAGE-MANAGER",
            "referenceType": "purl",
            "referenceLocator": (
                f"pkg:generic/vaultsync@{version}?os={asset['platform']}&arch={asset['architecture']}"
                f"&packaging={asset['packageKind']}"
            ),
        }],
    }


def build_document(manifest: dict, asset: dict, dependencies: list[tuple[str, str]], created: str) -> dict:
    release = manifest["release"]
    artifact = artifact_package(asset, release["version"])
    packages = [artifact, *(dependency_package(name, version) for name, version in dependencies)]
    relationships = [{
        "spdxElementId": "SPDXRef-DOCUMENT",
        "relationshipType": "DESCRIBES",
        "relatedSpdxElement": artifact["SPDXID"],
    }]
    relationships.extend({
        "spdxElementId": artifact["SPDXID"],
        "relationshipType": "DEPENDS_ON",
        "relatedSpdxElement": package["SPDXID"],
    } for package in packages[1:])
    namespace_seed = f"{release['repository']}:{release['version']}:{asset['name']}:{asset['sha256']}"
    namespace_id = hashlib.sha256(namespace_seed.encode()).hexdigest()
    return {
        "spdxVersion": SPDX_VERSION,
        "dataLicense": DATA_LICENSE,
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": f"VaultSync {release['version']} - {asset['name']}",
        "documentNamespace": f"https://github.com/{release['repository']}/sbom/{namespace_id}",
        "creationInfo": {
            "created": created,
            "creators": ["Tool: VaultSync release_sbom.py", "Organization: VaultSync"],
        },
        "documentDescribes": [artifact["SPDXID"]],
        "packages": packages,
        "relationships": relationships,
        "annotations": [{
            "annotationDate": created,
            "annotationType": "OTHER",
            "annotator": "Tool: VaultSync release_sbom.py",
            "comment": (
                f"Canonical release manifest commit={release['commit']} channel={release['channel']} "
                f"platform={asset['platform']} architecture={asset['architecture']} "
                f"packageKind={asset['packageKind']}"
            ),
        }],
    }


def validate_document(document: dict, asset: dict | None = None) -> None:
    if document.get("spdxVersion") != SPDX_VERSION or document.get("dataLicense") != DATA_LICENSE:
        raise ValueError("SBOM must declare SPDX-2.3 and CC0-1.0")
    if document.get("SPDXID") != "SPDXRef-DOCUMENT":
        raise ValueError("SBOM document SPDXID is invalid")
    packages = document.get("packages")
    if not isinstance(packages, list) or not packages:
        raise ValueError("SBOM contains no packages")
    ids = [package.get("SPDXID") for package in packages if isinstance(package, dict)]
    if len(ids) != len(packages) or len(set(ids)) != len(ids):
        raise ValueError("SBOM package identifiers are missing or duplicated")
    known_ids = {"SPDXRef-DOCUMENT", *ids}
    for relationship in document.get("relationships", []):
        if relationship.get("spdxElementId") not in known_ids or relationship.get("relatedSpdxElement") not in known_ids:
            raise ValueError("SBOM relationship refers to an unknown SPDX identifier")
    artifact = next((package for package in packages if package.get("SPDXID") == "SPDXRef-ReleaseArtifact"), None)
    if artifact is None:
        raise ValueError("SBOM does not identify its release artifact")
    if asset is not None:
        checksums = artifact.get("checksums", [])
        expected = {"algorithm": "SHA256", "checksumValue": asset["sha256"]}
        if artifact.get("name") != asset["name"] or expected not in checksums:
            raise ValueError(f"SBOM is not bound to release asset {asset['name']}")


def generate(manifest_path: Path, output: Path, assets_path: Path | None, created: str) -> None:
    manifest = load_json(manifest_path)
    output.mkdir(parents=True, exist_ok=True)
    subjects: list[str] = []
    index: list[dict] = []
    for asset in manifest.get("assets", []):
        if asset.get("packageKind") not in SELF_CONTAINED_KINDS:
            continue
        runtime_identifier = {
            ("windows", "x64"): "win-x64",
            ("macos", "arm64"): "osx-arm64",
            ("macos", "x64"): "osx-x64",
            ("linux", "x64"): "linux-x64",
            ("linux", "arm64"): "linux-arm64",
        }[(asset["platform"], asset["architecture"])]
        dependencies = load_nuget_packages(assets_path, runtime_identifier)
        document = build_document(manifest, asset, dependencies, created)
        validate_document(document, asset)
        name = f"{asset['name']}.spdx.json"
        (output / name).write_text(json.dumps(document, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        subjects.append(f"{asset['sha256']} *{asset['name']}")
        index.append({"artifact": asset["name"], "sha256": asset["sha256"], "sbom": name})
    if not index:
        raise ValueError("Canonical manifest contains no self-contained release packages")
    (output / "vaultsync-release-subjects.sha256").write_text("\n".join(subjects) + "\n", encoding="utf-8")
    (output / "vaultsync-release-sbom-index.json").write_text(
        json.dumps({"schemaVersion": 1, "release": manifest["release"], "sboms": index}, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def validate(manifest_path: Path, sbom_root: Path) -> None:
    manifest = load_json(manifest_path)
    expected = {asset["name"]: asset for asset in manifest.get("assets", []) if asset.get("packageKind") in SELF_CONTAINED_KINDS}
    index = load_json(sbom_root / "vaultsync-release-sbom-index.json")
    indexed = {entry["artifact"]: entry for entry in index.get("sboms", [])}
    if set(indexed) != set(expected):
        raise ValueError("SBOM index does not exactly cover self-contained release assets")
    for name, asset in expected.items():
        document = load_json(sbom_root / indexed[name]["sbom"])
        validate_document(document, asset)
    checksum_lines = (sbom_root / "vaultsync-release-subjects.sha256").read_text(encoding="utf-8").splitlines()
    if len(checksum_lines) != len(expected) or any(not SHA256_PATTERN.match(line.split(" ", 1)[0]) for line in checksum_lines):
        raise ValueError("Release subject checksum file is invalid")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    sub = parser.add_subparsers(dest="command", required=True)
    generate_parser = sub.add_parser("generate")
    generate_parser.add_argument("--manifest", type=Path, required=True)
    generate_parser.add_argument("--output", type=Path, required=True)
    generate_parser.add_argument("--project-assets", type=Path)
    generate_parser.add_argument("--created", default=datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z"))
    validate_parser = sub.add_parser("validate")
    validate_parser.add_argument("--manifest", type=Path, required=True)
    validate_parser.add_argument("--sbom-root", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        if args.command == "generate":
            generate(args.manifest, args.output, args.project_assets, args.created)
        else:
            validate(args.manifest, args.sbom_root)
        return 0
    except (OSError, KeyError, TypeError, ValueError, json.JSONDecodeError) as error:
        print(f"release SBOM error: {error}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
