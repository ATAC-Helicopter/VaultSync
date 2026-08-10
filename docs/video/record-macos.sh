#!/usr/bin/env bash
set -euo pipefail

video_dir="$(cd "$(dirname "$0")" && pwd)"
scene="${1:-}"
duration="${2:-}"
capture_rect="${VAULTSYNC_CAPTURE_RECT:-}"

if ! command -v ffprobe >/dev/null 2>&1; then
  echo "ffprobe is required to validate capture framing." >&2
  exit 2
fi

if [[ ! "$scene" =~ ^[0-9]{2}$ ]] || [[ ! "$duration" =~ ^[0-9]+$ ]]; then
  echo "Usage: VAULTSYNC_CAPTURE_RECT=x,y,w,h $0 SCENE SECONDS" >&2
  echo "Example: VAULTSYNC_CAPTURE_RECT=260,120,1600,900 $0 03 75" >&2
  exit 2
fi

if [[ ! "$capture_rect" =~ ^[0-9]+,[0-9]+,[0-9]+,[0-9]+$ ]]; then
  echo "VAULTSYNC_CAPTURE_RECT must be x,y,width,height for the app-only region." >&2
  exit 2
fi

IFS=',' read -r capture_x capture_y capture_width capture_height <<< "$capture_rect"
if (( capture_width * 9 != capture_height * 16 )); then
  echo "Capture region must be exactly 16:9; received ${capture_width}x${capture_height}." >&2
  echo "Use an app-content rectangle such as 1600x900 or 1440x810." >&2
  exit 2
fi
if (( capture_width < 1280 || capture_height < 720 )); then
  echo "Capture region must be at least 1280x720 logical pixels." >&2
  exit 2
fi

output_dir="$video_dir/build/capture"
output="$output_dir/$scene.mov"
mkdir -p "$output_dir"
temporary_dir="$(mktemp -d "$output_dir/.recording-${scene}.XXXXXX")"
temporary_output="$temporary_dir/$scene.mov"
cleanup() {
  rm -rf "$temporary_dir"
}
trap cleanup EXIT

echo "Recording scene $scene for $duration seconds."
echo "VaultSync must already be frontmost and unobstructed."
echo "The recording starts after the macOS countdown."
screencapture -x -v -k -V"$duration" -R"$capture_rect" "$temporary_output"
recorded_size="$(ffprobe -v error -select_streams v:0 \
  -show_entries stream=width,height -of csv=s=x:p=0 "$temporary_output")"
recorded_width="${recorded_size%x*}"
recorded_height="${recorded_size#*x}"
if (( recorded_width * 9 != recorded_height * 16 )); then
  echo "Recorded clip is not 16:9 (${recorded_size}); discard this take." >&2
  exit 1
fi
mv -f "$temporary_output" "$output"
echo "Scene saved: $output"
