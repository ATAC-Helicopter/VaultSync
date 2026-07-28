#!/usr/bin/env python3
"""Combine per-scene Edge TTS subtitles onto one continuous timeline."""

from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path


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


def duration_ms(path: Path) -> int:
    result = subprocess.run(
        [
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "csv=p=0",
            str(path),
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    return round(float(result.stdout.strip()) * 1_000)


def main() -> int:
    manifest_path = Path(sys.argv[1]).resolve()
    output_path = Path(sys.argv[2]).resolve()
    root = manifest_path.parents[3]
    entries: list[tuple[int, int, str]] = [
        (0, 2_500, "This guide uses AI-generated narration.")
    ]
    offset = 0
    block_pattern = re.compile(
        r"\d+\s*\n"
        r"(?P<start>\d\d:\d\d:\d\d,\d\d\d) --> "
        r"(?P<end>\d\d:\d\d:\d\d,\d\d\d)\s*\n"
        r"(?P<text>.*?)(?=\n{2,}|\Z)",
        re.DOTALL,
    )

    for scene in json.loads(manifest_path.read_text(encoding="utf-8")):
        subtitle_path = root / scene["subtitles"]
        edited_scene_path = manifest_path.parent / "scenes" / f"{scene['id']}.mp4"
        for match in block_pattern.finditer(subtitle_path.read_text(encoding="utf-8")):
            entries.append(
                (
                    offset + milliseconds(match.group("start")),
                    offset + milliseconds(match.group("end")),
                    match.group("text").strip(),
                )
            )
        offset += duration_ms(edited_scene_path)

    lines = []
    for index, (start, end, text) in enumerate(entries, start=1):
        lines.extend(
            [str(index), f"{timestamp(start)} --> {timestamp(end)}", text, ""]
        )
    output_path.write_text("\n".join(lines), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
