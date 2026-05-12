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
package_dir="$(mktemp -d)"
tool_dir=""
appdir=""
trap 'rm -rf "$package_dir" ${tool_dir:+"$tool_dir"} ${appdir:+"$appdir"}' EXIT

cp -a "${publish_dir}/." "$package_dir/"
cat > "${package_dir}/install.sh" <<'EOF'
#!/bin/sh
set -eu

APP_NAME="VaultSync"
APP_ID="vaultsync"
SOURCE_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
INSTALL_ROOT="${XDG_DATA_HOME:-"$HOME/.local/share"}/${APP_ID}"
BIN_DIR="${HOME}/.local/bin"
APPLICATIONS_DIR="${XDG_DATA_HOME:-"$HOME/.local/share"}/applications"
ICON_DIR="${XDG_DATA_HOME:-"$HOME/.local/share"}/icons/hicolor/256x256/apps"
DESKTOP_FILE="${APPLICATIONS_DIR}/${APP_ID}.desktop"
ICON_SOURCE="${SOURCE_DIR}/Assets/vaultsync-tray.png"
ICON_TARGET="${ICON_DIR}/${APP_ID}.png"

mkdir -p "$INSTALL_ROOT" "$BIN_DIR" "$APPLICATIONS_DIR" "$ICON_DIR"
cp -a "${SOURCE_DIR}/." "$INSTALL_ROOT/"
chmod +x "${INSTALL_ROOT}/VaultSync.UI" 2>/dev/null || true
ln -sfn "${INSTALL_ROOT}/VaultSync.UI" "${BIN_DIR}/vaultsync"

if [ -f "$ICON_SOURCE" ]; then
  cp "$ICON_SOURCE" "$ICON_TARGET"
fi

cat > "$DESKTOP_FILE" <<DESKTOP
[Desktop Entry]
Type=Application
Name=${APP_NAME}
Comment=Backup and synchronization tool
Exec="${INSTALL_ROOT}/VaultSync.UI" %U
Icon=${ICON_TARGET}
Categories=Utility;Archiving;
Terminal=false
StartupWMClass=VaultSync
DESKTOP

chmod +x "$DESKTOP_FILE" 2>/dev/null || true
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "${XDG_DATA_HOME:-"$HOME/.local/share"}/icons/hicolor" >/dev/null 2>&1 || true
fi

cat <<MSG
${APP_NAME} installed for this user.

Run it from your app menu, or from a terminal with:
  vaultsync

If ~/.local/bin is not on PATH, run:
  ${INSTALL_ROOT}/VaultSync.UI
MSG
EOF
chmod +x "${package_dir}/install.sh"

cat > "${package_dir}/uninstall.sh" <<'EOF'
#!/bin/sh
set -eu

APP_ID="vaultsync"
INSTALL_ROOT="${XDG_DATA_HOME:-"$HOME/.local/share"}/${APP_ID}"
BIN_LINK="${HOME}/.local/bin/vaultsync"
APPLICATIONS_DIR="${XDG_DATA_HOME:-"$HOME/.local/share"}/applications"
DESKTOP_FILE="${APPLICATIONS_DIR}/${APP_ID}.desktop"
ICON_ROOT="${XDG_DATA_HOME:-"$HOME/.local/share"}/icons/hicolor"
ICON_TARGET="${ICON_ROOT}/256x256/apps/${APP_ID}.png"

rm -f "$BIN_LINK" "$DESKTOP_FILE" "$ICON_TARGET"
rm -rf "$INSTALL_ROOT"

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "$APPLICATIONS_DIR" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache "$ICON_ROOT" >/dev/null 2>&1 || true
fi

echo "VaultSync removed for this user."
EOF
chmod +x "${package_dir}/uninstall.sh"

tar -C "$package_dir" -czf "$tarball_path" .

if [[ "$arch" != "x64" ]]; then
  exit 0
fi

appimage_tool_url="https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"
tool_dir="$(mktemp -d)"
appdir="$(mktemp -d)"

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
