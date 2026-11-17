#!/bin/sh
# Builds UncleCraft.app on this Mac and installs it to /Applications (or ~/Applications).
# Needs the .NET 10 SDK. Run from anywhere:  sh mac/build-mac.sh
# A locally built app isn't quarantined and gets an ad-hoc signature here, so no Gatekeeper steps are needed.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
version=0.1.0
case "$(uname -m)" in
  arm64)  rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "Unknown CPU type: $(uname -m)"; exit 1 ;;
esac

pub="$here/out/$rid"
app="$here/out/UncleCraft.app"
rm -rf "$pub" "$app"
dotnet publish "$here/UncleCraft.Mac.csproj" -c Release -r "$rid" --self-contained -o "$pub" -nologo

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$pub/." "$app/Contents/MacOS/"
find "$app/Contents/MacOS" \( -name '*.pdb' -o -name '*.xml' \) -delete
cp "$here/../icon/UncleCraft.icns" "$app/Contents/Resources/"
printf 'APPL????' > "$app/Contents/PkgInfo"
cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>UncleCraft</string>
  <key>CFBundleDisplayName</key><string>UncleCraft</string>
  <key>CFBundleIdentifier</key><string>local.unclecraft.launcher</string>
  <key>CFBundleExecutable</key><string>UncleCraft</string>
  <key>CFBundleIconFile</key><string>UncleCraft.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$version</string>
  <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.games</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>UncleCraft launcher (unofficial; not affiliated with Mojang or Microsoft)</string>
</dict>
</plist>
EOF
chmod +x "$app/Contents/MacOS/UncleCraft"
find "$app" -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --deep --sign - "$app"

if [ -w /Applications ]; then dest=/Applications; else dest="$HOME/Applications"; mkdir -p "$dest"; fi
rm -rf "$dest/UncleCraft.app"
ditto "$app" "$dest/UncleCraft.app"
echo "Installed: $dest/UncleCraft.app"
echo "Start it from Launchpad, or run:  open \"$dest/UncleCraft.app\""
