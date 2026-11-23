package unclecraft.tile.mixin;

import com.mojang.blaze3d.platform.Window;
import org.lwjgl.sdl.SDLVideo;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.spongepowered.asm.mixin.Final;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.Unique;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * UncleCraft split-screen: puts the game window in its tile with no title bar or frame. Does nothing unless the game
 * was started with -Dunclecraft.tile=x,y,w,h (SDL window coordinates: top-left origin; pixels on Windows, points on
 * macOS). Minecraft 26.x windows are SDL3 windows; Window.applyWindowed() is the one place the game sizes and places a
 * non-fullscreen window (at startup from the constructor's setMode(), and when leaving fullscreen), so the tile goes
 * into the windowed fields before it runs and the frame comes off right after. Fullscreen never goes through here.
 * Any failure is logged and ignored: the game keeps its normal window.
 */
@Mixin(Window.class)
public abstract class WindowMixin {
    @Unique private static final Logger UNCLECRAFT_LOG = LoggerFactory.getLogger("unclecraft_tile");
    @Unique private static int[] unclecraft$tile;
    @Unique private static boolean unclecraft$parsed, unclecraft$logged;

    @Shadow @Final private long handle;
    @Shadow private int windowedX;
    @Shadow private int windowedY;
    @Shadow private int windowedWidth;
    @Shadow private int windowedHeight;

    @Unique private static int[] unclecraft$readTile() {
        if (unclecraft$parsed) return unclecraft$tile;
        unclecraft$parsed = true;
        String v = System.getProperty("unclecraft.tile");
        if (v == null || v.isBlank()) return null;
        try {
            String[] p = v.trim().split(",");
            if (p.length != 4) throw new IllegalArgumentException("need x,y,w,h");
            int[] t = new int[4];
            for (int i = 0; i < 4; i++) t[i] = Integer.parseInt(p[i].trim());
            if (t[2] < 100 || t[3] < 100) throw new IllegalArgumentException("tile too small");
            unclecraft$tile = t;
        } catch (Throwable e) {
            UNCLECRAFT_LOG.warn("UncleCraft tile: ignoring -Dunclecraft.tile={}: {}", v, e.toString());
        }
        return unclecraft$tile;
    }

    @Inject(method = "applyWindowed", at = @At("HEAD"), require = 0)
    private void unclecraft$beforeWindowed(CallbackInfoReturnable<Boolean> cir) {
        try {
            int[] t = unclecraft$readTile();
            if (t == null) return;
            windowedX = t[0];
            windowedY = t[1];
            windowedWidth = t[2];
            windowedHeight = t[3];
        } catch (Throwable e) {
            UNCLECRAFT_LOG.warn("UncleCraft tile: could not set the window tile", e);
        }
    }

    @Inject(method = "applyWindowed", at = @At("RETURN"), require = 0)
    private void unclecraft$afterWindowed(CallbackInfoReturnable<Boolean> cir) {
        try {
            int[] t = unclecraft$readTile();
            if (t == null || handle == 0L) return;
            boolean ok = SDLVideo.SDL_SetWindowBordered(handle, false);
            // The frame coming off can change the window's size or place: put it in the tile again (this fires SDL's
            // moved/resized events, which Window.handleEvent turns into Minecraft's own size and framebuffer updates).
            ok &= SDLVideo.SDL_SetWindowPosition(handle, t[0], t[1]);
            ok &= SDLVideo.SDL_SetWindowSize(handle, t[2], t[3]);
            SDLVideo.SDL_SyncWindow(handle);
            if (!unclecraft$logged) {
                unclecraft$logged = true;
                UNCLECRAFT_LOG.info("UncleCraft tile: borderless window at {},{} size {}x{}{}", t[0], t[1], t[2], t[3], ok ? "" : " (SDL reported an error)");
            }
        } catch (Throwable e) {
            UNCLECRAFT_LOG.warn("UncleCraft tile: could not make the window borderless", e);
        }
    }
}
