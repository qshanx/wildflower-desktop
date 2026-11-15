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
# A hosted runner may briefly hold the image after hdiutil create returns.
for attempt in 1 2 3 4 5; do
  if report="$(LC_ALL=C hdiutil verify "$dmg" 2>&1)"; then
    printf '%s\n' "$report"
    exit 0
  fi
  printf '%s\n' "$report" >&2
  [[ "$report" == *"Resource temporarily unavailable"* && "$attempt" != 5 ]] || exit 1
  sleep 2
done
