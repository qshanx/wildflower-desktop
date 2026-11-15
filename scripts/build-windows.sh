#!/bin/bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
version="${1:?Usage: bash scripts/build-windows.sh VERSION [arm64|x64]}"
arch="${2:-arm64}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Expected a numeric release version." >&2; exit 2; }
case "$arch" in arm64|x64) ;; *) echo "Unsupported architecture: $arch" >&2; exit 2;; esac
command -v dotnet >/dev/null || { echo "Install the .NET 10 SDK first." >&2; exit 2; }
command -v vpk >/dev/null || { echo "Install vpk 1.2.161 and add it to PATH." >&2; exit 2; }

rid="win-$arch"
output="$root/artifacts/windows-$version/$rid"
if [[ -e "$output" ]]; then
  echo "Build output already exists: $output" >&2
  exit 2
fi
build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT
dotnet publish "$root/src/windows/WildflowerDesktop.csproj" -c Release -r "$rid" \
  --self-contained true -p:Version="$version" -o "$build/publish"
vpk '[win]' pack --packId WildflowerDesktop --packTitle "窗外" --icon "$root/src/windows/Assets/outside.ico" --packVersion "$version" \
  --packDir "$build/publish" --mainExe WildflowerDesktop.exe --runtime "$rid" \
  --channel "$rid" --framework webview2 --outputDir "$build/release"
test -s "$build/release/WildflowerDesktop-$rid-Setup.exe"
test -s "$build/release/releases.$rid.json"
mkdir -p "$(dirname "$output")"
mv "$build/release" "$output"
printf 'Local packages: %s\n' "$output"
