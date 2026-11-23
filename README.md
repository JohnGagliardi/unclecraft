# UncleCraft

The launcher for our private Minecraft server, the **Family Server**. You sign in to the host's server, and
UncleCraft starts Minecraft already signed in and joins the server. You don't need your own Minecraft account.

## 1. Install (once)

Download it from the **Releases** page: <https://github.com/JohnGagliardi/unclecraft/releases/latest>

- **Windows:** `UncleCraft-win-x64.exe`. Run it. The first time, it installs itself and adds Start menu and desktop
  shortcuts. After that, open it from the shortcut (the window says **Family Server**).
- **Mac** (macOS 12 or newer): the zip for your chip, `UncleCraft-mac-arm64.zip` for Apple M1/M2/M3/M4 or
  `UncleCraft-mac-x64.zip` for Intel Macs. Unzip it and move **UncleCraft** to Applications. Because it's a
  download, macOS may block the first start; `mac/MAC-README.txt` has the steps.

After that, UncleCraft updates itself before you play. You never download it again.

## 2. Connect (first time only)

Ask the host for:
- the **server address** (the port is **25591**; you only need it if the app asks)
- the **security code**: a long `AB:CD:...` code. **Paste it, don't type it.** It proves you're talking to the real
  server.
- your **login name** and a one-time **invite code** (it works once, for about a week)

In UncleCraft:
1. In setup, enter the server address and paste the security code.
2. Choose **New player** > **I have an invite code**, enter your login name and invite code, and pick your own
   password (10+ characters). The host never sees it. Leave **Remember me** ticked so next time is one click.
3. Click your player card to play. The first start downloads Minecraft, Java and the mods (about 600 MB), so give it
   a few minutes. Minecraft then opens and joins the server by itself. To stop, just quit Minecraft.

## Up to four players

One PC can run up to four games at once, console-style split-screen.
- Pick **1**, **2**, **3** or **4** players at the top of the player cards. Then click player 1, then a card for each
  other screen. Each of players 2-4 is another saved player, or **Guest of <player 1>** (when the host's server
  offers guests): no login of their own, they play as `<Player 1>_P2`, `_P3`, `_P4` with their own inventories.
  Click a filled screen to empty it. Emptying player 1 also removes the guests.
- **Layout** on the monitor the launcher is on (the taskbar stays visible):
  - 2 players: top and bottom halves, full width (player 1 on top).
  - 3 players: player 1 on the top half; players 2 and 3 share the bottom half, left and right.
  - 4 players: a 2x2 grid (1 top-left, 2 top-right, 3 bottom-left, 4 bottom-right).
  The app puts the windows there. On a Mac that needs the **Accessibility** permission: the first split-screen Play
  asks for it (and may also ask to let UncleCraft control System Events: allow that too). Turn UncleCraft on in
  System Settings > Privacy & Security > Accessibility, then Play again. Without it the games still start at the
  right size and you drag them into place; the home screen says so once. After replacing the app with a newer zip,
  macOS may need the switch turned off and on again.
- **All or nothing:** every player takes a seat on the server. If there aren't enough free seats for everyone, nobody
  starts and you see **THE SERVER IS FULL**: try fewer players.
- **Controllers:** plug them all in before you click Play. The keyboard and mouse work in whichever window you
  click. Player 1's game takes the first controller by itself. Players 2-4 start with no controller, so they don't
  all grab controller 1. In each of those windows, open Controllable settings > **Select Controller** once and pick
  that player's pad. The window's game folder remembers it for next time. Controllable can't be told "use controller
  number 3", so the app can't pick for you. Two identical pads may need picking again after a restart.
- Each screen has its own game folder (`games\p1` to `games\p4`: options, Controllable settings, logs). Games start
  10 seconds apart.

## Voice chat

Voice chat is built in. The app installs the mod for you.
- **Talk:** hold **Caps Lock** (push to talk). Press **V** in game for voice settings: microphone, volume, or
  switching to voice activation.
- **Who hears you:** everyone in the same world. The hub city is always full volume; some player worlds use
  proximity, so only people near you hear you there.
- **Microphone permission:** the first time, allow Java to use the microphone. On a Mac it asks; on Windows check
  Settings > Privacy & security > Microphone > "Let desktop apps access your microphone".
- Voice isn't recorded.

## How it connects (and why updating matters)

- The app only talks to the host's server over an encrypted connection that is checked against the security code, so
  nobody in between can read or fake it.
- Your game connection goes through that same encrypted connection: the app starts a small local entrance on your
  own computer (`127.0.0.1`), Minecraft connects to it, and the app carries everything to the server inside the
  encryption. Voice chat goes the same way. You don't need to set anything up.
- The server only accepts internet players through this tunnel, so **older builds of UncleCraft can't join from
  outside the host's house.** If you can't connect, update first.

## Updating

It's automatic. When there's a new version, pressing Play shows **Updating UncleCraft…**, then the app reopens.
Press Play again. Your saved login and game files are kept.

## If something goes wrong

| What you see | What to do |
| --- | --- |
| Can't reach the server / timed out | The server may be off, or not open to the internet right now. Ask the host. |
| "Only accepts players at the host's home right now" | The host needs to open the server to the internet. |
| Security code doesn't match | Paste it again exactly. If it still fails, stop and tell the host. |
| Invite code rejected | Each code works once. Ask the host for a new one. |
| Joins worked before, now they don't | Close and reopen UncleCraft so it updates. |
| "Couldn't check for updates" | GitHub couldn't be reached. You can still play; it tries again next time. |
| "Already playing on this server" | Wait 30 seconds and try again. |
| No one hears you | Check the microphone permission above and the voice settings (**V**). |

Send the host what the app said, plus the log:
- **Windows:** `%LOCALAPPDATA%\mc-shared\launcher.log`
- **Mac:** `~/Library/Logs/UncleCraft/launcher.log`

## For contributors

Building from source needs Git and the **.NET 10 SDK** (<https://dotnet.microsoft.com/download/dotnet/10.0>):

```
git clone https://github.com/JohnGagliardi/unclecraft.git
cd unclecraft
dotnet publish -c Release -o dist     # Windows: then run dist\UncleCraft.exe
sh mac/build-mac.sh                   # Mac: builds for your chip and puts it in Applications
```

Your own builds are the same as the releases: they take the version of the newest release tag in your checkout
(`git describe`; `0.2.0` for a copy without git history, such as a zip download) and update themselves from GitHub
Releases like everyone's copy: Play installs a newer release first. A build of the latest release's code, edited or
not, has that release's number and isn't updated until the next one. To test local
changes, opt out with `--no-update` or the environment variable `UNCLECRAFT_NO_UPDATE=1`; the home screen then says
updates are off on this copy. (A build given a higher version by hand with `-p:Version` counts as newer and isn't
updated.) Every push to `main` (except docs-only changes)
becomes a signed release that everyone updates to, so changes go through a reviewed pull request.
