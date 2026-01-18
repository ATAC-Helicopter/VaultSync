#!/usr/bin/env python3
import argparse
import hashlib
import json
import os
from pathlib import Path
import zipfile


def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def build_patch(base_dir: Path, out_zip: Path, out_manifest: Path, platform: str, previous: str, target: str) -> None:
    files = []
    base_dir = base_dir.resolve()
    paths = [p for p in base_dir.rglob("*") if p.is_file()]
    paths.sort(key=lambda p: str(p.relative_to(base_dir)).lower())

    out_zip.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out_zip, "w", compression=zipfile.ZIP_DEFLATED) as zf:
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
                    "sha256": sha256_file(path),
                    "size": path.stat().st_size,
                }
            )

    manifest = {
        "previousVersion": previous,
        "targetVersion": target,
        "archiveSha256": sha256_file(out_zip),
        "archiveSize": out_zip.stat().st_size,
        "files": files,
    }

    out_manifest.write_text(json.dumps(manifest, indent=4), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description="Build VaultSync patch archive + manifest.")
    parser.add_argument("--base-dir", required=True)
    parser.add_argument("--out-zip", required=True)
    parser.add_argument("--out-manifest", required=True)
    parser.add_argument("--platform", choices=["windows", "macos", "linux"], required=True)
    parser.add_argument("--previous", required=True)
    parser.add_argument("--target", required=True)
    args = parser.parse_args()

    build_patch(
        Path(args.base_dir),
        Path(args.out_zip),
        Path(args.out_manifest),
        args.platform,
        args.previous,
        args.target,
    )


if __name__ == "__main__":
    main()
