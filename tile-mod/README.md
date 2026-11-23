# unclecraft-tile (Fabric client mod)

Makes split-screen game windows borderless and puts each one in its tile. The app embeds `unclecraft-tile.jar`
(McShared.csproj and mac/UncleCraft.Mac.csproj) and `TileMod` in Game.cs copies it into `games/pN/mods` for
split-screen launches only (removed for every other launch), with `-Dunclecraft.tile=x,y,w,h` on the JVM command line.
Without that property the mod does nothing.

- Fabric Loader only, no Fabric API. One mixin, `WindowMixin`, into `com.mojang.blaze3d.platform.Window` (Minecraft
  26.x uses SDL3 windows, not GLFW): before `applyWindowed()` it puts the tile in the windowed position/size fields,
  after it `SDL_SetWindowBordered(false)`, then position and size again. `applyWindowed()` runs at startup and when
  leaving fullscreen; fullscreen is left alone. Every failure is caught and logged (`unclecraft_tile` logger).
- Coordinates are SDL window coordinates, top-left origin: physical pixels on Windows, points on macOS.

## Rebuilding

The jar is committed because CI builds the app but not Java. After changing `src/`, `fabric.mod.json` or the mixin
config, run (needs JDK 25 and UncleCraft's game files in `%LOCALAPPDATA%\mc-shared`; no Gradle, no remapping since
26.x is not obfuscated):

```powershell
.\tile-mod\build.ps1
```

Then commit `tile-mod/unclecraft-tile.jar`. If the game version changes, update the jar paths in build.ps1 and check
that `Window.applyWindowed()` and the `handle` / `windowed*` fields still exist (`javap -p`).
