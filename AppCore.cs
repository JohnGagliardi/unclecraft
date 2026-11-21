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
    /// <summary>Size for each game window in split-screen (half the screen), or null to let the game choose.</summary>
    (int width, int height)? SplitSize();
    /// <summary>Called once all game windows are up.</summary>
    Task Arrange(IReadOnlyList<GameProcess> games);
}

/// <summary>
/// Everything behind the pixel launcher page, shared by the Windows (WebView2) and macOS (Photino) hosts.
/// Page -> host: JSON {id, type, ...}. Host -> page: {id, ok, data|error} replies and {event, ...} events.
/// </summary>
public sealed class AppCore(Action<string> post, IGameWindows windows)
{
    private readonly Settings _s = Settings.Load();
    private BrokerClient? _broker;
    private string? _brokerKey;
    private bool _busy;
    private CancellationTokenSource? _playCts;
    private int _running;

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
        if (_broker == null || _brokerKey != id) { _broker?.Dispose(); _broker = new BrokerClient(host, port, key); _brokerKey = id; }
        return _broker;
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
        ["split"] = _s.SecondPlayer,
    };

    private JsonNode Init()
    {
        Log.Info($"{Platform.AppName} {Version} on {Platform.Describe()}; data {Paths.Root}");
        return new JsonObject
        {
            ["configured"] = HostKey() != null && _s.Server.Trim().Length > 0,
            ["settings"] = SettingsJson(),
            ["saved"] = SavedJson(),
            ["version"] = Version,
            ["platform"] = Platform.Name,
            ["logPath"] = Log.FilePath,
        };
    }

    private static string Version => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";

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
        _s.Server = server; _s.Fingerprint = FormatFp(key); _s.Save();
        _brokerKey = null;
        Log.Info($"setup ok: host {addr.host}:{addr.port}, mode {st["mode"]}");
        return new JsonObject
        {
            ["settings"] = SettingsJson(), ["saved"] = SavedJson(),
            ["testMode"] = st["testMode"]?.GetValue<bool>() ?? false,
            ["free"] = st["accounts"]?["free"]?.GetValue<int>() ?? 0,
            ["total"] = st["accounts"]?["total"]?.GetValue<int>() ?? 0,
        };
    }

    private async Task<JsonNode> Status() => await Broker().GetStatus();

    /// <summary>
    /// Waits while the host's game server is waking up (it sleeps after 20 minutes with nobody playing, and logging in
    /// wakes it). Older brokers don't report it: then this returns at once.
    /// </summary>
    private async Task WaitForServer(BrokerClient broker)
    {
        var deadline = DateTime.Now.AddMinutes(3);
        var told = false;
        while (true)
        {
            JsonNode st;
            try { st = await broker.GetStatus(); }
            catch (BrokerException) { return; }
            var paper = st["paper"]?.GetValue<string>();
            if (paper is null or "up")
            {
                if (told) Log.Info("server is awake");
                return;
            }
            if (!told)
            {
                Log.Info($"server is {paper}: waiting for it to wake up");
                Event("status", new JsonObject { ["text"] = "Waking up the server. This takes about 30 seconds..." });
                told = true;
            }
            if (DateTime.Now > deadline)
                throw new UiError("server_asleep", "The server didn't wake up in time. Try again in a minute, or ask the host.");
            await Task.Delay(2000);
        }
    }

    private JsonNode? SetSound(JsonObject m) { _s.SoundOn = m["on"]?.GetValue<bool>() ?? true; _s.Save(); return null; }
    private JsonNode? SetMode(JsonObject m) { _s.SecondPlayer = m["split"]?.GetValue<bool>() ?? false; _s.Save(); return null; }

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
        var broker = Broker();
        var hostKey = HostKey()!;
        var choices = m["players"]!.AsArray().Select(n => ParseChoice(n!, hostKey)).ToList();
        if (choices.Count == 2 && choices[0].Name.Equals(choices[1].Name, StringComparison.OrdinalIgnoreCase))
            throw new UiError("error", "The two screens need two different people.");

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
                var lease = await Logins.LoginAs(broker, c, hostKey);
                leases.Add(lease);
                Log.Info($"lease: {lease.Person} on {lease.AccountName}");
            }
            Event("saved", new JsonObject { ["saved"] = SavedJson() });

            // the host's server sleeps when nobody plays; logging in woke it, so wait until it's ready
            await WaitForServer(broker);
            cts.Token.ThrowIfCancellationRequested(); // last point Back can stop it: after this games start
            _playCts = null;

            Event("phase", new JsonObject { ["phase"] = "starting" });
            var size = leases.Count > 1 ? windows.SplitSize() : null;
            var games = new List<GameProcess>();
            var started = leases.ToList();
            for (var i = 0; i < started.Count; i++)
            {
                var lease = started[i];
                current = choices[i];
                // The game connection: through the encrypted tunnel when the host offers it, else straight to Paper.
                var (address, conn) = await GameConnection.Open(broker, hostKey, lease, lease.ServerHost ?? Address().host);
                GameProcess gp;
                try
                {
                    // Fetched right before each launch: in interim mode this is a short-lived one-time join name.
                    var token = await broker.GetMinecraftToken(lease);
                    gp = GameLauncher.Launch(game, i + 1, token, address, size?.width, size?.height);
                }
                catch { conn?.Dispose(); throw; }
                games.Add(gp);
                RememberedStore.Touch(hostKey, lease.Person);
                _ = Watch(broker, lease, gp, conn);
                leases.Remove(lease); // owned by Watch() now
                if (i == 0 && started.Count > 1) await Task.Delay(10_000); // stagger startup
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
        catch (BrokerException e)
        {
            foreach (var l in leases) _ = broker.Release(l);
            throw new UiError(e.Code ?? "error", e.Message, current?.Name);
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
            throw new UiError("error", e.Message, current?.Name);
        }
        finally { _playCts = null; _busy = false; }
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
        finally { conn?.Dispose(); Interlocked.Decrement(ref _running); }
    }
}
