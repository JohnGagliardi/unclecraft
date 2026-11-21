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

**Two players on one PC:** tick **Two players**, then click player 1, then player 2. Plug controllers in before you
click Play.

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

Your own builds are version `0.2.0-dev`. They show when an update is available but aren't forced to take it, and
`--no-update` turns the check off so you can test local changes. Every push to `main` (except docs-only changes)
becomes a signed release that everyone updates to, so changes go through a reviewed pull request.
