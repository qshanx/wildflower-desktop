#!/bin/bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
theme="$root/themes/pond-garden"
app="${1:-$root/artifacts/macos/野花原.app}"
bundle_id="${2:-local.wildflower.meadow}"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>$bundle_id</string>
<key>CFBundleExecutable</key><string>Meadow</string>
<key>CFBundleName</key><string>野花原</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>10</string>
<key>LSUIElement</key><true/>
</dict></plist>
PLIST
xcrun swiftc -swift-version 5 -O -module-cache-path "${TMPDIR:-/tmp}/meadow-swift-cache" -framework AppKit -framework WebKit "$root/src/macos/Meadow.swift" -o "$app/Contents/MacOS/Meadow"
cp "$theme/flower-meadow.html" "$theme/pond-botanical.webp" "$theme/pond-botanical-atlas.webp" "$theme/pond-workbench.webp" "$app/Contents/Resources/"
# Synced Documents folders can add Finder metadata that codesign rejects.
if xattr -p com.apple.FinderInfo "$app" >/dev/null 2>&1; then
  xattr -d com.apple.FinderInfo "$app"
fi
codesign --force --sign - "$app"
