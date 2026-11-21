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

**Branches:** `game-tunnel` (ab8e234, already in `main`) and `docs-setup` (173c315, local only). **Never push
`docs-setup`:** its README holds the host's public IP. If any of its README changes are wanted, re-make them on main
without the IP.

## In progress

Nothing.

## Next

- **Encrypted game tunnel, phase 2:** waits until **all friends have updated the app**. Phase 1 is live: the app
  uses the tunnel when the host offers it. Phase 2 closes the direct game port to the internet (broker and firewall
  side, owned by Broker and network), after which older builds can't join from outside the house. Nothing to do
  here until the orchestrator confirms every friend is on a build with ab8e234 or later.
- **Auto-update: built 2026-11-21, not committed, not released.**
  - Files: `Updater.cs`, `ReleaseSig.cs`, `UpdateKey.pub.txt` (the real public key, made by the host), the
    `.github/workflows/release.yml` workflow, and `tools/sign-release` plus `tools/new-signing-key.ps1` (runs on
    Windows PowerShell 5.1 and PowerShell 7).
  - Hooked in: AppCore (update check and forced update before Play), Program and MacProgram (`--update-trial`,
    single-copy lock handover), WebUi and MainForm, the version headers in Broker.cs, and the UI update phase and
    notice chip.
  - Tested in a scratch harness: signature, hash, version, platform, cache, and 404 or unreachable cases, plus
    swap and rollback with a dummy exe.
  - Not verified:
    - swapping the real GUI exe on Windows (including the two copies sharing the WebView2 data folder for up
      to 90 s)
    - anything on a Mac (ditto, xattr, `open -n`, moving a running bundle)
    - the UI in either window
    - the workflow itself, which has never run
  - Broker side: record the `X-UncleCraft-Version`, `-Platform` and `-Commit` headers per person, show a "who has
    updated" list, and optionally add `minAppVersion` (built by the Broker session).
- **Auto-update v2:** the design moved to GitHub. The user decided: updates come from the GitHub repo, every push to
  main reaches everyone, and updates are forced. A CI workflow builds and signs each release; the app checks
  releases/latest, swaps itself and can roll back. v2 was sent 2026-11-21 and is waiting on 3 confirmations: skip
  docs-only pushes, no approval environment, build with a test key first. The v1 summary below is out of date.
  Earlier v1 entry: the design was sent to the orchestrator on 2026-11-21 and is waiting on 4 decisions. It is in
  the shared notes folder as `unclecraft-auto-update-design.md`. Short version:
  - updates come from the broker as releases signed with an offline ECDSA key; the public key is built into the
    app, and the broker is only a store
  - each platform swaps itself and rolls back through a 90 s health handshake
  - the app sends an `X-UncleCraft-Version` header
  - the broker gets a `minAppVersion` setting
  The original brief asked to cover:
  - where updates come from: the broker over the pinned TLS channel, or the GitHub repo
  - how the app checks, downloads and swaps itself on Windows and on the Mac
  - rollback if a new version won't start
  - security: every update is verified before it runs, with a signature checked against a public key built into
    the app. The broker must not be able to push an exe without the signing key.
  - the app reporting its version to the broker, which answers "who has updated" for tunnel phase 2
  - what the broker side needs (built by the Broker session)

## Known bugs and gaps

- **Local builds are older than `main`:** `dist\UncleCraft.exe` (2026-11-20 18:43) and the `dist-mac` zips (19:14)
  were built before the wake-wait commit (21:43). Rebuild before handing anyone a binary.
- **Never verified on a real Mac:** whether the UI loads in WKWebView, Keychain writes, and the game start.
- **Split-screen controller focus** has not been tested.
- **No way to tell which build a friend runs:** the version is a fixed 0.1.0, which makes "has everyone updated?"
  hard to answer for phase 2. Not confirmed whether the app reports a version to the broker.
- `docs-setup` branch is probably stale and could be deleted (orchestrator's call).

## Review (2026-11-21)

Review only: nothing was fixed, built or run. Three subagents read three parts of the code: security, game and
downloads, and UI, hosts and packages. Each finding was checked against the code, and the top ones were checked
again here. Where two subagents found the same thing, it appears once.

**Counts:** 1 high, 8 medium, 9 low.

**Checked and clean:**
- **Certificate pin:** a SHA-256 of the full certificate, compared in fixed time. There is no fallback to accepting
  any certificate, and the tunnel uses the same check.
- **Saved logins:** DPAPI on Windows. On the Mac, Keychain writes go through stdin, never the command line.
- **Logging:** no tokens or passwords reach `launcher.log`.
- **Local listeners:** the tunnel and the voice relay listen on 127.0.0.1 only.
- **Mojang downloads:** checked against their SHA-1 and written to `.part` first. Mods are checked against their
  SHA-256 the same way.
- **Windows web view:** navigation is locked to the embedded page, and there is no `innerHTML` with server text.

**Ports:** the API and the game tunnel both run on **25591**. 25590 is the broker's admin API, reachable only from
the host PC.

| # | Sev | Where | What goes wrong | Suggested fix | Size |
| --- | --- | --- | --- | --- | --- |
| 1 ✅ fixed (wave 1; Back = Esc / pad B only, no on-screen cancel button; the wake wait can't be cut short) | High | `Tunnel.cs:127-133`, `Broker.cs:117-120`, `Game.cs:257-268`, `AppCore.cs:250` | A connection that goes quiet hangs Play for good. The tunnel's TLS handshake has no timeout (the 8 s limit covers only the TCP connect), and mod and Mojang downloads have none once the headers arrive. `Install` runs with `CancellationToken.None`. Trigger: the host PC sleeps or Wi-Fi drops partway through. `_busy` stays set, so every later Play says "Already starting a game." until the app is restarted. | One timeout of about 15 s across connect, handshake, hello and answer. A "no bytes for 30 s" timeout on body reads, then the existing retry. A real cancel token for `Install`, wired to Back. | S-M |
| 2 ✅ fixed (wave 2; checked when the mod list is read) | Med | `Game.cs:209, 216, 365-366` (names from `Broker.cs:110`) | A mod's file name from the host's list goes into `Path.Combine` unchecked. A name like `..\..\x.exe` or a full path writes outside the mods folder (for example over `UncleCraft.exe` or into Startup). The hash doesn't help, because the same list supplies it. This only matters if the broker is compromised. | Accept only `Path.GetFileName(f) == f`, ending in `.jar`, with no invalid characters. Check that the full path stays inside the folder. | S |
| 3 ✅ fixed (wave 2; Mac: per-launch bridge key, since Photino can't block navigation; Windows: source check) | Med | `mac/MacProgram.cs:31-33` | The Mac window has no navigation guard, and the message handler doesn't check where a message came from. WKWebView opens an HTML file dropped on the window by default, and that page can then call `setup` with an attacker's server and fingerprint, so later password logins go to the attacker. | Drop bridge messages unless the page is `app://localhost`, and block navigation and drag-drop if Photino allows it. Cheap extra on Windows: check `e.Source` in `WebUi.cs:78`. | S |
| 4 ✅ fixed (wave 2) | Med | `AppCore.cs:140` with `AppCore.cs:90` | Pressing Connect in Settings while a game runs sets `_brokerKey = null` (even with unchanged values). The next status poll then disposes the `BrokerClient` that the game's `Watch()` still uses. Heartbeats fail and `Release` throws, so the host's account stays reserved until its timeout and the UI never hears the game closed. | Keep the client when the key hasn't changed, and don't dispose a client a `Watch` holds (give each `Watch` its own, or dispose it once `_running` is 0). | S |
| 5 ✅ fixed (wave 2 targeted ports, then wave 3: MainForm now runs on AppCore through the same JSON messages and has a Cancel button) | Med | `MainForm.cs:198-279` vs `AppCore.cs:263, 294-301` | The classic fallback (no WebView2) has drifted from AppCore. It doesn't wait for the server to wake, so on a sleeping server the game joins too early. It doesn't catch an early Java crash (`java_failed`). An invite can't set the player's own password (`MainForm.cs:68`), and the saved cards don't refresh. | Rebuild MainForm on AppCore, removing about 150 duplicated lines. Minimum fix: make `WaitForServer` public and call it. | M (S minimum) |
| 6 ✅ fixed (wave 2) | Med | `Logins.cs:26-31`, `mac/SecretStore.Mac.cs` | Any error reading a saved login deletes it for good. On the Mac, clicking Deny on a Keychain prompt or cancelling a Keychain unlock makes `Read()` return null, and the login is removed. The message also says "this Windows account" on the Mac. | Delete only on a definite corrupt result: `CryptographicException` or `FormatException` on Windows, `security` exit 44 on the Mac. Otherwise show "try again", in wording that fits both platforms. | S |
| 7 ✅ fixed (wave 2; one-copy lock is Windows only, macOS keeps an app to one copy itself) | Med | `AppCore.cs:269-280`, `Game.cs:353-367, 418`, `Program.cs:59` | Play can be clicked again while a game runs, and each launch always uses slot `i+1`. There is no single-instance lock either. A second launch on slot p1 fails with a raw "file in use" error on `launcher-java.log` or a mod jar, or shares options and mods with the running game. | Track running slots and pick a free one, or say "Player 1 is already playing". Add a named mutex so only one copy runs. | M |
| 8 ✅ fixed (wave 2; Fabric, asset and Java indexes still need the internet the first time only) | Med | `Game.cs:65, 78`, `AppCore.cs:316-320`, `Broker.cs:117` | Every Play fetches Mojang's version list, even when everything is installed. With no internet, or with Mojang blocked or down, nobody can play, and the child sees raw text like `No such host is known. (piston-meta.mojang.com:443)`. `DownloadMod` errors and checksum or JSON errors also show raw .NET text. | Start from files on disk when Mojang can't be reached. Turn network and disk errors into codes the page knows (`unreachable`, `download_failed`) with kid-friendly wording, and keep the raw text in the log. | M |
| 9 ✅ fixed (wave 2) | Med | `MainForm.cs:114, 160, 289-305`, `Program.cs` | In the classic UI, "Forget" while the host is offline throws on the UI thread (an async-void handler with no try), and the user gets the raw .NET crash dialog. `MainForm.Watch` has no catch either. Program.cs has no global exception handlers, so nothing is logged. | Wrap both calls the way AppCore does. Add `Application.ThreadException`, `UnhandledException` and `UnobservedTaskException` handlers that call `Log.Error`. | S |
| 10 ✅ fixed (wave 3) | Low | `Broker.cs:61-67, 43`, `AppCore.cs:137` | A port outside 1-65535 (for example `host:255910`) passes `ParseAddress`, then `new Uri` throws "Invalid URI: Invalid port specified." outside the try, and it is logged as a crash. `host:abc` silently uses the default port. | Check that the port is a number in range inside `ParseAddress`, and throw the friendly address error. | S |
| 11 ✅ fixed (wave 3) | Low | `Game.cs:75-81, 143-152` | The Fabric profile and the Java runtime list are written once and never checked. A crash during `WriteAllTextAsync` leaves a cut-off file, and every later launch fails to parse it, with a raw error. The runtime list's own `sha1` is ignored. | Write to `.part`, then move it into place. On a parse failure, delete the file and download it again. Check the SHA-1. | S |
| 12 ✅ fixed (wave 2) | Low | `mac/SecretStore.Mac.cs:50-55` | `ReadToEnd()` blocks until `security` exits, so `WaitForExit(15_000)` never limits anything. An unanswered Keychain unlock prompt hangs the login. If the wait ever does time out, `p.ExitCode` throws. | Read stdout and stderr asynchronously, wait with a timeout, kill the process on timeout, and return a failure code. | S |
| 13 ✅ fixed (wave 3) | Low | `Program.cs:35-48` | Self-install doesn't compare versions, so running an old exe left in Downloads replaces a newer installed copy. `File.Copy` writes straight over the installed exe, so a failure partway (disk full, antivirus) leaves a broken exe that the shortcuts point to. | Compare `FileVersionInfo` and warn on a downgrade. Copy to `.new`, then `File.Move(overwrite)`. | S |
| 14 ✅ fixed (wave 3) | Low | `Game.cs:423` | `launcher-java.log` masks the access token but not the one-time join name, which Minecraft prints at startup. It is used up on join, hence low. | Also mask `token.Username` in `OnLine`. | S |
| 15 ✅ fixed (wave 3) | Low | `Logins.cs:6` | `PasswordChoice`'s generated `ToString()` includes the password. `InviteChoice` already overrides it. No log line prints a choice today, so this would only bite on a future log line. | Override `ToString` or `PrintMembers` to mask it. | S |
| 16 ✅ fixed (wave 3) | Low | `Game.cs:209, 366` | Cached and per-game mods are judged up to date by size only. If the host swaps in a different build with the same name and size, players keep the old jar without any error. | Keep a `name -> sha256` record beside the cache and compare against it. | S |
| 17 ✅ fixed (wave 3) | Low | `WebUi.cs:101-108` vs `Program.cs:107` | Split-screen sizes the games for the launcher's monitor but tiles them on the primary monitor. Trigger: the launcher sits on a second monitor with a different resolution, so the games start at the wrong size and then jump. | Pass the target screen's working area into `TileSideBySide`. | S |
| 18 ✅ fixed (wave 3; Directory.Build.props holds the version, one mac/Info.plist.template) | Low | `McShared.csproj:12, 42`, `mac/UncleCraft.Mac.csproj:11`, `mac/build-mac.ps1:7`, `mac/build-mac.sh:7, 24-44`, `mac/package_mac.py:11-30` | `0.1.0` is written in 5 places, and the plist template is duplicated, so bumping the csproj alone leaves the Mac build on the old version. That also blocks a "who has updated?" check. `McShared.csproj:42` is a dead `Delete`. | Read the version from the csproj (`dotnet msbuild -getProperty:Version`) and share one plist template. Bump the version on each change that matters. Delete line 42. | S |

### Update recommendations

- ✅ done (wave 3) **Microsoft.Web.WebView2:** 1.0.2903.40 → 1.0.4370.26 (latest stable). The APIs used here are unchanged. Worth
  doing.
- **Photino.NET:** 4.0.16 is already the latest.
- **.NET:** net10.0 / net10.0-windows (.NET 10 LTS) is current. No deprecated APIs were found.
- **Version number:** start bumping it (see #18) before tunnel phase 2, so you can tell who has updated.

## Done log

- 2026-11-21: plan file set up. No code changes.
- 2026-11-21: code review written (section above). No code changes.
