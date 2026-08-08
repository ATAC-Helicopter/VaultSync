#!/usr/bin/env python3
"""Assemble PNG icon frames into a Windows ICO without third-party packages."""

from __future__ import annotations

import struct
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) < 4:
        print("usage: build_ico.py OUTPUT.ico SIZE:IMAGE.png [SIZE:IMAGE.png ...]", file=sys.stderr)
        return 2

    output = Path(sys.argv[1])
    frames: list[tuple[int, bytes]] = []
    for value in sys.argv[2:]:
        size_text, path_text = value.split(":", 1)
        size = int(size_text)
        if size < 1 or size > 256:
            raise ValueError(f"invalid ICO frame size: {size}")
        frames.append((size, Path(path_text).read_bytes()))

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
