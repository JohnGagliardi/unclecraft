# UncleCraft app plan

Kept by the standing UncleCraft app session. Last updated 2026-11-21.

## Rules for this component

- This folder (`mc-shared/app`) is its own git repo: `JohnGagliardi/unclecraft`, private. Friends build it from
  source with `git pull` and a rebuild, so **anything pushed to `main` is what friends get**.
- Commits and pushes belong to the orchestrator. No publishing or releasing builds from this session.
- Broker changes belong to the Broker and network session; ask through the orchestrator.
- No test suites or new tests unless asked. Never enter Microsoft passwords; never print or commit secrets.
- Each task runs in a subagent; the result is checked here before it is marked done.

## What exists

**App:** UncleCraft 0.1.0, the launcher Java players use. The window says "Family Server". The player signs in to
the host's broker, and the app starts Minecraft already signed in and joins the server.

**Platforms:**
- Windows: .NET 10 WinForms hosting a WebView2 pixel UI; `MainForm.cs` is the classic fallback when the WebView2
  runtime is missing. Self-contained single exe that installs itself to `%LOCALAPPDATA%\Programs\UncleCraft`.
  Data stays in `%LOCALAPPDATA%\mc-shared`.
- macOS: Photino.NET host in `mac/`, built with `mac/build-mac.sh` on a Mac or `mac/build-mac.ps1` plus
  `package_mac.py` on Windows (unsigned zips in `dist-mac/`).

**Files:**

| File | Role |
| --- | --- |
| `AppCore.cs` | Shared core behind both UIs (the JSON bridge actions) |
| `Broker.cs` | Broker client: pinned-fingerprint HTTPS, address parsing |
| `Tunnel.cs` | Local `127.0.0.1` entrance that carries the game and voice inside the pinned TLS tunnel |
| `Game.cs` | Downloads Minecraft, Java and mods; launches; split-screen |
| `Logins.cs`, `Remembered.cs`, `SecretStore.*.cs` | Sign-in, invite codes, remembered players (DPAPI / Keychain) |
| `Settings.cs`, `Platform.cs`, `Log.cs` | Settings, per-OS paths, `launcher.log` |
| `WebUi.cs`, `Program.cs`, `MainForm.cs` | Windows host, install and shortcuts, fallback UI |
| `mac/MacProgram.cs` | macOS host |
| `ui/` | The pixel launcher UI (`index.html`, `app.js`, `pixel.js`, `style.css`); `shots.ps1` renders previews |

**Features in `main` (6b9a7d3, same as `origin/main`):**
- Setup wizard: server address (LAN, public, DNS, IPv6, pasted URL) and pasted security code.
- Invite code with the player's own password; remember-me player cards with Forget.
- One-click play; two players on one PC (split-screen).
- A fresh join name is fetched right before each launch.
- Voice chat mod installed by the app.
- Game connection carried inside the pinned TLS tunnel when the host offers it (ab8e234).
- Waits for the host's server to wake before launching (6b9a7d3).

**Branches:** `game-tunnel` (ab8e234, already in `main`) and `docs-setup` (173c315, local only, looks superseded by
d028ad5).

## In progress

Nothing.

## Next

- **Encrypted game tunnel, phase 2:** waits until **all friends have updated the app**. Phase 1 is live: the app
  uses the tunnel when the host offers it. Phase 2 closes the direct game port to the internet (broker and firewall
  side, owned by Broker and network), after which older builds can't join from outside the house. Nothing to do
  here until the orchestrator confirms every friend is on a build with ab8e234 or later.
- No app tasks are queued.

## Known bugs and gaps

- **Local builds are older than `main`:** `dist\UncleCraft.exe` (2026-11-20 18:43) and the `dist-mac` zips (19:14)
  were built before the wake-wait commit (21:43). Rebuild before handing anyone a binary.
- **Never verified on a real Mac:** whether the UI loads in WKWebView, Keychain writes, and the game start.
- **Split-screen controller focus** has not been tested.
- **No way to tell which build a friend runs:** the version is a fixed 0.1.0, which makes "has everyone updated?"
  hard to answer for phase 2. Not confirmed whether the app reports a version to the broker.
- `docs-setup` branch is probably stale and could be deleted (orchestrator's call).

## Done log

- 2026-11-21: plan file set up. No code changes.
