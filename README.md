# UncleCraft

The launcher for our private Minecraft server, the **Family Server**. You sign in to the host's server, and
UncleCraft starts Minecraft already signed in and joins the server. You don't need your own Minecraft account.

Build it yourself on your own computer with the steps below. It takes about 5 minutes the first time.

## 1. Install the tools (once)

- **Git:** <https://git-scm.com/downloads>. On a Mac, running `git` in Terminal offers to install it.
- **.NET 10 SDK:** <https://dotnet.microsoft.com/download/dotnet/10.0>. Pick the **SDK** (not just the runtime) for
  your system:
  - Windows x64
  - macOS Arm64 for Apple M1/M2/M3/M4
  - macOS x64 for Intel Macs

## 2. Get the code

This repo is private, so send the host your GitHub username first and accept the invite email from GitHub. Then:

```
git clone https://github.com/JohnGagliardi/unclecraft.git
cd unclecraft
```

If it asks you to sign in, use your GitHub account. You can also use `gh repo clone JohnGagliardi/unclecraft` if
you have the GitHub CLI.

## 3. Build

**Windows** (PowerShell or Command Prompt, inside the `unclecraft` folder):

```
dotnet publish -c Release -o dist
```

Run `dist\UncleCraft.exe`. The first time, it installs itself and adds Start menu and desktop shortcuts. After that,
open it from the shortcut (the window says **Family Server**).

**Mac** (Terminal, inside the `unclecraft` folder; macOS 12 or newer):

```
sh mac/build-mac.sh
```

This builds UncleCraft for your Mac's chip and puts it in Applications. Because you built it yourself, macOS
doesn't block it, so you can skip the Terminal steps in `mac/MAC-README.txt` (those are only for a downloaded zip).

## 4. Connect (first time only)

The host gives you:
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

Inside the `unclecraft` folder:

```
git pull
```

then run the same build command again (Windows: `dotnet publish -c Release -o dist`, then run
`dist\UncleCraft.exe` once to update the installed copy; Mac: `sh mac/build-mac.sh`). Your saved login and game files
are kept.

## If something goes wrong

| What you see | What to do |
| --- | --- |
| Can't reach the server / timed out | The server may be off, or not open to the internet right now. Ask the host. |
| "Only accepts players at the host's home right now" | The host needs to open the server to the internet. |
| Security code doesn't match | Paste it again exactly. If it still fails, stop and tell the host. |
| Invite code rejected | Each code works once. Ask the host for a new one. |
| Joins worked before, now they don't | Update the app (`git pull` and rebuild). |
| "Already playing on this server" | Wait 30 seconds and try again. |
| No one hears you | Check the microphone permission above and the voice settings (**V**). |

Send the host what the app said, plus the log:
- **Windows:** `%LOCALAPPDATA%\mc-shared\launcher.log`
- **Mac:** `~/Library/Logs/UncleCraft/launcher.log`
