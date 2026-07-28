#!/usr/bin/env python3
"""Render the walkthrough narration with Microsoft Edge neural TTS.

No API key is required. The renderer intentionally uses the same voice family
as the published ProofRestore walkthrough.
"""

from __future__ import annotations

import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SCRIPT_PATH = Path(__file__).with_name("walkthrough-script.md")
BUILD_DIR = Path(__file__).with_name("build")
AUDIO_DIR = BUILD_DIR / "audio"
TEXT_DIR = BUILD_DIR / "narration"
VOICE = os.environ.get("VAULTSYNC_NARRATION_VOICE", "en-US-BrianNeural")
RATE = os.environ.get("VAULTSYNC_NARRATION_RATE", "-2%")
MAX_ATTEMPTS = 3


def scenes(markdown: str) -> list[tuple[str, str, str]]:
    section_pattern = re.compile(
        r"^## (?P<id>\d{2}) — (?P<title>.+?)\n(?P<body>.*?)(?=^## \d{2} — |\Z)",
        re.MULTILINE | re.DOTALL,
    )
    result: list[tuple[str, str, str]] = []
    for match in section_pattern.finditer(markdown):
        body = match.group("body")
        narration_match = re.search(
            r"^\*\*Narration:\*\*\s*\n(?P<narration>.*?)(?=^\*\*|\Z)",
            body,
            re.MULTILINE | re.DOTALL,
        )
        if not narration_match:
            continue
        quoted_lines = []
        for line in narration_match.group("narration").splitlines():
            if line.startswith("> "):
                quoted_lines.append(line[2:])
            elif line == ">":
                quoted_lines.append("")
        narration = re.sub(r"\s+", " ", " ".join(quoted_lines)).strip()
        if narration:
            result.append((match.group("id"), match.group("title"), narration))
    return result


def main() -> int:
    try:
        subprocess.run(
            [sys.executable, "-m", "edge_tts", "--version"],
            check=True,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )
    except (OSError, subprocess.CalledProcessError):
        print(
            "edge-tts is required. Install it with:\n"
            "  python3 -m pip install edge-tts\n"
            "It uses Microsoft Edge neural voices and does not need an API key.",
            file=sys.stderr,
        )
        return 2

    parsed = scenes(SCRIPT_PATH.read_text(encoding="utf-8"))
    if not parsed:
        print("No narrated scenes found.", file=sys.stderr)
        return 1

    AUDIO_DIR.mkdir(parents=True, exist_ok=True)
    TEXT_DIR.mkdir(parents=True, exist_ok=True)
    manifest: list[dict[str, str]] = []

    for scene_id, title, narration in parsed:
        text_path = TEXT_DIR / f"{scene_id}.txt"
        audio_path = AUDIO_DIR / f"{scene_id}.mp3"
        subtitle_path = AUDIO_DIR / f"{scene_id}.srt"
        narration_file = narration + "\n"
        narration_is_current = (
            text_path.exists()
            and text_path.read_text(encoding="utf-8") == narration_file
        )
        text_path.write_text(narration_file, encoding="utf-8")
        if (
            narration_is_current
            and
            audio_path.exists()
            and audio_path.stat().st_size > 0
            and subtitle_path.exists()
            and subtitle_path.stat().st_size > 0
        ):
            print(f"Keeping existing scene {scene_id}: {title}")
            manifest.append(
                {
                    "id": scene_id,
                    "title": title,
                    "audio": str(audio_path.relative_to(ROOT)),
                    "subtitles": str(subtitle_path.relative_to(ROOT)),
                    "capture": str(
                        (BUILD_DIR / "capture" / f"{scene_id}.mov").relative_to(ROOT)
                    ),
                }
            )
            continue

        print(f"Rendering scene {scene_id}: {title}")
        for attempt in range(1, MAX_ATTEMPTS + 1):
            audio_path.unlink(missing_ok=True)
            subtitle_path.unlink(missing_ok=True)
            try:
                subprocess.run(
                    [
                        sys.executable,
                        "-m",
                        "edge_tts",
                        "--voice",
                        VOICE,
                        f"--rate={RATE}",
                        "--file",
                        str(text_path),
                        "--write-media",
                        str(audio_path),
                        "--write-subtitles",
                        str(subtitle_path),
                    ],
                    check=True,
                    timeout=180,
                )
                break
            except (subprocess.CalledProcessError, subprocess.TimeoutExpired):
                if attempt == MAX_ATTEMPTS:
                    raise
                print(f"Scene {scene_id} failed; retrying ({attempt + 1}/{MAX_ATTEMPTS})")
                time.sleep(attempt * 2)
        manifest.append(
            {
                "id": scene_id,
                "title": title,
                "audio": str(audio_path.relative_to(ROOT)),
                "subtitles": str(subtitle_path.relative_to(ROOT)),
                "capture": str(
                    (BUILD_DIR / "capture" / f"{scene_id}.mov").relative_to(ROOT)
                ),
            }
        )

    manifest_path = BUILD_DIR / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Narration ready: {AUDIO_DIR}")
    print(f"Manifest ready: {manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
