using System.Reflection;
using System.Text.Json.Nodes;

namespace McShared;

/// <summary>An error the page shows with friendly wording (code -> title/hint in app.js).</summary>
public sealed class UiError(string code, string message, string? person = null) : Exception(message)
{
    public string Code { get; } = code;
    public string? Person { get; } = person;
}

/// <summary>What the window host can do with game windows (Windows: size + tile; macOS: size only).</summary>
public interface IGameWindows
{
    /// <summary>Launch size (Minecraft's --width/--height) of player <paramref name="index"/> (0-based) out of
    /// <paramref name="count"/> in split-screen: its SplitLayout rectangle, or null to let the game choose.</summary>
    (int width, int height)? SplitSize(int index, int count);
    /// <summary>Called once all game windows are up (games in player order).</summary>
    Task Arrange(IReadOnlyList<GameProcess> games);
}

/// <summary>
/// Console-style split-screen on one monitor's working area (taskbar / menu bar excluded), in player order:
/// 1 = the whole area; 2 = top | bottom halves; 3 = player 1 on the top half, players 2 and 3 in the bottom-left and
/// bottom-right quarters; 4 = 2x2 grid (top-left, top-right, bottom-left, bottom-right). Odd pixels go to the
/// right / bottom windows, so the rectangles cover the area exactly.
/// </summary>
public static class SplitLayout
{
    public static System.Drawing.Rectangle[] Rects(int count, System.Drawing.Rectangle area)
    {
        int x = area.X, y = area.Y, w = area.Width, h = area.Height, hw = w / 2, hh = h / 2;
        System.Drawing.Rectangle R(int rx, int ry, int rw, int rh) => new(rx, ry, rw, rh);
        var top = R(x, y, w, hh);
        var bottomLeft = R(x, y + hh, hw, h - hh);
        var bottomRight = R(x + hw, y + hh, w - hw, h - hh);
        return count switch
        {
            <= 1 => [area],
            2 => [top, R(x, y + hh, w, h - hh)],
            3 => [top, bottomLeft, bottomRight],
            _ => [R(x, y, hw, hh), R(x + hw, y, w - hw, hh), bottomLeft, bottomRight],
        };
    }
}

/// <summary>
/// Everything behind the pixel launcher page, shared by the Windows (WebView2) and macOS (Photino) hosts.
/// Page -> host: JSON {id, type, ...}. Host -> page: {id, ok, data|error} replies and {event, ...} events.
/// </summary>
public sealed class AppCore(Action<string> post, IGameWindows windows, IUpdateHost? updateHost = null)
{
    private readonly Settings _s = Settings.Load();
    private BrokerClient? _broker;
    private string? _brokerKey;
    private readonly List<BrokerClient> _retired = [];
    private readonly HashSet<int> _slots = []; // player slots (games/pN) with a game running
    private bool _busy;
    private CancellationTokenSource? _playCts;
    private int _running;
    private string? _notice; // update notice for the home screen (null = none)
    private bool _noticeSticky; // set by an update attempt: a later check doesn't clear it
    private bool _trialWatch;
    private HashSet<string> _features = []; // optional broker features from the last GET /status ("guests")

    /// <summary>Games still running (the host asks before closing).</summary>
    public int Running => _running;

    private void Post(JsonObject msg) { try { post(msg.ToJsonString()); } catch (Exception e) { Log.Error("post to page failed", e); } }

    private void Event(string name, JsonObject? data = null)
    {
        var m = data ?? new JsonObject();
        m["event"] = name;
        Post(m);
    }

    public async Task HandleMessage(string json)
    {
        int id = 0; string type = "";
        try
        {
            var m = JsonNode.Parse(json)!.AsObject();
            id = m["id"]?.GetValue<int>() ?? 0;
            type = m["type"]?.GetValue<string>() ?? "";
            JsonNode? data = type switch
            {
                "init" => Init(),
                "setup" => await Setup(m),
                "status" => await Status(),
                "setSound" => SetSound(m),
                "setMode" => SetMode(m),
                "forget" => await Forget(m),
                "play" => await Play(m),
                "cancel" => CancelPlay(),
                "checkUpdate" => UpdateJson(await CheckUpdate(true)),
                "applyUpdate" => await ApplyUpdate(),
                _ => throw new UiError("error", $"Unknown request {type}"),
            };
            Post(new JsonObject { ["id"] = id, ["ok"] = true, ["data"] = data });
        }
        catch (Exception e)
        {
            var (code, person) = e switch { UiError u => (u.Code, u.Person), BrokerException b => (b.Code ?? "error", null), _ => ("error", (string?)null) };
            if (type != "status") Log.Info($"{type} failed: {code}: {e.Message}");
            if (code == "error" && e is not UiError) Log.Error($"{type} crashed", e);
            Post(new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = new JsonObject { ["code"] = code, ["message"] = e.Message, ["person"] = person } });
        }
    }

    // ---------------- handlers ----------------

    private string? HostKey() { try { return BrokerClient.NormalizeFingerprint(_s.Fingerprint); } catch { return null; } }

    private static string FormatFp(string hex) => string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));

    private (string host, int port) Address() => BrokerClient.ParseAddress(_s.Server, _s.Port);

    private BrokerClient Broker()
    {
        var key = HostKey();
        var (host, port) = Address();
        if (key == null || host.Length == 0) throw new UiError("not_configured", "Enter the host's address and security code first.");
        var id = $"{host}:{port}/{key}";
        if (_broker == null || _brokerKey != id)
        {
            if (_broker != null) { lock (_retired) _retired.Add(_broker); DisposeRetired(); }
            _broker = new BrokerClient(host, port, key); _brokerKey = id;
        }
        return _broker;
    }

    /// <summary>Replaced clients may still be in use by Play/Watch (heartbeats, Release): dispose them only once nothing runs.</summary>
    private void DisposeRetired()
    {
        lock (_retired)
        {
            if (_busy || Volatile.Read(ref _running) > 0) return;
            foreach (var b in _retired) b.Dispose();
            _retired.Clear();
        }
    }

    private JsonArray SavedJson()
    {
        var key = HostKey();
        var arr = new JsonArray();
        if (key == null) return arr;
        foreach (var r in RememberedStore.ForHost(key))
            arr.Add(new JsonObject { ["person"] = r.Person, ["lastPlayed"] = r.LastPlayed?.ToUniversalTime().ToString("o") });
        return arr;
    }

    private JsonObject SettingsJson() => new()
    {
        ["server"] = _s.Server,
        ["fingerprint"] = HostKey() is { } k ? FormatFp(k) : _s.Fingerprint,
        ["soundOn"] = _s.SoundOn,
        ["playerCount"] = _s.PlayerCount, // 1-4 players on this PC
        ["players"] = new JsonArray([.. _s.Players.Select(p => (JsonNode)p)]), // screens 2-4 last time: names or "guest"
        ["player1"] = _s.Player1, // the classic window pre-fills its name box with it
    };

    private JsonNode Init()
    {
        Log.Info($"{Platform.AppName} {Updater.InfoVersion} on {Platform.Describe()}; data {Paths.Root}");
        if (Updater.IsTrial && !_trialWatch)
        {
            _trialWatch = true;
            SetNotice($"UncleCraft is updated to v{Version}.");
            _ = TrialHealth();
        }
        _ = CheckUpdate(false); // answers with an "update" event
        return new JsonObject
        {
            ["configured"] = HostKey() != null && _s.Server.Trim().Length > 0,
            ["settings"] = SettingsJson(),
            ["saved"] = SavedJson(),
            ["version"] = Version,
            ["platform"] = Platform.Name,
            ["logPath"] = Log.FilePath,
            ["update"] = UpdateJson(Updater.Last ?? new UpdateCheck("checking")),
            ["features"] = FeaturesJson(), // last known; the page's status polls carry the fresh list
        };
    }

    private static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

    // ---------------- updates ----------------

    private JsonObject UpdateJson(UpdateCheck st) => new()
    {
        ["state"] = st.State, ["version"] = st.Release?.Version, ["current"] = Version, ["reason"] = st.Reason, ["notice"] = _notice,
    };

    private void SetNotice(string text) { _notice = text; _noticeSticky = true; }

    /// <summary>
    /// The check used right before installing. The 10-minute cache is fine for "nothing to do", but a cached "available"
    /// may name a release that has since been replaced by a newer one (a check at 17:50 said 0.2.3, 0.2.5 came out at
    /// 17:52, Play at 17:58 installed 0.2.3). So an update is only installed from a fresh look at GitHub.
    /// </summary>
    private async Task<UpdateCheck> CheckBeforeInstall()
    {
        var st = await CheckUpdate(false);
        return st.State is "available" or "available-optout" ? await CheckUpdate(true) : st;
    }

    /// <summary>Checks (cached 10 minutes unless forced) and tells the page: {event:"update", state, version, current, reason, notice}.</summary>
    private async Task<UpdateCheck> CheckUpdate(bool force)
    {
        var st = await Updater.Check(force);
        if (!_noticeSticky)
            _notice = st.State switch
            {
                "failed" => "Couldn't check for updates.",
                // a plain label, not a button: --no-update / UNCLECRAFT_NO_UPDATE=1 (builds from source update like the rest)
                "available-optout" => $"Updates are off on this copy (v{st.Release!.Version} is on GitHub).",
                _ => null,
            };
        Event("update", UpdateJson(st));
        return st;
    }

    /// <summary>
    /// Downloads, verifies and starts the new version; on success this process exits. Any failure is logged, shown as a
    /// notice, and the caller carries on (Play goes on with this version). Cancel stops the download part.
    /// </summary>
    private async Task InstallUpdate(ReleaseManifest r)
    {
        if (Volatile.Read(ref _running) > 0) throw new UiError("update_quit_game", "Quit Minecraft to update UncleCraft first.");
        using var cts = new CancellationTokenSource();
        _playCts = cts;
        Event("phase", new JsonObject { ["phase"] = "update", ["version"] = r.Version });
        var shown = 0L;
        try
        {
            var file = await Updater.Download(r, (done, total) =>
            {
                var now = Environment.TickCount64;
                if (now - shown < 200 && done < total) return;
                shown = now;
                Event("progress", new JsonObject { ["stage"] = "update", ["doneBytes"] = done, ["totalBytes"] = total, ["doneFiles"] = 0, ["totalFiles"] = 1, ["done"] = false });
            }, cts.Token);
            _playCts = null;
            cts.Token.ThrowIfCancellationRequested();
            Event("phase", new JsonObject { ["phase"] = "update-apply", ["version"] = r.Version }); // no cancel from here
            await Updater.Apply(r, file, updateHost);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            throw new UiError("cancelled", "Stopped. Press Play when you're ready.");
        }
        catch (Exception e)
        {
            Log.Error($"update to {r.Version} failed; staying on {Version}", e);
            SetNotice(e is UpdateException { Notice: { } n } ? n : $"Update failed, playing on v{Version} for now.");
            Event("update", UpdateJson(Updater.Last ?? new UpdateCheck("failed")));
        }
        finally { _playCts = null; }
    }

    /// <summary>Bridge "applyUpdate": installs an available update now (also on an opted-out copy, which Play never forces).</summary>
    private async Task<JsonNode> ApplyUpdate()
    {
        if (_busy) throw new UiError("error", "Already starting a game.");
        _busy = true;
        try
        {
            var st = await CheckBeforeInstall();
            if (st.State is "available" or "available-optout") await InstallUpdate(st.Release!);
            return UpdateJson(Updater.Last ?? st);
        }
        finally { _busy = false; }
    }

    /// <summary>
    /// Started with --update-trial: report healthy once the page is up (init) and the host answered a status call, or
    /// after 45 s with the host offline. Until then the old version waits and rolls back if this one dies.
    /// </summary>
    private async Task TrialHealth()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while (!cts.IsCancellationRequested)
        {
            try { await Broker().GetStatus(cts.Token); break; }
            catch (Exception)
            {
                try { await Task.Delay(3000, cts.Token); } catch (OperationCanceledException) { /* 45 s are up */ }
            }
        }
        Updater.MarkHealthy();
    }

    private async Task<JsonNode> Setup(JsonObject m)
    {
        var server = m["server"]?.GetValue<string>()?.Trim() ?? "";
        var fp = m["fingerprint"]?.GetValue<string>() ?? "";
        if (server.Length == 0) throw new UiError("error", "Enter the host's address.");
        string key;
        try { key = BrokerClient.NormalizeFingerprint(fp); } catch (BrokerException e) { throw new UiError("error", e.Message); }
        (string host, int port) addr;
        try { addr = BrokerClient.ParseAddress(server, _s.Port); } catch { throw new UiError("error", "That address doesn't look right. Use an IP like 192.168.1.20 or a name like uncle.example.org."); }
        using var probe = new BrokerClient(addr.host, addr.port, key);
        var st = await probe.GetStatus();
        _features = BrokerClient.Features(st);
        _s.Server = server; _s.Fingerprint = FormatFp(key); _s.Save(); // Broker() makes a new client only if host/key changed
        Log.Info($"setup ok: host {addr.host}:{addr.port}, mode {st["mode"]}");
        return new JsonObject
        {
            ["settings"] = SettingsJson(), ["saved"] = SavedJson(),
            ["testMode"] = st["testMode"]?.GetValue<bool>() ?? false,
            ["features"] = FeaturesJson(),
        };
    }

    private async Task<JsonNode> Status()
    {
        var st = await Broker().GetStatus();
        _features = BrokerClient.Features(st);
        return st;
    }

    private JsonArray FeaturesJson() => new([.. _features.Order().Select(f => (JsonNode)f)]);

    /// <summary>
    /// Waits while the host's game server is waking up (it sleeps after 20 minutes with nobody playing, and logging in
    /// wakes it). Older brokers don't report it: then this returns at once.
    /// </summary>
    /// <summary>Quiet hours on the broker (Paper never wakes then): says when it reopens, in this PC's local time.</summary>
    private static string ClosedMessage(string? closedUntil) =>
        DateTimeOffset.TryParse(closedUntil, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)
            ? $"The server is closed for the night. It opens again at {t.ToLocalTime():t}."
            : "The server is closed for the night. Try again in the morning.";

    private static async Task WaitForServer(BrokerClient broker, Action<string> status, CancellationToken ct)
    {
        var deadline = DateTime.Now.AddMinutes(3);
        var told = false;
        while (true)
        {
            JsonNode st;
            try { st = await broker.GetStatus(ct); }
            catch (BrokerException) { return; }
            var paper = st["paper"]?.GetValue<string>();
            if (paper == "closed")
                throw new UiError("server_closed", ClosedMessage(st["closedUntil"]?.GetValue<string>()));
            if (paper is null or "up")
            {
                if (told) Log.Info("server is awake");
                return;
            }
            if (!told)
            {
                Log.Info($"server is {paper}: waiting for it to wake up");
                status("Waking up the server. This takes about 30 seconds...");
                told = true;
            }
            if (DateTime.Now > deadline)
                throw new UiError("server_asleep", "The server didn't wake up in time. Try again in a minute, or ask the host.");
            await Task.Delay(2000, ct);
        }
    }

    private JsonNode? SetSound(JsonObject m) { _s.SoundOn = m["on"]?.GetValue<bool>() ?? true; _s.Save(); return null; }
    /// <summary>{players: 1-4}; an older page sends {split: bool}.</summary>
    private JsonNode? SetMode(JsonObject m)
    {
        var n = m["players"]?.GetValue<int>() ?? (m["split"]?.GetValue<bool>() == true ? 2 : 1);
        _s.PlayerCount = Math.Clamp(n, 1, Settings.MaxPlayers);
        _s.Save();
        return null;
    }

    private async Task<JsonNode> Forget(JsonObject m)
    {
        var person = m["person"]?.GetValue<string>() ?? "";
        var key = HostKey();
        if (key != null)
        {
            var r = RememberedStore.ForHost(key).FirstOrDefault(x => x.Person.Equals(person, StringComparison.OrdinalIgnoreCase));
            if (r != null)
            {
                string? token = null;
                try { token = RememberedStore.Token(r); } catch { /* unreadable */ }
                RememberedStore.Remove(key, r.Person);
                if (token != null) { try { await Broker().ForgetDevice(token); } catch { /* host offline: token expires anyway */ } }
            }
        }
        return new JsonObject { ["saved"] = SavedJson() };
    }

    // ---------------- play ----------------

    private static PlayerChoice ParseChoice(JsonNode n, string hostKey)
    {
        var kind = n["kind"]?.GetValue<string>();
        var person = n["person"]?.GetValue<string>() ?? "";
        if (kind == "saved")
        {
            var r = RememberedStore.ForHost(hostKey).FirstOrDefault(x => x.Person.Equals(person, StringComparison.OrdinalIgnoreCase))
                ?? throw new UiError("device_token_invalid", $"{person} isn't saved on this computer any more.", person);
            return new SavedChoice(r);
        }
        if (kind == "guest") return new GuestChoice();
        if (kind == "invite")
        {
            var code = BrokerClient.NormalizeInvite(n["code"]?.GetValue<string>() ?? "")
                ?? throw new UiError("invite_invalid", "An invite code is 16 letters and numbers, like ABCD-EFGH-JKMN-PQRS.");
            var pw = n["newPassword"]?.GetValue<string>();
            if (string.IsNullOrEmpty(pw)) pw = null;
            else if (pw.Length < 10) throw new UiError("password_too_short", "Your new password needs at least 10 characters.");
            return new InviteChoice(code, pw);
        }
        return new PasswordChoice(person, n["password"]?.GetValue<string>() ?? "", n["remember"]?.GetValue<bool>() ?? true);
    }

    private async Task<JsonNode?> Play(JsonObject m)
    {
        if (_busy) throw new UiError("error", "Already starting a game.");
        // Play always runs the newest signed release: a newer one is installed first (this process then exits).
        // GitHub unreachable or a failed update: play on this version (CheckUpdate/InstallUpdate show the notice).
        _busy = true;
        try
        {
            var upd = await CheckBeforeInstall();
            if (upd.State == "available") await InstallUpdate(upd.Release!);
        }
        finally { _busy = false; }
        var broker = Broker();
        var hostKey = HostKey()!;
        var choices = m["players"]!.AsArray().Select(n => ParseChoice(n!, hostKey)).ToList();
        if (choices.Count == 0 || choices[0] is GuestChoice)
            throw new UiError("error", "Pick player 1 first; a guest plays next to them.");
        if (choices.Count > Settings.MaxPlayers)
            throw new UiError("error", $"Up to {Settings.MaxPlayers} players fit on one PC.");
        var people = choices.Where(c => c is not GuestChoice).Select(c => c.Name).ToList();
        if (people.Distinct(StringComparer.OrdinalIgnoreCase).Count() != people.Count)
            throw new UiError("error", "Each screen needs a different person (or a guest).");
        if (choices[0] is PasswordChoice) _s.Player1 = choices[0].Name;
        if (choices.Count > 1) _s.Players = [.. choices.Skip(1).Select(c => c is GuestChoice ? "guest" : c.Name)];
        _s.Save();

        _busy = true;
        using var cts = new CancellationTokenSource();
        _playCts = cts;
        var leases = new List<Lease>();
        PlayerChoice? current = null;
        Log.Info($"play: {choices.Count} player(s)");
        try
        {
            var game = await new GameInstaller(broker,
                text => Event("status", new JsonObject { ["text"] = text }),
                p => Event("progress", new JsonObject
                {
                    ["stage"] = p.Stage, ["doneBytes"] = p.DoneBytes, ["totalBytes"] = p.TotalBytes,
                    ["doneFiles"] = p.DoneFiles, ["totalFiles"] = p.TotalFiles, ["done"] = p.Done,
                })).Install(cts.Token);

            foreach (var c in choices)
            {
                cts.Token.ThrowIfCancellationRequested();
                current = c;
                Event("phase", new JsonObject { ["phase"] = "login", ["person"] = c.Name });
                // a guest needs player 1's lease: player 1 is always logged in first
                var lease = c is GuestChoice ? await broker.GuestSession(leases[0], cts.Token) : await Logins.LoginAs(broker, c, hostKey);
                leases.Add(lease);
                Log.Info($"lease: {lease.Person} on {lease.AccountName}{(lease.GuestOf != null ? $" (guest of {lease.GuestOf})" : "")}");
            }
            Event("saved", new JsonObject { ["saved"] = SavedJson() });

            // the host's server sleeps when nobody plays; logging in woke it, so wait until it's ready
            await WaitForServer(broker, text => Event("status", new JsonObject { ["text"] = text }), cts.Token);
            cts.Token.ThrowIfCancellationRequested(); // last point Back can stop it: after this games start
            _playCts = null;

            Event("phase", new JsonObject { ["phase"] = "starting" });
            var games = new List<GameProcess>();
            var started = leases.ToList();
            for (var i = 0; i < started.Count; i++)
            {
                var lease = started[i];
                current = choices[i];
                // The game connection: through the encrypted tunnel when the host offers it, else straight to Paper.
                var (address, conn) = await GameConnection.Open(broker, hostKey, lease, lease.ServerHost ?? Address().host);
                GameProcess gp;
                int slot;
                lock (_slots) { slot = 1; while (_slots.Contains(slot)) slot++; _slots.Add(slot); } // lowest free games/pN
                try
                {
                    // Fetched right before each launch: in interim mode this is a short-lived one-time join name.
                    var token = await broker.GetMinecraftToken(lease);
                    var size = started.Count > 1 ? windows.SplitSize(i, started.Count) : null; // its own screen area
                    gp = GameLauncher.Launch(game, slot, token, address, size?.width, size?.height);
                }
                catch { conn?.Dispose(); lock (_slots) _slots.Remove(slot); throw; }
                games.Add(gp);
                if (lease.GuestOf == null) RememberedStore.Touch(hostKey, lease.Person);
                _ = Watch(broker, lease, gp, conn);
                leases.Remove(lease); // owned by Watch() now
                if (i < started.Count - 1) await Task.Delay(10_000); // stagger startup: one game boots at a time
            }
            current = null;
            Event("saved", new JsonObject { ["saved"] = SavedJson() });

            Event("phase", new JsonObject { ["phase"] = "booting" });
            var deadline = DateTime.Now.AddMinutes(4);
            while (DateTime.Now < deadline)
            {
                var dead = games.FirstOrDefault(g => g.Proc.HasExited);
                if (dead != null)
                    throw new UiError("java_failed", $"Minecraft closed while starting (exit code {dead.Proc.ExitCode}). Details: {Path.Combine(Paths.GameDir(dead.Slot), "launcher-java.log")}");
                if (games.All(g => g.WindowReady)) break;
                await Task.Delay(1000);
            }
            await windows.Arrange(games);
            Event("phase", new JsonObject { ["phase"] = "ingame" });
            return null;
        }
        catch (BrokerException e) when (e.Code == "no_free_account" && choices.Count > 1)
        {
            // all or nothing: the seats already taken for this launch go back
            foreach (var l in leases) _ = broker.Release(l);
            throw new UiError("no_free_account", $"The server is full: not enough free seats for {choices.Count} players; try fewer.", current?.Name);
        }
        catch (BrokerException e)
        {
            foreach (var l in leases) _ = broker.Release(l);
            throw new UiError(e.Code ?? "error",
                e.Code == "server_closed" ? ClosedMessage(e.Body?["closedUntil"]?.GetValue<string>()) : e.Message, current?.Name);
        }
        catch (UiError)
        {
            foreach (var l in leases) _ = broker.Release(l);
            throw;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            foreach (var l in leases) _ = broker.Release(l);
            throw new UiError("cancelled", "Stopped. Press Play when you're ready.");
        }
        catch (Exception e)
        {
            Log.Error("play failed", e);
            foreach (var l in leases) _ = broker.Release(l);
            // the real message is in the log; the page gets kid-friendly wording
            throw e switch
            {
                HttpRequestException or TimeoutException or TaskCanceledException =>
                    new UiError("download_failed", "Couldn't download the game files. Check the internet and try again.", current?.Name),
                IOException or System.Text.Json.JsonException =>
                    new UiError("download_failed", "Some game files are broken or in use. Close Minecraft if it's open and try again.", current?.Name),
                _ => new UiError("error", "Something went wrong starting the game. Try again; if it keeps happening, tell the host.", current?.Name),
            };
        }
        finally { _playCts = null; _busy = false; DisposeRetired(); }
    }

    /// <summary>Back on the launch screen: stops the download/sign-in part of Play. Once games start it does nothing.</summary>
    private JsonNode? CancelPlay()
    {
        try { _playCts?.Cancel(); } catch (ObjectDisposedException) { /* Play just finished */ }
        return null;
    }

    /// <summary>Keeps the lease alive while the game runs, then gives the account back.</summary>
    private async Task Watch(BrokerClient broker, Lease lease, GameProcess gp, IDisposable? conn)
    {
        Interlocked.Increment(ref _running);
        try
        {
            var proc = gp.Proc;
            while (!proc.HasExited)
            {
                await Task.WhenAny(proc.WaitForExitAsync(), Task.Delay(60_000));
                if (!proc.HasExited) { try { await broker.Heartbeat(lease); } catch { /* server keeps the lease while the plugin sees us online */ } }
            }
            conn?.Dispose();
            await broker.Release(lease);
            Log.Info($"{lease.Person}'s game closed; lease released");
            Event("closed", new JsonObject { ["person"] = lease.Person, ["account"] = lease.AccountName });
        }
        catch (Exception e) { Log.Error("watch failed", e); }
        finally
        {
            conn?.Dispose();
            lock (_slots) _slots.Remove(gp.Slot);
            Interlocked.Decrement(ref _running);
            DisposeRetired();
        }
    }
}
