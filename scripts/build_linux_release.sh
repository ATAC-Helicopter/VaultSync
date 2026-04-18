#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 3 ]]; then
  echo "Usage: $0 <version> <arch> <publish-dir>" >&2
  exit 1
fi

version="$1"
arch="$2"
publish_dir="$3"

if [[ "$arch" != "x64" && "$arch" != "arm64" ]]; then
  echo "Unsupported Linux arch: $arch" >&2
  exit 1
fi

if [[ ! -d "$publish_dir" ]]; then
  echo "Publish directory not found: $publish_dir" >&2
  exit 1
fi

dist_dir="dist/linux"
mkdir -p "$dist_dir"

base_name="VaultSync-${version}-linux-${arch}"
tarball_path="${dist_dir}/${base_name}.tar.gz"
tar -C "$publish_dir" -czf "$tarball_path" .

if [[ "$arch" != "x64" ]]; then
  exit 0
fi

appimage_tool_url="https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
tool_dir="$(mktemp -d)"
appdir="$(mktemp -d)"
trap 'rm -rf "$tool_dir" "$appdir"' EXIT

appimage_tool="${tool_dir}/appimagetool-x86_64.AppImage"
curl -fsSL "$appimage_tool_url" -o "$appimage_tool"
chmod +x "$appimage_tool"

mkdir -p \
  "${appdir}/usr/bin" \
  "${appdir}/usr/share/applications" \
  "${appdir}/usr/share/icons/hicolor/256x256/apps"

cp -a "${publish_dir}/." "${appdir}/usr/bin/"
cp "${publish_dir}/Assets/vaultsync-tray.png" "${appdir}/usr/share/icons/hicolor/256x256/apps/vaultsync.png"
cp "${publish_dir}/Assets/vaultsync-tray.png" "${appdir}/vaultsync.png"

cat > "${appdir}/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "${HERE}/usr/bin/VaultSync.UI" "$@"
EOF
chmod +x "${appdir}/AppRun"

cat > "${appdir}/vaultsync.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=VaultSync
Comment=Backup and synchronization tool
Exec=VaultSync.UI
Icon=vaultsync
Categories=Utility;Archiving;
Terminal=false
StartupWMClass=VaultSync
EOF

cp "${appdir}/vaultsync.desktop" "${appdir}/usr/share/applications/vaultsync.desktop"

appimage_path="${dist_dir}/${base_name}.AppImage"
APPIMAGE_EXTRACT_AND_RUN=1 ARCH=x86_64 "$appimage_tool" "$appdir" "$appimage_path"
