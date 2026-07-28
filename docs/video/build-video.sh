#!/usr/bin/env bash
set -euo pipefail

video_dir="$(cd "$(dirname "$0")" && pwd)"
repo_dir="$(cd "$video_dir/../.." && pwd)"
build_dir="$video_dir/build"
manifest="$build_dir/manifest.json"
output="$build_dir/vaultsync-guided-walkthrough.mp4"

if ! command -v ffmpeg >/dev/null 2>&1 || ! command -v ffprobe >/dev/null 2>&1; then
  echo "ffmpeg and ffprobe are required." >&2
  exit 2
fi

if [[ ! -f "$manifest" ]]; then
  echo "Run python3 docs/video/render-narration.py first." >&2
  exit 2
fi

mkdir -p "$build_dir/scenes"
concat_file="$build_dir/scenes.txt"
: > "$concat_file"
transition_duration="0.40"

while IFS=$'\t' read -r scene_id capture_rel audio_rel; do
  capture="$repo_dir/$capture_rel"
  audio="$repo_dir/$audio_rel"
  scene="$build_dir/scenes/$scene_id.mp4"

  if [[ ! -f "$capture" ]]; then
    echo "Missing scene recording: $capture" >&2
    exit 2
  fi

  audio_duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$audio")"
  capture_duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$capture")"
  scene_duration="$(awk -v audio="$audio_duration" -v tail="$transition_duration" 'BEGIN { printf "%.3f", audio + tail }')"
  minimum_capture="$(awk -v scene="$scene_duration" 'BEGIN { printf "%.3f", scene - 0.45 }')"
  if ! awk -v capture="$capture_duration" -v minimum="$minimum_capture" \
    'BEGIN { exit !(capture >= minimum) }'; then
    echo "Scene $scene_id is too short for its narration." >&2
    echo "  capture:   ${capture_duration}s" >&2
    echo "  required:  ${minimum_capture}s (long frozen holds are not allowed)" >&2
    exit 2
  fi
  fade_out_start="$(awk -v duration="$scene_duration" -v fade="$transition_duration" \
    'BEGIN { printf "%.3f", duration - fade }')"
  audio_fade_out_start="$(awk -v duration="$audio_duration" \
    'BEGIN { value = duration - 0.20; if (value < 0) value = 0; printf "%.3f", value }')"

  ffmpeg -hide_banner -loglevel warning -nostdin -y \
    -i "$capture" -i "$audio" \
    -filter_complex \
      "[0:v]split=2[background_source][foreground_source];\
[background_source]scale=1920:1080:force_original_aspect_ratio=increase,\
crop=1920:1080,gblur=sigma=28:steps=3,eq=brightness=-0.16[background];\
[foreground_source]scale=1920:1080:force_original_aspect_ratio=decrease[foreground];\
[background][foreground]overlay=(W-w)/2:(H-h)/2,\
tpad=stop_mode=clone:stop_duration=0.45,trim=duration=${scene_duration},\
fade=t=in:st=0:d=${transition_duration},\
fade=t=out:st=${fade_out_start}:d=${transition_duration},\
setpts=PTS-STARTPTS[v];\
[1:a]loudnorm=I=-16:TP=-1.5:LRA=11,\
afade=t=in:st=0:d=0.10,afade=t=out:st=${audio_fade_out_start}:d=0.20,\
apad=pad_dur=${scene_duration},atrim=duration=${scene_duration}[a]" \
    -map "[v]" -map "[a]" \
    -r 30 -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \
    -c:a aac -b:a 192k -ar 48000 -ac 2 -movflags +faststart "$scene"
  printf "file '%s'\n" "$scene" >> "$concat_file"
done < <(
  python3 - "$manifest" <<'PY'
import json
import sys
for item in json.load(open(sys.argv[1], encoding="utf-8")):
    print(item["id"], item["capture"], item["audio"], sep="\t")
PY
)

silent_subtitle_output="$build_dir/vaultsync-guided-walkthrough-no-captions.mp4"
ffmpeg -hide_banner -loglevel warning -nostdin -y \
  -f concat -safe 0 -i "$concat_file" \
  -c:v copy -c:a aac -b:a 192k -ar 48000 -ac 2 -movflags +faststart \
  "$silent_subtitle_output"

python3 "$video_dir/combine-subtitles.py" "$manifest" "$build_dir/captions.srt"
ffmpeg -hide_banner -loglevel warning -nostdin -y \
  -i "$silent_subtitle_output" -i "$build_dir/captions.srt" \
  -map 0:v -map 0:a -map 1:0 \
  -c:v copy -c:a copy -c:s mov_text \
  -metadata:s:s:0 language=eng -disposition:s:0 default \
  -movflags +faststart "$output"

echo "Video ready: $output"
echo "Caption sidecar: $build_dir/captions.srt"
