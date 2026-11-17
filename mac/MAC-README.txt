UncleCraft for Mac - first-time setup
=====================================

Which file?
  - Apple Silicon Mac (M1/M2/M3/M4; Apple menu > About This Mac shows "Chip: Apple M..."):
      UncleCraft-mac-apple-silicon.zip
  - Intel Mac ("Processor: Intel ..."):  UncleCraft-mac-intel.zip
  Needs macOS 12 or newer (tested by Microsoft on 14 Sonoma and newer).

1. Double-click the zip. Drag "UncleCraft" (the pixel icon) into your Applications folder.

2. UncleCraft isn't signed by Apple (it's a small private app), so macOS blocks it the first time.
   Easiest fix - open Terminal (Applications > Utilities > Terminal), paste this ONE line, press Return:

   APP=/Applications/UncleCraft.app; xattr -dr com.apple.quarantine "$APP"; find "$APP" -name '*.dylib' -exec codesign --force --sign - {} \; ; codesign --force --deep --sign - "$APP"; open "$APP"

   What it does: removes the "downloaded from the internet" flag and gives the app a local (ad-hoc) signature,
   which Apple Silicon Macs require before they run any program. Nothing is sent anywhere.
   You only do this once (and again after replacing the app with a newer zip).

   Without Terminal (may still need the line above on Apple Silicon if the app "quits unexpectedly"):
   - Right-click (or Control-click) UncleCraft in Applications > Open > Open.
   - Or: try to open it once, then System Settings > Privacy & Security > scroll down to
     "UncleCraft was blocked..." > Open Anyway > enter your Mac password > Open.

3. In UncleCraft: enter the host's address and security code (the host gives you both), then
   "New player" > "I HAVE AN INVITE CODE" and type the invite code. You can also choose your own password
   there (optional, 10+ characters; the host never sees it). After that, just click your card.
   The first start downloads Minecraft, Java and the mods (about 600 MB) - give it a few minutes.

4. Controller: plug it in before clicking Play. In the game: Controllable settings > Select Controller.

If something goes wrong, send the host these files (Finder > Go > Go to Folder..., paste the path):
   ~/Library/Logs/UncleCraft/launcher.log                       (the launcher)
   ~/Library/Application Support/UncleCraft/games/p1/launcher-java.log   (Java / game start)
   ~/Library/Application Support/UncleCraft/games/p1/logs/latest.log     (Minecraft itself)
No passwords are stored. Your saved login lives in your Mac's Keychain ("UncleCraft").
To remove everything: delete /Applications/UncleCraft.app and ~/Library/Application Support/UncleCraft,
and the "UncleCraft" entries in Keychain Access.
