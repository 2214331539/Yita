#!/bin/bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:?Usage: Build-Mac-Helper.sh output-directory}"
helper_app="$output_root/Yita.Native.Mac.Helper.app"
helper_binary="$helper_app/Contents/MacOS/Yita.Native.Mac.Helper"
architecture="$(uname -m)"
sdk_path="$(xcrun --sdk macosx --show-sdk-path)"

mkdir -p "$helper_app/Contents/MacOS"
cp "$repo_root/src/Yita.Native.Mac.Helper/Info.plist" "$helper_app/Contents/Info.plist"
xcrun swiftc -swift-version 5 -O -warnings-as-errors -sdk "$sdk_path" \
  -target "$architecture-apple-macos12.0" \
  "$repo_root/src/Yita.Native.Mac.Helper/main.swift" -o "$helper_binary" \
  -framework AppKit -framework ApplicationServices
plutil -lint "$helper_app/Contents/Info.plist"
codesign --force --sign - --identifier com.yita.desktop.native-helper "$helper_app"
codesign --verify --strict "$helper_app"
printf 'Built development helper: %s\n' "$helper_app"
