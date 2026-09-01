#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import zipfile


def is_within(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
    except ValueError:
        return False
    return path != root


def ensure_existing_workspace() -> Path:
    return Path.cwd().resolve(strict=True)


def resolve_workspace_path(raw_path: str, *, must_exist: bool = False, directory: bool = False) -> Path:
    workspace = ensure_existing_workspace()
    raw_candidate = Path(raw_path)
    lexical = (workspace / raw_candidate if not raw_candidate.is_absolute() else raw_candidate).resolve(strict=False)
    if not is_within(lexical, workspace):
        raise ValueError(f"Path must stay inside the workspace: {raw_path}")
    candidate = lexical.resolve(strict=False)
    if not is_within(candidate, workspace):
        raise ValueError(f"Resolved path must stay inside the workspace: {raw_path}")
    if must_exist and not candidate.exists():
        raise ValueError(f"Path does not exist: {raw_path}")
    if directory and candidate.exists() and not candidate.is_dir():
        raise ValueError(f"Path is not a directory: {raw_path}")
    return candidate


def ensure_output_path(path: Path, workspace: Path) -> Path:
    lexical = path.resolve(strict=False)
    if not is_within(lexical, workspace):
        raise ValueError(f"Output path must stay inside the workspace: {path}")
    resolved = lexical.resolve(strict=False)
    if not is_within(resolved, workspace):
        raise ValueError(f"Resolved output path must stay inside the workspace: {path}")
    return resolved


def ensure_child_path(path: Path, root: Path) -> Path:
    lexical = path.resolve(strict=False)
    if not is_within(lexical, root):
        raise ValueError(f"Patch input escapes base directory: {path}")
    resolved = lexical.resolve(strict=False)
    if not is_within(resolved, root):
        raise ValueError(f"Resolved patch input escapes base directory: {path}")
    return resolved


def ensure_output_file(path: Path, root: Path, *, suffix: str | None = None) -> Path:
    normalized_root = root.resolve(strict=False)
    safe_path = ensure_child_path(path, root)
    if suffix and safe_path.suffix.lower() != suffix:
        raise ValueError(f"Output file must use a {suffix} extension: {path}")
    safe_parent = safe_path.parent.resolve(strict=False)
    if safe_parent != normalized_root and not is_within(safe_parent, normalized_root):
        raise ValueError(f"Output parent path must stay inside the workspace: {path}")
    safe_parent.mkdir(parents=True, exist_ok=True)
    return safe_path


def sha256_file(path: Path, root: Path) -> str:
    path = ensure_child_path(path, root)
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def write_json_file(path: Path, root: Path, data: object) -> None:
    safe_path = ensure_output_file(path, root, suffix=".json")
    # ensure_output_file resolves the CLI path, keeps it inside root, validates the .json suffix,
    # and creates only a validated parent before this write.
    safe_path.write_text(json.dumps(data, indent=4), encoding="utf-8")  # NOSONAR


def load_manifest_paths(path: Path, workspace: Path) -> set[str]:
    safe_path = ensure_child_path(path, workspace)
    payload = json.loads(safe_path.read_text(encoding="utf-8-sig"))
    files = payload.get("files") if isinstance(payload, dict) else None
    if not isinstance(files, list) or not files:
        raise ValueError(f"Reference patch manifest has no file inventory: {path}")

    normalized: set[str] = set()
    for entry in files:
        raw_path = entry.get("path") if isinstance(entry, dict) else None
        if not isinstance(raw_path, str) or not raw_path.strip():
            raise ValueError(f"Reference patch manifest has an invalid file path: {path}")
        normalized.add(raw_path.replace("\\", "/").casefold())
    return normalized


def normalize_previous_versions(previous_versions: list[str]) -> list[str]:
    normalized: list[str] = []
    seen: set[str] = set()
    for previous in previous_versions:
        trimmed = previous.strip()
        if trimmed and trimmed not in seen:
            seen.add(trimmed)
            normalized.append(trimmed)
    if not normalized:
        raise ValueError("At least one --previous version is required.")
    return normalized


def qualify_previous_versions(
    previous_versions: list[str],
    base_manifests: dict[str, Path] | None,
    current_paths: set[str],
    workspace: Path,
    skip_incompatible_bases: bool,
) -> list[str]:
    qualified = [previous_versions[0]]
    references = {
        version.strip().casefold(): path
        for version, path in (base_manifests or {}).items()
        if version.strip()
    }
    for additional_base in previous_versions[1:]:
        reference = references.get(additional_base.casefold())
        if reference is None:
            raise ValueError(
                f"Additional base {additional_base} requires a reference patch manifest."
            )
        obsolete_paths = load_manifest_paths(reference, workspace) - current_paths
        if not obsolete_paths:
            qualified.append(additional_base)
            continue

        sample = ", ".join(sorted(obsolete_paths)[:3])
        message = (
            f"Additional base {additional_base} is not overlay-safe; "
            f"the target omits {len(obsolete_paths)} managed file(s): {sample}. "
            "Use the full installer for this base."
        )
        if not skip_incompatible_bases:
            raise ValueError(message)
        print(f"Skipping {message}")
    return qualified


def write_patch_archive(
    paths: list[Path], base_dir: Path, out_zip: Path, workspace: Path, platform: str
) -> list[dict[str, object]]:
    files: list[dict[str, object]] = []
    safe_out_zip = ensure_output_file(out_zip, workspace, suffix=".zip")
    with zipfile.ZipFile(safe_out_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for path in paths:
            relative = str(path.relative_to(base_dir))
            zf.write(path, relative.replace(os.sep, "/"))
            manifest_path = relative.replace("/", "\\") if platform == "windows" else relative.replace("\\", "/")
            files.append(
                {
                    "path": manifest_path,
                    "sha256": sha256_file(path, base_dir),
                    "size": path.stat().st_size,
                }
            )
    return files


def build_patch(
    base_dir: Path,
    out_zip: Path,
    out_manifest: Path,
    platform: str,
    previous_versions: list[str],
    target: str,
    base_manifests: dict[str, Path] | None = None,
    skip_incompatible_bases: bool = False,
) -> None:
    base_dir = base_dir.resolve()
    workspace = ensure_existing_workspace()
    if not base_dir.is_dir():
        raise ValueError(f"Base directory does not exist: {base_dir}")
    out_zip = ensure_output_path(out_zip, workspace)
    out_manifest = ensure_output_path(out_manifest, workspace)
    if out_zip == out_manifest:
        raise ValueError("Patch archive and manifest paths must be different.")
    if out_zip.suffix.lower() != ".zip":
        raise ValueError("Patch archive output must use a .zip extension.")
    if out_manifest.suffix.lower() != ".json":
        raise ValueError("Patch manifest output must use a .json extension.")

    normalized_previous = normalize_previous_versions(previous_versions)
    paths = [ensure_child_path(p, base_dir) for p in base_dir.rglob("*") if p.is_file()]
    paths.sort(key=lambda p: str(p.relative_to(base_dir)).lower())
    current_paths = {
        str(path.relative_to(base_dir)).replace("\\", "/").casefold()
        for path in paths
    }

    qualified_previous = qualify_previous_versions(
        normalized_previous,
        base_manifests,
        current_paths,
        workspace,
        skip_incompatible_bases,
    )
    files = write_patch_archive(paths, base_dir, out_zip, workspace, platform)
    safe_out_zip = ensure_output_file(out_zip, workspace, suffix=".zip")
    safe_out_manifest = ensure_output_file(out_manifest, workspace, suffix=".json")

    manifest = {
        "previousVersion": normalized_previous[0],
        "baseVersions": qualified_previous,
        "targetVersion": target,
        "archiveSha256": sha256_file(safe_out_zip, workspace),
        "archiveSize": safe_out_zip.stat().st_size,
        "files": files,
    }

    write_json_file(safe_out_manifest, workspace, manifest)


def main() -> None:
    parser = argparse.ArgumentParser(description="Build VaultSync patch archive + manifest.")
    parser.add_argument("--base-dir", required=True)
    parser.add_argument("--out-zip", required=True)
    parser.add_argument("--out-manifest", required=True)
    parser.add_argument("--platform", choices=["windows", "macos", "linux"], required=True)
    parser.add_argument("--previous", action="append", required=True)
    parser.add_argument(
        "--base-manifest",
        action="append",
        default=[],
        metavar="VERSION=PATH",
        help="Reference manifest proving an additional base has no obsolete managed files.",
    )
    parser.add_argument(
        "--skip-incompatible-bases",
        action="store_true",
        help="Omit additional bases that contain managed files absent from the target.",
    )
    parser.add_argument("--target", required=True)
    args = parser.parse_args()

    base_manifests: dict[str, Path] = {}
    for value in args.base_manifest:
        version, separator, raw_path = value.partition("=")
        if not separator or not version.strip() or not raw_path.strip():
            raise ValueError("--base-manifest must use VERSION=PATH.")
        base_manifests[version.strip()] = resolve_workspace_path(raw_path, must_exist=True)

    build_patch(
        resolve_workspace_path(args.base_dir, must_exist=True, directory=True),
        resolve_workspace_path(args.out_zip),
        resolve_workspace_path(args.out_manifest),
        args.platform,
        args.previous,
        args.target,
        base_manifests,
        args.skip_incompatible_bases,
    )


if __name__ == "__main__":
    main()
