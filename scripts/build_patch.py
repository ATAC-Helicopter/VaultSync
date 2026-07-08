#!/usr/bin/env python3
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
    normalized_root = root.resolve(strict=False)
    safe_path = path.resolve(strict=False)
    if safe_path.suffix.lower() != ".json":
        raise ValueError(f"Output file must use a .json extension: {path}")
    if safe_path == normalized_root or not safe_path.is_relative_to(normalized_root):
        raise ValueError(f"Output path must stay inside the workspace: {path}")
    safe_parent = safe_path.parent.resolve(strict=False)
    if safe_parent != normalized_root and not safe_parent.is_relative_to(normalized_root):
        raise ValueError(f"Output parent path must stay inside the workspace: {path}")
    safe_parent.mkdir(parents=True, exist_ok=True)
    safe_path.write_text(json.dumps(data, indent=4), encoding="utf-8")


def build_patch(base_dir: Path, out_zip: Path, out_manifest: Path, platform: str, previous_versions: list[str], target: str) -> None:
    files = []
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
    paths = [ensure_child_path(p, base_dir) for p in base_dir.rglob("*") if p.is_file()]
    paths.sort(key=lambda p: str(p.relative_to(base_dir)).lower())

    safe_out_zip = ensure_output_file(out_zip, workspace, suffix=".zip")
    _ = ensure_output_file(out_manifest, workspace, suffix=".json")
    with zipfile.ZipFile(safe_out_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
        for path in paths:
            rel = path.relative_to(base_dir)
            rel_str = str(rel)
            zip_path = rel_str.replace(os.sep, "/")
            zf.write(path, zip_path)

            manifest_path = rel_str
            if platform == "windows":
                manifest_path = manifest_path.replace("/", "\\")
            else:
                manifest_path = manifest_path.replace("\\", "/")

            files.append(
                {
                    "path": manifest_path,
                    "sha256": sha256_file(path, base_dir),
                    "size": path.stat().st_size,
                }
            )

    normalized_previous = []
    seen_previous = set()
    for previous in previous_versions:
        trimmed = previous.strip()
        if not trimmed or trimmed in seen_previous:
            continue
        seen_previous.add(trimmed)
        normalized_previous.append(trimmed)

    if not normalized_previous:
        raise ValueError("At least one --previous version is required.")

    manifest = {
        "previousVersion": normalized_previous[0],
        "baseVersions": normalized_previous,
        "targetVersion": target,
        "archiveSha256": sha256_file(safe_out_zip, workspace),
        "archiveSize": safe_out_zip.stat().st_size,
        "files": files,
    }

    write_json_file(out_manifest, workspace, manifest)


def main() -> None:
    parser = argparse.ArgumentParser(description="Build VaultSync patch archive + manifest.")
    parser.add_argument("--base-dir", required=True)
    parser.add_argument("--out-zip", required=True)
    parser.add_argument("--out-manifest", required=True)
    parser.add_argument("--platform", choices=["windows", "macos", "linux"], required=True)
    parser.add_argument("--previous", action="append", required=True)
    parser.add_argument("--target", required=True)
    args = parser.parse_args()

    build_patch(
        resolve_workspace_path(args.base_dir, must_exist=True, directory=True),
        resolve_workspace_path(args.out_zip),
        resolve_workspace_path(args.out_manifest),
        args.platform,
        args.previous,
        args.target,
    )


if __name__ == "__main__":
    main()
