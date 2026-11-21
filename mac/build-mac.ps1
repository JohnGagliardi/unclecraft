# Builds the macOS UncleCraft.app for Apple Silicon and Intel and zips each into app\dist-mac.
# Runs on Windows (cross-publish). The app is unsigned: see app\dist-mac\MAC-README.txt for first-run steps.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$dist = Join-Path $here '..\dist-mac'
$icns = Join-Path $here '..\icon\UncleCraft.icns'
$version = "$(dotnet msbuild (Join-Path $here 'UncleCraft.Mac.csproj') -getProperty:Version)".Trim() # from ..\Directory.Build.props
if ($LASTEXITCODE -or -not $version) { throw 'could not read the version' }
New-Item -ItemType Directory -Force $dist | Out-Null
foreach ($rid in 'osx-arm64', 'osx-x64') {
    $out = Join-Path $here "out\$rid"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    dotnet publish (Join-Path $here 'UncleCraft.Mac.csproj') -c Release -r $rid --self-contained -o $out -nologo
    if ($LASTEXITCODE) { throw "publish $rid failed" }
    $label = if ($rid -eq 'osx-arm64') { 'apple-silicon' } else { 'intel' }
    python -I (Join-Path $here 'package_mac.py') $out $label $icns (Join-Path $dist "UncleCraft-mac-$label.zip") $version
    if ($LASTEXITCODE) { throw "packaging $rid failed" }
}
Copy-Item (Join-Path $here 'MAC-README.txt') $dist -Force
"Done: $dist"
