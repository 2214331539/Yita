#!/bin/bash
set -euo pipefail

app="${1:?Usage: Verify-Mac-Package.sh /absolute/path/Yita.app}"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { printf 'Apple Silicon macOS is required.\n' >&2; exit 1; }
[[ "$app" == /* && -d "$app/Contents" ]] || { printf 'An absolute app bundle path is required.\n' >&2; exit 1; }
main="$app/Contents/MacOS/Yita.Desktop"
helper_app="$app/Contents/Helpers/Yita.Native.Mac.Helper.app"
helper="$helper_app/Contents/MacOS/Yita.Native.Mac.Helper"
plutil -lint "$app/Contents/Info.plist" "$helper_app/Contents/Info.plist"
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$app/Contents/Info.plist")" == com.yita.desktop ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$app/Contents/Info.plist")" == Yita.Desktop ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$app/Contents/Info.plist")" == 12.0 ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$helper_app/Contents/Info.plist")" == com.yita.desktop.native-helper ]]
[[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$app/Contents/Info.plist")" == "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleVersion' "$helper_app/Contents/Info.plist")" ]]
[[ -x "$main" && -x "$helper" ]]
[[ -s "$app/Contents/Resources/Yita.icns" ]]
for required in LICENSE NOTICE.md LICENSE-SourceSans.md LICENSES/InstantTranslate-MIT.txt dependency-inventory.json; do
  [[ -s "$app/Contents/Resources/Licenses/$required" ]]
done
for required in libhostfxr.dylib libhostpolicy.dylib libcoreclr.dylib libAvaloniaNative.dylib libSkiaSharp.dylib libHarfBuzzSharp.dylib Yita.Desktop.runtimeconfig.json; do
  [[ -s "$app/Contents/MacOS/$required" ]]
done
native_count=0
while IFS= read -r -d '' binary; do
  description="$(file -b "$binary")"
  [[ "$description" == *Mach-O* ]] || continue
  [[ "$(lipo -archs "$binary")" == arm64 ]] || { printf 'Unexpected architecture: %s\n' "$binary" >&2; exit 1; }
  codesign --verify --strict "$binary"
  native_count=$((native_count + 1))
done < <(find "$app" -type f -print0)
[[ "$native_count" -ge 6 ]]
codesign --verify --deep --strict "$app"
# The apphost must load its own runtime even when no global .NET installation is available.
env -u YITA_MAC_HELPER_PATH DOTNET_ROOT=/nonexistent/yita-test-runtime DOTNET_MULTILEVEL_LOOKUP=0 "$main" --package-check
"$helper" --selection-self-test
"$helper" --clipboard-self-test
"$helper" --input-self-test
printf 'Verified Apple Silicon bundle (%s native binaries). No external desktop acceptance claimed.\n' "$native_count"
