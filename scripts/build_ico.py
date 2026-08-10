#!/usr/bin/env python3
"""Assemble PNG icon frames into a Windows ICO without third-party packages."""

from __future__ import annotations

import struct
import sys
import tempfile
from pathlib import Path

REPOSITORY_ROOT = Path(__file__).resolve().parent.parent
TEMPORARY_ROOT = Path(tempfile.gettempdir()).resolve()


def resolve_restricted_path(
    path_text: str,
    *,
    allowed_roots: tuple[Path, ...],
    must_exist: bool,
) -> Path:
    """Resolve a CLI path and reject traversal or symlink escapes."""
    candidate = Path(path_text).expanduser().resolve(strict=must_exist)
    if not any(candidate.is_relative_to(root) for root in allowed_roots):
        roots = ", ".join(str(root) for root in allowed_roots)
        raise ValueError(f"path must stay within an allowed root ({roots}): {path_text}")
    return candidate


def main() -> int:
    if len(sys.argv) < 4:
        print("usage: build_ico.py OUTPUT.ico SIZE:IMAGE.png [SIZE:IMAGE.png ...]", file=sys.stderr)
        return 2

    output = resolve_restricted_path(
        sys.argv[1],
        allowed_roots=(REPOSITORY_ROOT,),
        must_exist=False,
    )
    if output.suffix.lower() != ".ico":
        raise ValueError(f"ICO output must use the .ico extension: {output}")
    if not output.parent.is_dir():
        raise ValueError(f"ICO output directory does not exist: {output.parent}")

    frames: list[tuple[int, bytes]] = []
    for value in sys.argv[2:]:
        size_text, path_text = value.split(":", 1)
        size = int(size_text)
        if size < 1 or size > 256:
            raise ValueError(f"invalid ICO frame size: {size}")
        frame_path = resolve_restricted_path(
            path_text,
            allowed_roots=(REPOSITORY_ROOT, TEMPORARY_ROOT),
            must_exist=True,
        )
        if frame_path.suffix.lower() != ".png" or not frame_path.is_file():
            raise ValueError(f"ICO frame must be a PNG file: {frame_path}")
        frames.append((size, frame_path.read_bytes()))

    header_size = 6 + (16 * len(frames))
    offset = header_size
    entries: list[bytes] = []
    payloads: list[bytes] = []
    for size, payload in frames:
        dimension = 0 if size == 256 else size
        entries.append(
            struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(payload), offset)
        )
        payloads.append(payload)
        offset += len(payload)

    output.write_bytes(struct.pack("<HHH", 0, 1, len(frames)) + b"".join(entries) + b"".join(payloads))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
