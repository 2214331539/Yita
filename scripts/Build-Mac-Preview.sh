#!/bin/bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
output_root="${1:-$repo_root/artifacts/macos-preview}"
build_number="${2:-1}"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { printf 'Apple Silicon macOS is required to build this preview.\n' >&2; exit 1; }
[[ "$build_number" =~ ^[1-9][0-9]*$ ]] || { printf 'A positive numeric build number is required.\n' >&2; exit 1; }
for tool in dotnet pwsh xcrun codesign hdiutil iconutil sips plutil ditto; do command -v "$tool" >/dev/null; done
mkdir -p "$output_root"
output_root="$(cd "$output_root" && pwd -P)"
version="0.9.0-macos-preview.$build_number"
name="Yita-$version-osx-arm64"
for extension in dmg zip; do
  [[ ! -e "$output_root/$name.$extension" ]] || { printf 'Refusing to overwrite an existing preview package.\n' >&2; exit 1; }
done
work="$(mktemp -d "${TMPDIR:-/tmp}/yita-mac-package.XXXXXX")"
work="$(cd "$work" && pwd -P)"
mounted=""
cleanup() {
  if [[ -n "$mounted" ]]; then hdiutil detach "$mounted" -quiet || true; fi
  rm -rf "$work"
}
trap cleanup EXIT
app="$work/bundle/Yita.app"
runtime_root="$app/Contents/Resources/Runtime"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Helpers" "$runtime_root"
cd "$repo_root"
dotnet publish src/Yita.Desktop/Yita.Desktop.csproj -c Release -r osx-arm64 --self-contained true \
  -p:PublishTrimmed=false -p:PublishSingleFile=false -p:Version="$version" \
  -p:AssemblyVersion=0.9.0.0 -p:FileVersion="0.9.0.$build_number" \
  -p:MacBundleAppHostPath="$app/Contents/MacOS/Yita.Desktop" -o "$runtime_root"
rm "$runtime_root/Yita.Desktop"
bash scripts/Build-Mac-Helper.sh "$app/Contents/Helpers"
cp packaging/macos/Info.plist "$app/Contents/Info.plist"
helper_app="$app/Contents/Helpers/Yita.Native.Mac.Helper.app"
for bundle in "$app" "$helper_app"; do
  /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $build_number" "$bundle/Contents/Info.plist"
  /usr/libexec/PlistBuddy -c 'Set :CFBundleShortVersionString 0.9.0' "$bundle/Contents/Info.plist"
done

iconset="$work/Yita.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  double=$((size * 2))
  cp "assets/branding/yita/v1/yita-icon-$size.png" "$iconset/icon_${size}x${size}.png"
  cp "assets/branding/yita/v1/yita-icon-$double.png" "$iconset/icon_${size}x${size}@2x.png"
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Yita.icns"
pwsh -NoProfile -File scripts/Collect-DesktopLicenses.ps1 \
  -PublishDirectory "$runtime_root" -OutputDirectory "$app/Contents/Resources/Licenses"
cp docs/MAC_PREVIEW_TESTING.md "$app/Contents/Resources/TESTING.md"

manifest="$app/Contents/Resources/build-manifest.json"
runtime="$(plutil -extract runtimeOptions.includedFrameworks.0.version raw -o - "$runtime_root/Yita.Desktop.runtimeconfig.json")"
plutil -create xml1 "$manifest"
plutil -insert version -string "$version" "$manifest"
plutil -insert commit -string "$(git rev-parse HEAD)" "$manifest"
plutil -insert rid -string osx-arm64 "$manifest"
plutil -insert minimumMacOS -string 12.0 "$manifest"
plutil -insert runtime -string "$runtime" "$manifest"
plutil -insert sdk -string "$(dotnet --version)" "$manifest"
plutil -insert signing -string 'ad-hoc; no Developer ID; not notarized' "$manifest"
plutil -insert builtAtUtc -string "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$manifest"
plutil -convert json "$manifest"

# Thin vendor universal libraries before signing each executable, then nested bundles.
while IFS= read -r -d '' binary; do
  description="$(file -b "$binary")"
  [[ "$description" == *Mach-O* ]] || continue
  architectures="$(lipo -archs "$binary")"
  [[ " $architectures " == *' arm64 '* ]] || { printf 'Missing arm64 slice: %s\n' "$binary" >&2; exit 1; }
  if [[ "$architectures" != arm64 ]]; then
    lipo "$binary" -thin arm64 -output "$binary.arm64"
    mv "$binary.arm64" "$binary"
  fi
  chmod u+x "$binary"
  codesign --force --sign - "$binary"
done < <(find "$app" -type f -print0)
codesign --force --sign - --identifier com.yita.desktop.native-helper "$helper_app"
codesign --force --sign - --identifier com.yita.desktop "$app"
bash scripts/Verify-Mac-Package.sh "$app"
dotnet run --project tools/Yita.MacHelperSmoke/Yita.MacHelperSmoke.csproj -c Release --no-build -- \
  --helper "$helper_app/Contents/MacOS/Yita.Native.Mac.Helper"

stage="$work/dmg"
mkdir -p "$stage"
ditto "$app" "$stage/Yita.app"
ln -s /Applications "$stage/Applications"
cp docs/MAC_PREVIEW_TESTING.md "$stage/TESTING.md"
cp "$app/Contents/Resources/Yita.icns" "$stage/.VolumeIcon.icns"
xcrun SetFile -a C "$stage"
hdiutil create -volname 'Yita Preview' -srcfolder "$stage" -format UDZO -ov "$output_root/$name.dmg"
hdiutil verify "$output_root/$name.dmg"
mkdir -p "$work/mounted"
hdiutil attach "$output_root/$name.dmg" -readonly -nobrowse -mountpoint "$work/mounted" -quiet
mounted="$work/mounted"
[[ "$(readlink "$mounted/Applications")" == /Applications ]]
bash scripts/Verify-Mac-Package.sh "$mounted/Yita.app"
hdiutil detach "$mounted" -quiet
mounted=""
ditto -c -k --sequesterRsrc --keepParent "$app" "$output_root/$name.zip"
mkdir -p "$work/extracted"
ditto -x -k "$output_root/$name.zip" "$work/extracted"
bash scripts/Verify-Mac-Package.sh "$work/extracted/Yita.app"
cp "$manifest" "$output_root/$name.build.json"
cp docs/MAC_PREVIEW_TESTING.md "$output_root/TESTING.md"
cd "$output_root"
shasum -a 256 "$name.dmg" "$name.zip" "$name.build.json" TESTING.md > SHA256SUMS.txt
printf 'Created Apple Silicon internal test packages: %s\n' "$output_root/$name.dmg"
