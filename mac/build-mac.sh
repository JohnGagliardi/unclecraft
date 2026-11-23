#!/bin/sh
# Builds UncleCraft.app on this Mac and installs it to /Applications (or ~/Applications).
# Needs the .NET 10 SDK. Run from anywhere:  sh mac/build-mac.sh
# A locally built app isn't quarantined and gets an ad-hoc signature here, so no Gatekeeper steps are needed.
set -e
here="$(cd "$(dirname "$0")" && pwd)"
# Same number the binary gets (newest v* tag, see ../Directory.Build.targets); passed back in so plist and binary agree.
version="$(dotnet msbuild "$here/UncleCraft.Mac.csproj" -nologo -t:UncleCraftVersion -getProperty:Version | tr -d '[:space:]')"
[ -n "$version" ] || { echo "Could not read the version"; exit 1; }
case "$(uname -m)" in
  arm64)  rid=osx-arm64 ;;
  x86_64) rid=osx-x64 ;;
  *) echo "Unknown CPU type: $(uname -m)"; exit 1 ;;
esac

pub="$here/out/$rid"
app="$here/out/UncleCraft.app"
rm -rf "$pub" "$app"
dotnet publish "$here/UncleCraft.Mac.csproj" -c Release -r "$rid" --self-contained -o "$pub" -nologo -p:Version="$version"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$pub/." "$app/Contents/MacOS/"
find "$app/Contents/MacOS" \( -name '*.pdb' -o -name '*.xml' \) -delete
cp "$here/../icon/UncleCraft.icns" "$app/Contents/Resources/"
printf 'APPL????' > "$app/Contents/PkgInfo"
sed "s/@VERSION@/$version/g" "$here/Info.plist.template" > "$app/Contents/Info.plist" # shared with package_mac.py
chmod +x "$app/Contents/MacOS/UncleCraft"
find "$app" -name '*.dylib' -exec codesign --force --sign - {} \;
codesign --force --deep --sign - "$app"

if [ -w /Applications ]; then dest=/Applications; else dest="$HOME/Applications"; mkdir -p "$dest"; fi
rm -rf "$dest/UncleCraft.app"
ditto "$app" "$dest/UncleCraft.app"
echo "Installed: $dest/UncleCraft.app"
echo "Start it from Launchpad, or run:  open \"$dest/UncleCraft.app\""
