#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_svg="$repo_root/docs/branding/vaultsync-logo-icon.svg"
lockup_svg="$repo_root/docs/branding/vaultsync-lockup-light.svg"
background="$repo_root/docs/branding/backgrounds/vaultsync-data-flow.png"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

for command_name in sips ffmpeg iconutil python3; do
  command -v "$command_name" >/dev/null 2>&1 || {
    echo "Missing required command: $command_name" >&2
    exit 1
  }
done

sips -s format png "$source_svg" --out "$work_dir/icon-512.png" >/dev/null
sips -s format png "$lockup_svg" --out "$work_dir/lockup.png" >/dev/null

resize_icon() {
  local size="$1"
  local output="$2"
  sips -z "$size" "$size" "$work_dir/icon-512.png" --out "$output" >/dev/null
}

resize_icon 512 "$repo_root/docs/branding/vaultsync-logo-icon-preview.png"
resize_icon 300 "$repo_root/docs/branding/vaultsync-reddit-icon.png"
resize_icon 64 "$repo_root/src/VaultSync.UI/Assets/vaultsync-tray.png"
resize_icon 44 "$repo_root/packaging/VaultSync.Store/Assets/Square44x44Logo.png"
resize_icon 50 "$repo_root/packaging/VaultSync.Store/Assets/StoreLogo.png"
resize_icon 150 "$repo_root/packaging/VaultSync.Store/Assets/Square150x150Logo.png"
resize_icon 310 "$repo_root/packaging/VaultSync.Store/Assets/Square310x310Logo.png"

ico_frames=()
for size in 16 20 24 32 40 48 64 256; do
  frame="$work_dir/icon-$size.png"
  resize_icon "$size" "$frame"
  ico_frames+=("$size:$frame")
done
python3 "$repo_root/scripts/build_ico.py" \
  "$repo_root/src/VaultSync.UI/Assets/vaultsync.ico" "${ico_frames[@]}"

iconset="$work_dir/VaultSync.iconset"
mkdir -p "$iconset"
for spec in "16 icon_16x16" "32 icon_16x16@2x" "32 icon_32x32" "64 icon_32x32@2x" \
            "128 icon_128x128" "256 icon_128x128@2x" "256 icon_256x256" \
            "512 icon_256x256@2x" "512 icon_512x512" "1024 icon_512x512@2x"; do
  read -r size name <<<"$spec"
  sips -z "$size" "$size" "$work_dir/icon-512.png" --out "$iconset/$name.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$repo_root/src/VaultSync.UI/Assets/VaultSync.icns"

ffmpeg -hide_banner -loglevel error -y -i "$background" -i "$work_dir/lockup.png" \
  -filter_complex "[0:v]scale=1080:445,crop=1080:128:(iw-ow)/2:(ih-oh)/2[bg];[1:v]scale=-1:92[logo];[bg][logo]overlay=(W-w)/2:(H-h)/2" \
  -frames:v 1 "$repo_root/docs/branding/vaultsync-reddit-banner.png"

ffmpeg -hide_banner -loglevel error -y -i "$background" -i "$work_dir/lockup.png" \
  -filter_complex "[0:v]scale=1280:640:force_original_aspect_ratio=increase,crop=1280:640[bg];[1:v]scale=820:-1[logo];[bg][logo]overlay=(W-w)/2:(H-h)/2" \
  -frames:v 1 "$repo_root/docs/branding/vaultsync-social-preview.png"

ffmpeg -hide_banner -loglevel error -y -i "$background" -i "$work_dir/lockup.png" \
  -filter_complex "[0:v]scale=620:300:force_original_aspect_ratio=increase,crop=620:300[bg];[1:v]scale=450:-1[logo];[bg][logo]overlay=(W-w)/2:(H-h)/2" \
  -frames:v 1 "$repo_root/packaging/VaultSync.Store/Assets/SplashScreen.png"

ffmpeg -hide_banner -loglevel error -y -i "$background" -i "$work_dir/lockup.png" \
  -filter_complex "[0:v]scale=310:150:force_original_aspect_ratio=increase,crop=310:150[bg];[1:v]scale=250:-1[logo];[bg][logo]overlay=(W-w)/2:(H-h)/2" \
  -frames:v 1 "$repo_root/packaging/VaultSync.Store/Assets/Wide310x150Logo.png"

ffmpeg -hide_banner -loglevel error -y -i "$background" -i "$work_dir/icon-512.png" \
  -filter_complex "[0:v]scale=1440:2160:force_original_aspect_ratio=increase,crop=1440:2160[bg];[1:v]scale=860:860[logo];[bg][logo]overlay=(W-w)/2:(H-h)/2" \
  -frames:v 1 "$repo_root/docs/branding/vaultsync-logo-icon-9x16-1440x2160.png"

echo "VaultSync brand assets regenerated."
