#!/usr/bin/env python3
"""Render action-cued walkthrough narration with the local Kokoro model."""

from __future__ import annotations

from dataclasses import dataclass
import json
import os
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[2]
VIDEO_DIR = Path(__file__).resolve().parent
SCRIPT_PATH = VIDEO_DIR / "walkthrough-script.md"
BUILD_DIR = VIDEO_DIR / "build"
AUDIO_DIR = BUILD_DIR / "audio"
TEXT_DIR = BUILD_DIR / "narration"
MODEL_PATH = Path(
    os.environ.get(
        "VAULTSYNC_NARRATION_MODEL",
        BUILD_DIR / "models" / "kokoro-v1.0.int8.onnx",
    )
)
VOICES_PATH = Path(
    os.environ.get(
        "VAULTSYNC_NARRATION_VOICES",
        BUILD_DIR / "models" / "voices-v1.0.bin",
    )
)
VOICE = os.environ.get("VAULTSYNC_NARRATION_VOICE", "af_heart")
SPEED = float(os.environ.get("VAULTSYNC_NARRATION_SPEED", "1.0"))


@dataclass(frozen=True)
class Cue:
    start_seconds: float
    text: str


@dataclass(frozen=True)
class Scene:
    scene_id: str
    title: str
    cues: list[Cue]


def cue_time(value: str) -> float:
    minutes, seconds = value.split(":", maxsplit=1)
    return int(minutes) * 60 + float(seconds)


def parse_cue(lines: list[str], index: int) -> tuple[Cue, int]:
    line = lines[index][2:]
    marker_end = line.find("**", 2)
    if not line.startswith("**") or marker_end < 2:
        raise ValueError(f"Invalid narration cue: {lines[index]}")
    start = cue_time(line[2:marker_end])
    separator = line.find("—", marker_end)
    if separator < 0:
        raise ValueError(f"Narration cue needs an em dash: {lines[index]}")
    parts = [line[separator + 1 :].strip()]
    index += 1
    while index < len(lines):
        continuation = lines[index]
        if continuation.startswith(("- **", "## ")):
            break
        if continuation.startswith("  "):
            parts.append(continuation.strip())
            index += 1
            continue
        if not continuation.strip():
            index += 1
        break
    return Cue(start, " ".join(parts)), index


def markdown_sections(markdown: str) -> list[tuple[str, str, list[str]]]:
    sections: list[tuple[str, str, list[str]]] = []
    scene_id = ""
    title = ""
    body: list[str] = []
    for line in markdown.splitlines():
        if not line.startswith("## "):
            if scene_id:
                body.append(line)
            continue
        if scene_id:
            sections.append((scene_id, title, body))
        heading = line[3:].split(" — ", maxsplit=1)
        scene_id, title = heading if len(heading) == 2 else ("", "")
        body = []
    if scene_id:
        sections.append((scene_id, title, body))
    return sections


def section_cues(lines: list[str]) -> list[Cue]:
    try:
        index = lines.index("**Narration cues:**") + 1
    except ValueError:
        return []
    cues: list[Cue] = []
    while index < len(lines):
        if lines[index].startswith("- **"):
            cue, index = parse_cue(lines, index)
            cues.append(cue)
            continue
        index += 1
    return cues


def parse_scenes(markdown: str) -> list[Scene]:
    parsed: list[Scene] = []
    for scene_id, title, body in markdown_sections(markdown):
        cues = section_cues(body)
        if cues:
            parsed.append(Scene(scene_id, title, cues))
    return parsed


def capture_duration(path: Path) -> float:
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
    return float(result.stdout.strip())


def srt_timestamp(seconds: float) -> str:
    milliseconds = round(seconds * 1_000)
    hours, milliseconds = divmod(milliseconds, 3_600_000)
    minutes, milliseconds = divmod(milliseconds, 60_000)
    whole_seconds, milliseconds = divmod(milliseconds, 1_000)
    return f"{hours:02}:{minutes:02}:{whole_seconds:02},{milliseconds:03}"


def write_subtitles(path: Path, cues: list[tuple[Cue, float]]) -> None:
    lines: list[str] = []
    for index, (cue, duration) in enumerate(cues, start=1):
        lines.extend(
            [
                str(index),
                f"{srt_timestamp(cue.start_seconds)} --> "
                f"{srt_timestamp(cue.start_seconds + duration)}",
                cue.text,
                "",
            ]
        )
    path.write_text("\n".join(lines), encoding="utf-8")


def render_scene(engine, scene: Scene, soundfile, numpy) -> dict[str, object]:
    capture_path = BUILD_DIR / "capture" / f"{scene.scene_id}.mov"
    if not capture_path.exists():
        raise FileNotFoundError(f"Missing scene recording: {capture_path}")
    duration = capture_duration(capture_path)
    rendered: list[tuple[Cue, object, int]] = []
    sample_rate = 0
    for cue in scene.cues:
        samples, cue_rate = engine.create(
            cue.text,
            voice=VOICE,
            speed=SPEED,
            lang="en-us",
        )
        if sample_rate and sample_rate != cue_rate:
            raise ValueError("Kokoro returned inconsistent sample rates")
        sample_rate = cue_rate
        rendered.append((cue, numpy.asarray(samples, dtype=numpy.float32), cue_rate))

    timeline = numpy.zeros(round(duration * sample_rate), dtype=numpy.float32)
    subtitle_cues: list[tuple[Cue, float]] = []
    for index, (cue, samples, _) in enumerate(rendered):
        start = round(cue.start_seconds * sample_rate)
        end = start + len(samples)
        next_start = (
            round(rendered[index + 1][0].start_seconds * sample_rate)
            if index + 1 < len(rendered)
            else len(timeline)
        )
        if end + round(0.15 * sample_rate) > next_start:
            available = (next_start - start) / sample_rate
            actual = len(samples) / sample_rate
            raise ValueError(
                f"Scene {scene.scene_id} cue at {cue.start_seconds:.1f}s is "
                f"{actual:.2f}s long but only {available:.2f}s is available"
            )
        timeline[start:end] = samples
        subtitle_cues.append((cue, len(samples) / sample_rate))

    audio_path = AUDIO_DIR / f"{scene.scene_id}.wav"
    subtitle_path = AUDIO_DIR / f"{scene.scene_id}.srt"
    transcript_path = TEXT_DIR / f"{scene.scene_id}.txt"
    soundfile.write(audio_path, timeline, sample_rate, subtype="PCM_16")
    write_subtitles(subtitle_path, subtitle_cues)
    transcript_path.write_text(
        "\n\n".join(cue.text for cue in scene.cues) + "\n",
        encoding="utf-8",
    )
    return {
        "id": scene.scene_id,
        "title": scene.title,
        "audio": str(audio_path.relative_to(ROOT)),
        "subtitles": str(subtitle_path.relative_to(ROOT)),
        "capture": str(capture_path.relative_to(ROOT)),
        "duration": round(duration, 3),
        "narration": {"engine": "Kokoro ONNX", "voice": VOICE, "speed": SPEED},
    }


def load_dependencies():
    try:
        import numpy
        import soundfile
        from kokoro_onnx import Kokoro
    except ImportError:
        print(
            "Narration dependencies are missing. Install them with:\n"
            "  python3 -m pip install -r docs/video/requirements.txt",
            file=sys.stderr,
        )
        raise SystemExit(2)
    return Kokoro, soundfile, numpy


def main() -> int:
    if not MODEL_PATH.exists() or not VOICES_PATH.exists():
        print(
            "Kokoro model files are missing. Follow docs/video/README.md to "
            "download them into docs/video/build/models.",
            file=sys.stderr,
        )
        return 2
    parsed = parse_scenes(SCRIPT_PATH.read_text(encoding="utf-8"))
    if not parsed:
        print("No narrated scenes found.", file=sys.stderr)
        return 1

    kokoro_class, soundfile, numpy = load_dependencies()
    engine = kokoro_class(str(MODEL_PATH), str(VOICES_PATH))
    AUDIO_DIR.mkdir(parents=True, exist_ok=True)
    TEXT_DIR.mkdir(parents=True, exist_ok=True)
    manifest: list[dict[str, object]] = []
    for scene in parsed:
        print(f"Rendering scene {scene.scene_id}: {scene.title}", flush=True)
        manifest.append(render_scene(engine, scene, soundfile, numpy))

    manifest_path = BUILD_DIR / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Narration ready: {AUDIO_DIR}")
    print(f"Manifest ready: {manifest_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
