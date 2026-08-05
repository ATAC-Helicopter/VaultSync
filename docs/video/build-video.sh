#!/usr/bin/env bash
set -euo pipefail

video_dir="$(cd "$(dirname "$0")" && pwd)"
repo_dir="$(cd "$video_dir/../.." && pwd)"
build_dir="$video_dir/build"
manifest="$build_dir/manifest.json"
output="$build_dir/vaultsync-guided-walkthrough.mp4"
transition_duration="0.35"

if ! command -v ffmpeg >/dev/null 2>&1 || ! command -v ffprobe >/dev/null 2>&1; then
  echo "ffmpeg and ffprobe are required." >&2
  exit 2
fi

if [[ ! -f "$manifest" ]]; then
  echo "Run python3 docs/video/render-narration.py first." >&2
  exit 2
fi

mkdir -p "$build_dir/scenes"
scene_paths=()
scene_durations=()
reference_size=""

while IFS=$'\t' read -r scene_id capture_rel audio_rel expected_duration; do
  capture="$repo_dir/$capture_rel"
  audio="$repo_dir/$audio_rel"
  scene="$build_dir/scenes/$scene_id.mp4"
  if [[ ! -f "$capture" || ! -f "$audio" ]]; then
    echo "Scene $scene_id is missing its recording or narration." >&2
    exit 2
  fi

  capture_duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$capture")"
  audio_duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$audio")"
  capture_size="$(ffprobe -v error -select_streams v:0 \
    -show_entries stream=width,height -of csv=s=x:p=0 "$capture")"
  capture_width="${capture_size%x*}"
  capture_height="${capture_size#*x}"
  if (( capture_width * 9 != capture_height * 16 )); then
    echo "Scene $scene_id is ${capture_size}, not 16:9. Re-record it; assembly will not crop it." >&2
    exit 2
  fi
  if [[ -z "$reference_size" ]]; then
    reference_size="$capture_size"
  elif [[ "$capture_size" != "$reference_size" ]]; then
    echo "Scene $scene_id is ${capture_size}; all scenes must match ${reference_size}." >&2
    exit 2
  fi
  if ! awk -v capture="$capture_duration" -v audio="$audio_duration" \
    'BEGIN { difference = capture - audio; if (difference < 0) difference = -difference; exit !(difference < 0.08) }'; then
    echo "Scene $scene_id narration does not match the capture duration." >&2
    echo "  capture: ${capture_duration}s; audio: ${audio_duration}s" >&2
    exit 2
  fi
  if ! awk -v actual="$capture_duration" -v expected="$expected_duration" \
    'BEGIN { difference = actual - expected; if (difference < 0) difference = -difference; exit !(difference < 0.08) }'; then
    echo "Scene $scene_id changed after narration was rendered. Render it again." >&2
    exit 2
  fi

  reuse_scene=false
  if [[ -f "$scene" && "$scene" -nt "$capture" && "$scene" -nt "$audio" ]]; then
    prepared_duration="$(ffprobe -v error -show_entries format=duration -of csv=p=0 "$scene")"
    if awk -v prepared="$prepared_duration" -v capture="$capture_duration" \
      'BEGIN { difference = prepared - capture; if (difference < 0) difference = -difference; exit !(difference < 0.08) }'; then
      reuse_scene=true
    fi
  fi

  if [[ "$reuse_scene" == true ]]; then
    echo "Keeping prepared scene $scene_id."
  else
    echo "Preparing scene $scene_id..."
    ffmpeg -hide_banner -loglevel warning -nostdin -y \
      -i "$capture" -i "$audio" \
      -filter_complex \
        "[0:v]scale=1920:1080:flags=lanczos,fps=30,\
format=yuv420p,settb=AVTB,setpts=PTS-STARTPTS[v];\
[1:a]loudnorm=I=-16:TP=-1.5:LRA=7,aresample=48000,\
aformat=sample_fmts=fltp:channel_layouts=stereo,asetpts=PTS-STARTPTS[a]" \
      -map "[v]" -map "[a]" -t "$capture_duration" \
      -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \
      -c:a aac -b:a 192k -ar 48000 -ac 2 -movflags +faststart "$scene"
  fi

  scene_paths+=("$scene")
  scene_durations+=("$capture_duration")
done < <(
  python3 - "$manifest" <<'PY'
import json
import sys

for item in json.load(open(sys.argv[1], encoding="utf-8")):
    print(
        item["id"],
        item["capture"],
        item["audio"],
        item["duration"],
        sep="\t",
    )
PY
)

if (( ${#scene_paths[@]} < 2 )); then
  echo "At least two scenes are required." >&2
  exit 2
fi

ffmpeg_inputs=()
for scene in "${scene_paths[@]}"; do
  ffmpeg_inputs+=(-i "$scene")
done

video_chain="[0:v][1:v]xfade=transition=fade:duration=${transition_duration}:offset="
audio_chain="[0:a][1:a]acrossfade=d=${transition_duration}:c1=tri:c2=tri[a1];"
cumulative="${scene_durations[0]}"
first_offset="$(awk -v total="$cumulative" -v transition="$transition_duration" \
  'BEGIN { printf "%.3f", total - transition }')"
video_chain+="${first_offset}[v1];"
cumulative="$(awk -v total="$cumulative" -v duration="${scene_durations[1]}" \
  -v transition="$transition_duration" 'BEGIN { printf "%.3f", total + duration - transition }')"

last_index=$((${#scene_paths[@]} - 1))
for (( index=2; index<=last_index; index++ )); do
  previous=$((index - 1))
  offset="$(awk -v total="$cumulative" -v transition="$transition_duration" \
    'BEGIN { printf "%.3f", total - transition }')"
  video_chain+="[v${previous}][${index}:v]xfade=transition=fade:duration=${transition_duration}:offset=${offset}[v${index}];"
  audio_chain+="[a${previous}][${index}:a]acrossfade=d=${transition_duration}:c1=tri:c2=tri[a${index}];"
  cumulative="$(awk -v total="$cumulative" -v duration="${scene_durations[$index]}" \
    -v transition="$transition_duration" 'BEGIN { printf "%.3f", total + duration - transition }')"
done

silent_subtitle_output="$build_dir/vaultsync-guided-walkthrough-no-captions.mp4"
reuse_blend=false
if [[ -f "$silent_subtitle_output" ]]; then
  reuse_blend=true
  for scene in "${scene_paths[@]}"; do
    if [[ ! "$silent_subtitle_output" -nt "$scene" ]]; then
      reuse_blend=false
      break
    fi
  done
fi

if [[ "$reuse_blend" == true ]]; then
  echo "Keeping prepared scene blend."
else
  echo "Blending scene boundaries..."
  ffmpeg -hide_banner -loglevel warning -nostdin -y \
    "${ffmpeg_inputs[@]}" \
    -filter_complex "${video_chain}${audio_chain}" \
    -map "[v${last_index}]" -map "[a${last_index}]" \
    -c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p \
    -c:a aac -b:a 192k -ar 48000 -ac 2 -movflags +faststart \
    "$silent_subtitle_output"
fi

python3 "$video_dir/combine-subtitles.py" \
  "$manifest" "$build_dir/captions.srt" "$transition_duration"
ffmpeg -hide_banner -loglevel warning -nostdin -y \
  -i "$silent_subtitle_output" -i "$build_dir/captions.srt" \
  -map 0:v -map 0:a -map 1:0 \
  -c:v copy -c:a copy -c:s mov_text \
  -metadata:s:s:0 language=eng -disposition:s:0 0 \
  -movflags +faststart "$output"

stream_durations="$(ffprobe -v error -show_entries stream=codec_type,duration \
  -of csv=p=0 "$output")"
video_duration="$(awk -F, '$1 == "video" { print $2; exit }' <<< "$stream_durations")"
audio_duration="$(awk -F, '$1 == "audio" { print $2; exit }' <<< "$stream_durations")"
if ! awk -v video="$video_duration" -v audio="$audio_duration" \
  'BEGIN { difference = video - audio; if (difference < 0) difference = -difference; exit !(difference < 0.15) }'; then
  echo "Final audio/video duration mismatch: video=${video_duration}s audio=${audio_duration}s" >&2
  exit 1
fi

echo "Video ready: $output"
echo "Caption sidecar: $build_dir/captions.srt"
