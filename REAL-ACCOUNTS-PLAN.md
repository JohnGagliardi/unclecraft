# Plan: PC players on their own Microsoft accounts, with Bedrock-style split-screen guests

Status (2026-11-23): **decided; Track A is being built.** The broker goes first. Only test setups are used: no live
broker deploy or restart, and no app push or release, until the user OKs each one. Written by the App session; the
broker and plugin pieces belong to the Broker session.

## Decisions (user, 2026-11-23)

1. **Identity:** Xbox sign-in now. Build Track A. Track B waits for Mojang.
2. **Split-screen guests:** only Microsoft-linked people can bring one. Invite-code and password people can't.
3. **Bedrock:** a Microsoft account already approved on Bedrock is auto-approved on Java as the same person, and
   the other way round.
4. **Tokens:** the broker keeps players' MSA refresh tokens on the host, DPAPI-encrypted, for Track B.
5. **Guest name:** `<Person>_P2`.

## API contract (agreed between App and Broker)

All endpoints are on the normal broker API (pinned TLS, port 25591). Error bodies are `{ error, message }` as
everywhere else, and the app shows `message` under its own title for each `error` code.

**`GET /api/v1/status`** gains `features: ["microsoft", "guests"]`. The app shows the new options only when the
matching feature is listed, so an old broker hides them.

**`POST /api/v1/microsoft/start`**
- Request: `{ deviceLabel }`.
- To link the person who is already signed in, send it with `Authorization: Bearer <leaseToken>`.
- `200 { flowId, userCode, verificationUri, expiresIn, interval }`
- `flowId` is random and only for this flow. The flow is bound to the caller's IP, and the start calls are
  rate-limited per IP.
- Quiet hours don't block it: signing in starts no game.

**`POST /api/v1/microsoft/poll`** with `{ flowId }`, polled every `interval` seconds. The answers:
- `202 { state: "pending" }`
- **Signed in:** `200 { state: "done", person, gamertag, deviceToken, deviceId }`. The XUID is linked or
  approved, the device is issued, and the app then logs in with
  `POST /api/v1/sessions { deviceToken }` as usual.
- **Linked to the signed-in person:** `200 { state: "done", person, gamertag, linked: true }`. This is the
  answer when the flow was started with a lease token. No device token is returned, because the person already
  has one.
- `403 not_approved { gamertag }`: unknown XUID, now on the host's waiting list. The message says "Ask the host to
  let you in."
- `409 xuid_taken`: when linking, that Microsoft account is already linked to another person.
- `409 device_limit`: same as for invite codes.
- `400 ms_declined`, `410 ms_expired`: sign-in declined, or the code expired.
- `403 xbox_blocked`: XSTS refused. The message is the plain `XERR` text, for example child account or parental
  controls.
- `404 flow_unknown`: no such flow, or the flow is finished.

**`POST /api/v1/sessions`** (unchanged):
- The 200 answer also includes `microsoft: { linked, gamertag? }`, so the app knows whether to offer "Link your
  Microsoft account" and the guest option.
- With `javaLogin: "microsoft-only"`, a password or invite login for a linked person returns
  `403 use_microsoft`.

**`POST /api/v1/sessions/guest`**
- Request: `Authorization: Bearer <player 1's leaseToken>` and `{}`.
- `200`, the same shape as a `/sessions` answer: `leaseToken`, `joinTicket` in interim mode, `person:
  "<Person>_P2"`, `guestOf: "<Person>"`, `paper`.
- The app then uses the guest's `leaseToken` exactly like a normal one: minecraft-token, heartbeat and release.
- `403 guest_needs_microsoft`: player 1 isn't linked.
- `403 wrong_pc`: not the PC that player 1 logged in from.
- `409 guest_in_use`: player 1 already has a guest.
- `409 no_free_account`: the guest takes a pool slot like any other player.
- The guest lease ends when player 1's lease ends.
- The plugin names the guest `<Person>_P2` and gives it its own stable profile, a v3 UUID of
  `UncleCraft:guest:<person>:2`.

**Host CLI (Broker):**
- `microsoft list`
- `microsoft approve <gamertag> [person]`
- `microsoft unlink <person>`
- `microsoft remove <gamertag>`
- The admin list shows each person as linked or not linked.

**Bedrock (decision 3):** this is the Broker session's call; the App isn't affected.
- An XUID approved on one edition counts as approved on the other.
- Sharing one profile or inventory across editions is optional. If it's hard, tell the orchestrator.

## Why

Kevin ticked "Two players on this PC", and player 2 got the "Use your invite code" screen. Today every Java player
is a separate broker person, and each person needs their own invite code or password.

The user wants PC players tied to their own Minecraft accounts. Split-screen should then work the way it does on
Bedrock (commit 0b2522b): a guest of an approved player is let in automatically.

## What exists today (short)

**Server:** Paper runs in **interim mode**: `online-mode=false`, the whitelist is on, and there's no proxy.

**How a player gets in:**
- The broker lends one of the host's pool slots.
- The app gets a one-time **join name** that only works from that PC's IP.
- The plugin redeems the join name at pre-login and swaps in the person's own profile, a v3 UUID of
  `UncleCraft:<person>`. Inventories follow this UUID.

**Approved mode later:** `online-mode=true`, and the broker mints a real token from a pool account. This is blocked
by Mojang: `login_with_xbox` returns 403 (`app_not_approved`) until Mojang approves the host's Azure app.

**Bedrock players:**
- Bedrock players are approved by XUID.
- The Geyser fork gives each split-screen sub-client a synthetic XUID, `slot:<hostXuid>:<slot>`, and names it
  `<HostTag>_P2`.
- The broker auto-approves the guest of an approved host (`Bedrock.guestOf`).

## The key constraint

The step that proves someone owns **Minecraft Java** is `login_with_xbox` plus entitlements on
`api.minecraftservices.com`. It needs Mojang to approve our Azure app, and they haven't yet. Every third-party
launcher hits the same wall. Using the official launcher's client ID instead would mean pretending to be Microsoft,
so it's out.

**What works today without approval:** Microsoft sign-in, then Xbox Live, then XSTS. That proves the person controls
a real Microsoft account with an Xbox profile, and it gives their **XUID and gamertag**. That's the same identity
Bedrock uses.

So the plan has two tracks:

- **Track A (buildable now):** link each Java person to their Microsoft account (XUID). The game connection stays
  exactly as today: interim mode, join names, the person profile. Split-screen guests work through this.
- **Track B (after Mojang approval):** the same linked account also becomes the player's own Java login. Paper moves
  to online mode, and Java ownership is checked. It needs no new sign-in from players, because Track A already
  stored what Track B needs (see 1.4).

## 1. Signing in with Microsoft (Track A)

### 1.1 Flow: device code, run by the broker

Only Microsoft's own page ever sees the password.

1. In the app, the player clicks **Sign in with Microsoft**.
2. The app calls a new broker endpoint, `POST /api/v1/microsoft/start`. The broker calls `startDeviceCode` (already
   in `broker/lib/msa.js`) with its own `msaClientId`, and returns `{ flowId, userCode, verificationUri,
   expiresIn }`.
3. The app shows the code in large text, with a **Copy code and open Microsoft** button. That opens
   `microsoft.com/link` in the player's normal browser, not inside the app.
4. The player signs in **on Microsoft's page**, with their own email, password and 2FA, and enters the code.
5. The app polls `POST /api/v1/microsoft/poll { flowId }`.
6. The broker runs `pollDeviceCode`, then XBL, then XSTS for relying party `http://xboxlive.com`. That gives
   `DisplayClaims.xui[0]` with **xid (XUID)** and **gtg (gamertag)**. The broker has used every step of this
   except the relying party for the pool accounts.
7. The broker looks up who that XUID belongs to (1.2) and answers like a normal login: a session and a **device
   token**. From then on, the player clicks their card, just like today. They sign in with Microsoft only once per
   PC, or again when the device token expires after 90 days unused.

**Why the broker runs the flow and not the app:**
- No Microsoft or Xbox token ever passes through the app.
- The Azure client ID stays out of the public repo.
- No new redirect URI needs to be registered.
- It reuses code that already works.

**Which tokens the broker keeps:**
- It uses the MSA and Xbox tokens once and then drops them. They're never logged.
- It keeps the MSA refresh token only for Track B (1.4), DPAPI-encrypted like the pool accounts' tokens.

**Mac and the classic window:** they share the same flow through AppCore.

### 1.2 Who gets in

The new file is `broker/data/microsoft.json`. Like `bedrock.json`, it keys by XUID:

```json
{ "<xuid>": { "person": "Kevin", "gamertag": "KevinGamer", "linked": "2026-…" } }
```

- **A linked XUID** signs in as that person: their profile, inventory and worlds, exactly as today.
- **An unknown XUID** goes on a `waiting` list, as it does on Bedrock. The app shows **"ASK THE HOST TO LET YOU
  IN"**. The host runs `node broker.js microsoft approve <gamertag> [person]`:
  - with `person`, the XUID links to an existing person
  - without it, it makes a new person named after the gamertag
- **Optional: one identity across editions.** If the XUID is already an approved Bedrock player, the broker can
  auto-approve the Java link too. (Question 3.)

### 1.3 Moving the existing invite-code and password people over

Nobody gets cut off.

1. **Release 1:** existing people keep working unchanged. Signed-in people see a one-time prompt: **"Link your
   Microsoft account."** It runs the same device-code flow while already logged in, through
   `POST /api/v1/microsoft/link`, so it links to *this* person without host approval.
2. **New people** get the Microsoft sign-in instead of an invite code. The host can still make invite codes, which
   stay as the fallback.
3. **Later, when every person shows "linked"** on the host's admin list, the host can turn on a config setting,
   `javaLogin: "microsoft-only"`. That turns off passwords and invite codes for linked people. Device tokens keep
   working.
4. **Inventories and worlds are kept,** because the in-game identity is still the person's v3 UUID. Only the way of
   logging in changes.

### 1.4 Track B: after Mojang approves the Azure app

The refresh token saved in 1.1 lets the broker mint that player's own Java token. That's the same code path the pool
accounts use today: `mintMinecraftToken` and `minecraftFromMsa`, with `ownsJava` from entitlements. This needs a
small change to approved mode: a linked person uses **their own account** instead of a pool account.

What changes:
- **No pool limit for linked owners.** "THE SERVER IS FULL" only applies to people without Java.
- **The server can go online-mode,** as INTERIM-MODE.md already plans for approval day.
- **What players see:** their own skin, and their real name if wanted (the profile swap can stay).

People without Java keep borrowing a pool account (Question 1).

## 2. Player 2 in split-screen: a guest of player 1

The Bedrock model, applied to Java:
- Player 1 signs in as usual, with a device token or Microsoft. In Track A, player 1 must be a **linked**
  (Microsoft) person, the same rule as Bedrock's "guest of an approved player". During the move-over, any signed-in
  person could bring a guest (Question 2).
- **Player 2 needs no account, code or password,** just like a Bedrock guest. On the Two-players screen, player 2
  defaults to **"Guest of <player 1>"**. The player can still pick a saved person instead, as today.
- The app asks the broker for a guest seat in the same session:
  - The endpoint is `POST /api/v1/sessions/guest { leaseToken of player 1, slot: 2 }`.
  - The broker issues a second lease and join name, bound to the same PC IP, with `guestOf: <person>`.
  - It counts against the pool like any player.
- **Naming:** `<Person>_P2`, the same rule as Bedrock's `guestName`: alphanumerics only, cut to 16 characters.
- **Profile:** the guest gets a stable profile, a v3 UUID of `UncleCraft:guest:<person>:2`. Kevin's guest therefore
  gets the same inventory every time, and it stays separate from Kevin's.
- **Ties to player 1:**
  - The guest lease is valid only while player 1's session is.
  - If the host removes player 1, the guest goes too, like `remove` does for Bedrock guests.
  - At most one guest per person, and only on the same PC.
- **In Track B,** the guest borrows a pool account if Paper is online-mode. If no pool account is free, the guest
  gets "THE SERVER IS FULL" and player 1 still plays. While interim mode lasts, a guest needs no Java account at
  all.

**Does player 2 need their own Minecraft account?** No, the same as Bedrock. If player 2 has one, they can sign in as
themselves instead of playing as a guest. That gives them their own name and inventory on any PC.

## 3. Who builds what

| Piece | Session |
| --- | --- |
| Device-code endpoints `microsoft/start`, `poll` and `link`; `microsoft.json` with waiting and approve; CLI `microsoft approve/list/remove` | **Broker** |
| Guest seats: `sessions/guest`, `guestOf` on leases and `release`, the `_P2` name, the guest profile UUID, removing a guest with its host | **Broker** (plugin part included) |
| `features: ["microsoft", "guests"]` in `GET /status`, so the app only shows the new options on a broker that has them | **Broker** |
| `javaLogin` setting, the admin list "linked / not linked", Track B using the person's own token | **Broker** |
| "Sign in with Microsoft" screen (code, copy and open browser, waiting, errors), the "Link your Microsoft account" prompt | **App** |
| Two-players: the player-2 picker defaults to "Guest of P1", the guest seat call, the guest card in "Playing now" | **App** |
| Classic window and Mac: same through AppCore; README and setup text | **App** |

**Order:**
1. The Broker session builds and deploys its side first. It's backward compatible, so the old app is unaffected.
2. Then the app ships.

The app reads `features` from status, so even an early app release is harmless. I haven't contacted the Broker
session yet. The endpoint shapes above are a proposal to agree with it before either side starts.

## 4. Rollout: what needs the user's OK, and the risks

**Needs the user's OK:**
1. **The policy questions** below.
2. **Deploying the broker** (Broker session, live server).
3. **Pushing the app.** Every code push to main auto-releases to every family install, so this push is the moment
   families get the change.
4. **Turning on `microsoft-only`,** which turns passwords off. Only do it once the admin list shows everyone linked.
5. **Track B,** whenever Mojang approves. That's a separate go-ahead, because it flips Paper to online mode.

**Risks:**
- **Child accounts.** Microsoft accounts under 18 often fail at XSTS with Xbox error 2148916238 ("an adult must add
  it to a family"), or 2148916229 when parental controls block online play. Kids in the families may need a parent
  to adjust Xbox family settings first.
  - The app will show the same plain-English messages the broker already has in `XERR`.
  - The invite-code login stays as a fallback for these kids. This is the biggest practical risk.
- **Device-code phishing.** The flow trains people to type codes into microsoft.com/link.
  - The app only ever shows a code the broker just made.
  - The host should tell families never to enter a code someone else sends them.
- **Guest allowance.** Any approved player can bring one unnamed guest from their own PC. That's the same trust the
  user accepted for Bedrock. The guest is tied to the host player's PC IP and session, and limited to one.
- **Refresh tokens on the host PC.** Keeping players' refresh tokens for Track B puts their Microsoft sign-ins on the
  host PC, DPAPI-encrypted.
  - The alternative is to keep only the XUID now and ask everyone to sign in again on approval day.
  - Question 4.
- **No proof of Java ownership in Track A.** We only know the account is a real Microsoft/Xbox account, which is
  enough to say who someone is. Ownership can't be checked until Mojang approves.
- **The auto-release.** An app bug in the new login screen reaches everyone at once.
  - The old login stays the default path until the broker says `features` includes `microsoft`.
  - The auto-updater rolls back an app version that won't start, but not one with a broken screen.
  - Recommended: test it on the host PC first, with a broker started on test ports.

## Questions for the user

1. Is a real Microsoft/Xbox account enough to tie someone's identity (Track A), with the game still on the host's
   accounts until Mojang approves? Or should we wait for approval and require that each player owns Java?
2. During the move-over, can any signed-in person bring a split-screen guest, or only people who have linked
   Microsoft?
3. Should someone already approved on Bedrock be auto-approved on Java under the same Microsoft account, as one
   person?
4. Keep players' Microsoft refresh tokens on the host PC now, so Track B needs no second sign-in? Or keep only the
   XUID, and have everyone sign in once more on approval day?
5. Should the guest be named `<Person>_P2`, like Bedrock, or something friendlier like `Kevin's guest`, if it fits
   in 16 characters?
