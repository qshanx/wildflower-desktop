#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
theme="$root/themes/pond-garden"
app="${1:-$root/artifacts/macos/窗外.app}"
bundle_id="${2:-local.wildflower.meadow}"
swift_flags=()
if [[ -n "${WILDFLOWER_SPARKLE_DIR:-}" ]]; then
  : "${WILDFLOWER_MAC_VERSION:?Set the Mac release version}"
  : "${WILDFLOWER_MAC_BUILD:?Set the increasing Mac build number}"
  : "${WILDFLOWER_SPARKLE_PUBLIC_KEY:?Set the Sparkle Ed25519 public key}"
  [[ "$WILDFLOWER_MAC_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
  [[ "$WILDFLOWER_MAC_BUILD" =~ ^[1-9][0-9]*$ ]]
  [[ "$WILDFLOWER_SPARKLE_PUBLIC_KEY" =~ ^[A-Za-z0-9+/]{43}=$ ]]
  test -d "$WILDFLOWER_SPARKLE_DIR/Sparkle.framework"
  test ! -e "$app"
  swift_flags=(-F "$WILDFLOWER_SPARKLE_DIR" -framework Sparkle -Xlinker -rpath -Xlinker @executable_path/../Frameworks)
fi
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>$bundle_id</string>
<key>CFBundleExecutable</key><string>Meadow</string>
<key>CFBundleName</key><string>窗外</string>
<key>CFBundleDisplayName</key><string>窗外</string>
<key>CFBundleIconFile</key><string>Outside.icns</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>10</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
plist="$app/Contents/Info.plist"
if [[ -n "${WILDFLOWER_MAC_VERSION:-}" || -n "${WILDFLOWER_MAC_BUILD:-}" ]]; then
  [[ "${WILDFLOWER_MAC_VERSION:-}" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]
  [[ "${WILDFLOWER_MAC_BUILD:-}" =~ ^[1-9][0-9]*$ ]]
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $WILDFLOWER_MAC_BUILD" \
    -c "Add :CFBundleShortVersionString string $WILDFLOWER_MAC_VERSION" "$plist"
fi
if [[ -n "${WILDFLOWER_SPARKLE_DIR:-}" ]]; then
  mkdir -p "$app/Contents/Frameworks"
  ditto "$WILDFLOWER_SPARKLE_DIR/Sparkle.framework" "$app/Contents/Frameworks/Sparkle.framework"
  /usr/libexec/PlistBuddy -c 'Add :SUFeedURL string https://github.com/qshanx/wildflower-desktop/releases/download/update-feed/mac-appcast.xml' \
    -c "Add :SUPublicEDKey string $WILDFLOWER_SPARKLE_PUBLIC_KEY" \
    -c 'Add :SUEnableAutomaticChecks bool true' \
    -c 'Add :SUAutomaticallyUpdate bool true' \
    -c 'Add :SUScheduledCheckInterval integer 21600' "$plist"
fi
xcrun swiftc -swift-version 5 -O -module-cache-path "${TMPDIR:-/tmp}/meadow-swift-cache" -framework AppKit -framework WebKit ${swift_flags[@]+"${swift_flags[@]}"} "$root/src/macos/Meadow.swift" -o "$app/Contents/MacOS/Meadow"
cp "$theme/flower-meadow.html" "$theme/pond-botanical.webp" "$theme/pond-botanical-atlas.webp" "$theme/pond-workbench.webp" "$app/Contents/Resources/"
(
  iconstage="$(mktemp -d)"
  trap 'rm -rf "$iconstage"' EXIT
  iconset="$iconstage/Outside.iconset"
  mkdir "$iconset"
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$root/assets/outside-icon.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
    sips -z "$((size * 2))" "$((size * 2))" "$root/assets/outside-icon.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$iconset" -o "$app/Contents/Resources/Outside.icns"
)
# Synced Documents folders can add Finder metadata that codesign rejects.
if xattr -p com.apple.FinderInfo "$app" >/dev/null 2>&1; then
  xattr -d com.apple.FinderInfo "$app"
fi
codesign --force --sign - "$app"
