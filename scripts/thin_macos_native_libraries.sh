#!/bin/bash
set -euo pipefail

arch="${1:-}"
publish_dir="${2:-}"

case "$arch" in
  arm64) target_arch="arm64" ;;
  x64) target_arch="x86_64" ;;
  *)
    echo "Usage: $0 <arm64|x64> <publish_dir>" >&2
    exit 1
    ;;
esac

if [[ ! -d "$publish_dir" ]]; then
  echo "Publish directory does not exist: $publish_dir" >&2
  exit 1
fi

for name in libSkiaSharp.dylib libHarfBuzzSharp.dylib libAvaloniaNative.dylib; do
  library="$publish_dir/$name"
  [[ -f "$library" ]] || {
    echo "Required macOS native library is missing: $library" >&2
    exit 1
  }

  architectures="$(lipo -archs "$library")"
  if [[ " $architectures " != *" $target_arch "* ]]; then
    echo "$library does not contain required architecture $target_arch: $architectures" >&2
    exit 1
  fi

  if [[ "$architectures" == "$target_arch" ]]; then
    continue
  fi

  thinned="$library.thin"
  lipo "$library" -thin "$target_arch" -output "$thinned"
  chmod "$(stat -f '%Lp' "$library")" "$thinned"
  mv "$thinned" "$library"
  echo "Thinned $name to $target_arch"
done
