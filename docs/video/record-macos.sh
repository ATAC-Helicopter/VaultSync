#!/usr/bin/env bash
set -euo pipefail

video_dir="$(cd "$(dirname "$0")" && pwd)"
scene="${1:-}"
duration="${2:-}"
capture_rect="${VAULTSYNC_CAPTURE_RECT:-}"

if [[ ! "$scene" =~ ^[0-9]{2}$ ]] || [[ ! "$duration" =~ ^[0-9]+$ ]]; then
  echo "Usage: VAULTSYNC_CAPTURE_RECT=x,y,w,h $0 SCENE SECONDS" >&2
  echo "Example: VAULTSYNC_CAPTURE_RECT=260,120,1600,900 $0 03 75" >&2
  exit 2
fi

if [[ ! "$capture_rect" =~ ^[0-9]+,[0-9]+,[0-9]+,[0-9]+$ ]]; then
  echo "VAULTSYNC_CAPTURE_RECT must be x,y,width,height for the app-only region." >&2
  exit 2
fi

output_dir="$video_dir/build/capture"
output="$output_dir/$scene.mov"
mkdir -p "$output_dir"

echo "Recording scene $scene for $duration seconds."
echo "VaultSync must already be frontmost and unobstructed."
echo "The recording starts after the macOS countdown."
screencapture -x -v -k -V"$duration" -R"$capture_rect" "$output"
echo "Scene saved: $output"
