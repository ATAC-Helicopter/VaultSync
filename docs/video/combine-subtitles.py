#!/usr/bin/env python3
"""Combine action-cued scene subtitles onto the blended video timeline."""

from __future__ import annotations

import json
from pathlib import Path
import sys


def milliseconds(value: str) -> int:
    hours, minutes, rest = value.split(":")
    seconds, millis = rest.split(",")
    return (
        int(hours) * 3_600_000
        + int(minutes) * 60_000
        + int(seconds) * 1_000
        + int(millis)
    )


def timestamp(value: int) -> str:
    hours, value = divmod(value, 3_600_000)
    minutes, value = divmod(value, 60_000)
    seconds, millis = divmod(value, 1_000)
    return f"{hours:02}:{minutes:02}:{seconds:02},{millis:03}"


def subtitle_blocks(content: str) -> list[tuple[int, int, str]]:
    blocks: list[tuple[int, int, str]] = []
    for block in content.strip().split("\n\n"):
        lines = block.splitlines()
        if len(lines) < 3 or " --> " not in lines[1]:
            continue
        start, end = lines[1].split(" --> ", maxsplit=1)
        blocks.append((milliseconds(start), milliseconds(end), "\n".join(lines[2:])))
    return blocks


def main() -> int:
    manifest_path = Path(sys.argv[1]).resolve()
    output_path = Path(sys.argv[2]).resolve()
    transition_ms = round(float(sys.argv[3]) * 1_000)
    root = manifest_path.parents[3]
    entries: list[tuple[int, int, str]] = [
        (0, 1_800, "This guide uses AI-generated narration.")
    ]
    offset = 0
    scenes = json.loads(manifest_path.read_text(encoding="utf-8"))
    for index, scene in enumerate(scenes):
        subtitle_path = root / scene["subtitles"]
        for start, end, text in subtitle_blocks(
            subtitle_path.read_text(encoding="utf-8")
        ):
            entries.append((offset + start, offset + end, text))
        offset += round(float(scene["duration"]) * 1_000)
        if index + 1 < len(scenes):
            offset -= transition_ms

    lines: list[str] = []
    for index, (start, end, text) in enumerate(entries, start=1):
        lines.extend([str(index), f"{timestamp(start)} --> {timestamp(end)}", text, ""])
    output_path.write_text("\n".join(lines), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
