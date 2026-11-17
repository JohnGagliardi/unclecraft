# UncleCraft

The launcher for our private Minecraft server. You sign in to the host's server, and UncleCraft starts Minecraft
already signed in and joins the server. You don't need your own Minecraft account.

Build it yourself on your own computer with the steps below. It takes about 5 minutes.

## 1. Install the tools (once)

- **Git:** <https://git-scm.com/downloads>. On a Mac, running `git` in Terminal offers to install it.
- **.NET 10 SDK:** <https://dotnet.microsoft.com/download/dotnet/10.0>. Pick the **SDK** for your system:
  - Windows x64
  - macOS Arm64 for Apple M1/M2/M3/M4
  - macOS x64 for Intel Macs

## 2. Get the code

This repo is private, so the host has to add your GitHub account first. Then:

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

Run `dist\UncleCraft.exe`. The first time, it installs itself and adds Start menu and desktop shortcuts.

**Mac** (Terminal, inside the `unclecraft` folder):

```
sh mac/build-mac.sh
```

This puts UncleCraft in Applications. Because you built it yourself, macOS doesn't block it, so you can skip the
Terminal steps in `mac/MAC-README.txt`. Those steps are only for the downloaded zip.

## 4. Connect

The host gives you three things:
- the server address
- the security code
- an invite code

In UncleCraft:
1. Enter the address and the security code. Paste the code, don't type it.
2. Choose **New player** > **I have an invite code**, and choose your own password (10+ characters).
3. Click your card to play. The first start downloads Minecraft, Java and the mods (about 600 MB).

## Updating

Inside the `unclecraft` folder, run `git pull`, then run the same build command again. Your saved login and game
files are kept.

## If something goes wrong

Send the host what the app said, plus the log:
- **Windows:** `%LOCALAPPDATA%\mc-shared\launcher.log`
- **Mac:** `~/Library/Logs/UncleCraft/launcher.log`
