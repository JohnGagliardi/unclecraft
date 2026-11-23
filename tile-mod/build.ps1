# Builds unclecraft-tile.jar (committed: CI builds the app but not Java). Compiles with javac against the game files
# UncleCraft already installed in %LOCALAPPDATA%\mc-shared (read only; start UncleCraft once if they're missing).
# No Gradle and no remapping: Minecraft 26.x is not obfuscated, so the mixin names the real class and fields.
param([string]$Jdk = "$env:USERPROFILE\.jdks\openjdk-25.0.1\bin")
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$lib = "$env:LOCALAPPDATA\mc-shared\libraries"
$cp = @(
    "$env:LOCALAPPDATA\mc-shared\versions\26.3\26.3.jar",
    "$lib\net\fabricmc\sponge-mixin\0.17.5+mixin.0.8.7\sponge-mixin-0.17.5+mixin.0.8.7.jar",
    "$lib\org\lwjgl\lwjgl\3.4.3\lwjgl-3.4.3.jar",
    "$lib\org\lwjgl\lwjgl-sdl\3.4.3\lwjgl-sdl-3.4.3.jar",
    "$lib\org\slf4j\slf4j-api\2.0.17\slf4j-api-2.0.17.jar"
)
foreach ($f in $cp) { if (-not (Test-Path $f)) { throw "missing $f (start UncleCraft once so it installs the game)" } }
$classes = Join-Path $here 'build\classes'
if (Test-Path $classes) { Remove-Item -Recurse -Force $classes }
New-Item -ItemType Directory -Force $classes | Out-Null
$src = Get-ChildItem -Recurse (Join-Path $here 'src') -Filter *.java | ForEach-Object FullName
& "$Jdk\javac.exe" --release 25 '-proc:none' '-Xlint:-options' -cp ($cp -join ';') -d $classes $src
if ($LASTEXITCODE -ne 0) { throw 'javac failed' }
Copy-Item (Join-Path $here 'fabric.mod.json'), (Join-Path $here 'unclecraft_tile.mixins.json') $classes
$jar = Join-Path $here 'unclecraft-tile.jar'
if (Test-Path $jar) { Remove-Item -Force $jar }
& "$Jdk\jar.exe" --create --no-manifest --file $jar -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'jar failed' }
Write-Host "built $jar ($((Get-Item $jar).Length) bytes)"
