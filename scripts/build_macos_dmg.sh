#!/bin/bash
set -euo pipefail

version="${1:-}"
arch="${2:-}"
publish_dir="${3:-}"

if [[ -z "$version" || -z "$arch" || -z "$publish_dir" ]]; then
  echo "Usage: $0 <version> <arch> <publish_dir>"
  exit 1
fi

base_dist="dist/macos"
iconset="$base_dist/icons/VaultSync.iconset"
icns="$base_dist/VaultSync.icns"

if [[ ! -f "$icns" ]]; then
  iconutil -c icns "$iconset" -o "$icns"
fi

app_name="VaultSync-macos-$arch.app"
app_dir="$base_dist/$app_name"
contents="$app_dir/Contents"
macos_dir="$contents/MacOS"
resources_dir="$contents/Resources"

rm -rf "$app_dir"
mkdir -p "$macos_dir" "$resources_dir"
cp -R "$publish_dir"/* "$macos_dir"/
cp "$icns" "$resources_dir/VaultSync.icns"

cat > "$contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>VaultSync</string>
  <key>CFBundleDisplayName</key>
  <string>VaultSync</string>
  <key>CFBundleIdentifier</key>
  <string>com.vaultsync.app</string>
  <key>CFBundleVersion</key>
  <string>${version}</string>
  <key>CFBundleShortVersionString</key>
  <string>${version}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleExecutable</key>
  <string>VaultSync.UI</string>
  <key>CFBundleIconFile</key>
  <string>VaultSync</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
</dict>
</plist>
EOF

if [[ "$arch" == "arm64" ]]; then
  dmg_name="VaultSync-${version}-macos-apple-silicon.dmg"
else
  dmg_name="VaultSync-${version}-macos-intel.dmg"
fi

dmg="$base_dist/$dmg_name"
rm -f "$dmg"
hdiutil create -volname "VaultSync" -srcfolder "$app_dir" -ov -format UDZO "$dmg" > /dev/null

echo "$dmg"
