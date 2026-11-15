#!/bin/bash
set -euo pipefail

app="${1:?App bundle required}"
dmg="${2:?Output DMG path required}"
volume="${3:-窗外}"
codesign --verify --deep --strict "$app"
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
ditto "$app" "$stage/${app##*/}"
ln -s /Applications "$stage/Applications"
hdiutil create -volname "$volume" -srcfolder "$stage" -format UDZO "$dmg"
hdiutil verify "$dmg"
