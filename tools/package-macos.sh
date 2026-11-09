#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
[[ "$(uname -s)" == Darwin && "$(uname -m)" == arm64 ]] || { echo 'Requires an Apple Silicon Mac.' >&2; exit 1; }
mkdir -p "$root/artifacts/package"
work="$(mktemp -d "$root/artifacts/package-build.XXXXXX")"
app="$root/artifacts/package/YuWangSync.app"
if [[ -e "$app" ]]; then mv "$app" "$root/artifacts/package/YuWangSync.previous.$(uuidgen).app"; fi
mkdir -p "$app/Contents/MacOS/Engine" "$app/Contents/Resources"
dotnet publish src/YuWangSync.Engine/YuWangSync.Engine.csproj -c Release -r osx-arm64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false \
  -p:RestoreLockedMode=true -o "$app/Contents/MacOS/Engine"
python3 tools/collect-licenses.py "$app/Contents/Resources/OpenSourceNotices.txt"
mkdir -p "$app/Contents/Resources/Symbols"
while IFS= read -r -d '' symbols; do mv "$symbols" "$app/Contents/Resources/Symbols/"; done < <(find "$app/Contents/MacOS/Engine" -type f -name '*.pdb' -print0)
if find "$app/Contents/MacOS/Engine" -name '*.dll' -print -quit | grep -q .; then
  echo 'Unexpected loose managed assembly in a macOS code directory.' >&2; exit 1
fi
xcrun swiftc -swift-version 5 -parse-as-library -O -target arm64-apple-macos14.0 \
  -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
  "$root"/native/app/*.swift -o "$app/Contents/MacOS/YuWangSync"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>io.github.TOBYmiz.YuWangSync-macOS</string>
<key>CFBundleName</key><string>YuWangSync</string>
<key>CFBundleDisplayName</key><string>育网云</string>
<key>CFBundleExecutable</key><string>YuWangSync</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleVersion</key><string>3</string>
<key>CFBundleShortVersionString</key><string>0.1.0</string>
<key>YuWangReleaseVersion</key><string>0.1.0-preview.3</string>
<key>CFBundleIconFile</key><string>app.icns</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSPrincipalClass</key><string>NSApplication</string>
</dict></plist>
PLIST
mkdir -p "$work/app.iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" native/app/Assets/app.png --out "$work/app.iconset/icon_${size}x${size}.png" >/dev/null
  retina=$((size * 2))
  sips -z "$retina" "$retina" native/app/Assets/app.png --out "$work/app.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$work/app.iconset" -o "$app/Contents/Resources/app.icns"
chmod +x "$app/Contents/MacOS/YuWangSync" "$app/Contents/MacOS/Engine/YuWangSync.Engine"
while IFS= read -r -d '' library; do bash "$root/tools/sign-macos.sh" "$library" "io.github.TOBYmiz.YuWangSync-macOS.$(basename "$library")"; done < <(find "$app/Contents/MacOS" -type f -name '*.dylib' -print0)
bash "$root/tools/sign-macos.sh" "$app/Contents/MacOS/Engine/YuWangSync.Engine" io.github.TOBYmiz.YuWangSync-macOS.engine
bash "$root/tools/sign-macos.sh" "$app/Contents/MacOS/YuWangSync" io.github.TOBYmiz.YuWangSync-macOS
bash "$root/tools/sign-macos.sh" "$app" io.github.TOBYmiz.YuWangSync-macOS
codesign --verify --deep --strict "$app"
plutil -lint "$app/Contents/Info.plist"
ditto -c -k --sequesterRsrc --keepParent "$app" "$root/artifacts/YuWangSync-macOS-arm64-preview.zip"
(cd "$root/artifacts" && shasum -a 256 YuWangSync-macOS-arm64-preview.zip > YuWangSync-macOS-arm64-preview.zip.sha256)
image_root="$work/dmg-root"
mkdir -p "$image_root"
ditto "$app" "$image_root/YuWangSync.app"
ln -s /Applications "$image_root/Applications"
cat > "$image_root/安装说明.txt" <<'INSTALL'
将 YuWangSync.app 拖入 Applications 即可安装。
适用 macOS 14+、Apple Silicon。应用自带运行时。
此版本为预览版，尚未完成 Developer ID 签名与 Apple 公证。

更新前请通过 ⌘Q 或菜单栏“退出育网云”完全退出应用，再替换 Applications 中的旧版本。
关闭主窗口不会退出后台进程。
INSTALL
xcrun swiftc -swift-version 5 -O -target arm64-apple-macos14.0 \
  "$root/tools/dmg-artwork.swift" -o "$work/dmg-artwork"
"$work/dmg-artwork" render "$image_root"
python3 -m venv "$work/dmg-tools"
dmg_python="$work/dmg-tools/bin/python3"
"$dmg_python" -m pip --disable-pip-version-check install --require-hashes -r "$root/tools/requirements-dmg.txt"
dmg="$root/artifacts/YuWangSync-macOS-arm64-preview.dmg"
mount="$work/dmg-mount"
mkdir -p "$mount"
hdiutil create -ov -volname "育网云" -srcfolder "$image_root" -format UDRW -fs HFS+ "$work/layout.dmg"
hdiutil attach -nobrowse -mountpoint "$mount" "$work/layout.dmg"
trap 'hdiutil detach "$mount" >/dev/null 2>&1 || true' EXIT
"$dmg_python" "$root/tools/dmg-layout.py" write "$mount"
hdiutil detach "$mount"
trap - EXIT
hdiutil convert "$work/layout.dmg" -ov -format UDZO -o "$dmg"
hdiutil verify "$dmg"
hdiutil attach -readonly -nobrowse -mountpoint "$mount" "$dmg"
trap 'hdiutil detach "$mount" >/dev/null 2>&1 || true' EXIT
codesign --verify --deep --strict "$mount/YuWangSync.app"
test "$(readlink "$mount/Applications")" = /Applications
diff -qr "$app" "$mount/YuWangSync.app"
"$dmg_python" "$root/tools/dmg-layout.py" verify "$mount"
if [[ "${YUWANG_DMG_PREVIEW:-0}" == 1 ]]; then
  "$work/dmg-artwork" capture "$mount" "$root/artifacts/ui-dmg-macos.png"
fi
hdiutil detach "$mount"
trap - EXIT
(cd "$root/artifacts" && shasum -a 256 YuWangSync-macOS-arm64-preview.dmg > YuWangSync-macOS-arm64-preview.dmg.sha256)
echo "Created $app"
