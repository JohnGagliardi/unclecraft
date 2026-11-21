using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace McShared;

/// <summary>
/// One update check. State: off (no release key built in yet), none, available (Play installs it first),
/// available-dev (dev build or --no-update: shown, never forced), failed (GitHub unreachable or a bad release).
/// </summary>
public sealed record UpdateCheck(string State, ReleaseManifest? Release = null, string? Reason = null);

/// <summary>An update that didn't install. Notice is what the person sees instead of "Update failed, playing on vX".</summary>
public sealed class UpdateException(string message, string? notice = null) : Exception(message)
{
    public string? Notice { get; } = notice;
}

/// <summary>What the window host does while the new version starts on trial.</summary>
public interface IUpdateHost
{
    /// <summary>true: give up the one-copy lock and hide the window. false (rolled back): take the lock back and show it.</summary>
    void Handover(bool toNewVersion);
}

/// <summary>
/// Auto-update from the GitHub Releases of JohnGagliardi/unclecraft: check the signed release.json, download this
/// platform's file, check its size and SHA-256 against it, swap it in and start it on trial. The old copy waits; if the
/// new one doesn't report healthy in 90 s, the old copy is put back and that version is never forced again.
/// </summary>
public static class Updater
{
    // Test seams: only code can change these (never arguments or environment variables).
    internal static string BaseUrl = "https://github.com/JohnGagliardi/unclecraft/releases";
    internal static Func<string?> PublicKey = ReleaseSig.BuiltInPublicKey;
    internal static string InfoVersionFull = ReadInfoVersion();
    internal static Func<string> WindowsTarget = () => File.Exists(InstalledExe) ? InstalledExe : Environment.ProcessPath!;
    internal static Action Exit = () => Environment.Exit(0);

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = MakeHttp();
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (DateTime At, byte[]? Manifest, string? Sig)? _good;
    private static bool _loggedOff, _healthy;
    private const string CantUpdateHere = "Can't update here. Ask the host.";

    /// <summary>The last check's result (null before the first one finishes).</summary>
    public static UpdateCheck? Last { get; private set; }

    /// <summary>"0.2.57" or "0.2.0-dev": the informational version without its +commit part.</summary>
    public static string InfoVersion => InfoVersionFull.Split('+')[0];

    /// <summary>Short commit from the informational version's +metadata (the SDK adds the git sha), or null.</summary>
    public static string? Commit
    {
        get
        {
            var i = InfoVersionFull.IndexOf('+');
            var meta = i < 0 ? "" : InfoVersionFull[(i + 1)..];
            return meta.Length >= 7 && meta.All(Uri.IsHexDigit) ? meta[..7].ToLowerInvariant() : null;
        }
    }

    /// <summary>Key in release.json's files.</summary>
    public static string Rid => Platform.IsMac ? (Platform.IsArm64 ? "osx-arm64" : "osx-x64") : "win-x64";

    public static bool IsDev => ReleaseSig.IsDev(InfoVersionFull);

    /// <summary>The installed Windows copy (see Installer in Program.cs).</summary>
    public static string InstalledExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "UncleCraft", "UncleCraft.exe");

    // ---------------- arguments ----------------

    /// <summary>--update-trial: the version that started this one, which waits for it to report healthy.</summary>
    public static string? TrialFrom { get; private set; }
    public static int TrialParent { get; private set; }
    public static bool IsTrial => TrialFrom != null;
    /// <summary>--no-update: check, but never force (for testing local changes).</summary>
    public static bool NoUpdate { get; private set; }

    public static void ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--no-update") NoUpdate = true;
            else if (args[i] == "--update-trial" && i + 2 < args.Length && int.TryParse(args[i + 2], out var pid))
            {
                TrialFrom = args[i + 1]; TrialParent = pid; i += 2;
            }
        }
        if (IsTrial) Log.Info($"update trial: {InfoVersion}, started by {TrialFrom} (pid {TrialParent})");
    }

    private static string ReadInfoVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    private static HttpClient MakeHttp()
    {
        // Follows GitHub's redirect to its release-files CDN (not the REST API, so no 60-an-hour limit).
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1_000_000 };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"UncleCraft/{InfoVersion}"); // generic: no personal data
        return h;
    }

    // ---------------- check ----------------

    /// <summary>Checks for a newer signed release. A good answer is reused for 10 minutes (memory, then update-check.json).</summary>
    public static async Task<UpdateCheck> Check(bool force = false, CancellationToken ct = default)
    {
        var key = PublicKey();
        if (key == null)
        {
            if (!_loggedOff) { Log.Info("updates are off: no release key built in (UpdateKey.pub.txt)"); _loggedOff = true; }
            return Last = new UpdateCheck("off");
        }
        await Gate.WaitAsync(ct);
        try
        {
            (DateTime At, byte[]? Manifest, string? Sig)? good = force ? null : _good is { } g && Fresh(g.At) ? g : LoadCache(key);
            if (good == null)
            {
                try { good = await Fetch(key, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    var why = e is UpdateException ? e.Message : $"{e.GetType().Name}: {e.Message}";
                    Log.Info($"update check failed: {why}");
                    return Last = new UpdateCheck("failed", Reason: why);
                }
                _good = good;
                SaveCache(good.Value);
            }
            else _good = good;
            return Last = Evaluate(good.Value.Manifest);
        }
        finally { Gate.Release(); }
    }

    private static bool Fresh(DateTime at) => DateTime.UtcNow - at < CacheFor && at < DateTime.UtcNow.AddMinutes(1);

    /// <summary>release.json + .sig from the latest release, verified. Manifest null = no release yet (404).</summary>
    private static async Task<(DateTime, byte[]?, string?)> Fetch(string key, CancellationToken ct)
    {
        var json = await Get($"{BaseUrl}/latest/download/release.json", ct);
        if (json == null) { Log.Info("update check: no release published yet"); return (DateTime.UtcNow, null, null); }
        // A release published between these two requests fails the signature: it's tried again next time.
        var sigBytes = await Get($"{BaseUrl}/latest/download/release.json.sig", ct) ?? throw new UpdateException("the release has no release.json.sig");
        var sig = Encoding.ASCII.GetString(sigBytes);
        if (!ReleaseSig.Verify(json, sig, key)) throw new UpdateException("release.json's signature doesn't match the built-in key");
        ReleaseManifest m;
        try { m = ReleaseManifest.Parse(json); }
        catch (Exception e) { throw new UpdateException($"release.json doesn't parse ({e.Message})"); }
        if (m.App != Platform.AppName) throw new UpdateException($"release.json is for {m.App}, not {Platform.AppName}");
        if (ReleaseSig.ParseVersion(m.Version) == null) throw new UpdateException($"release.json has a bad version {m.Version}");
        return (DateTime.UtcNow, json, sig);
    }

    private static async Task<byte[]?> Get(string url, CancellationToken ct)
    {
        using var res = await Http.GetAsync(url, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw new UpdateException($"GitHub answered HTTP {(int)res.StatusCode}");
        return await res.Content.ReadAsByteArrayAsync(ct);
    }

    private static UpdateCheck Evaluate(byte[]? json)
    {
        if (json == null) return new UpdateCheck("none");
        var m = ReleaseManifest.Parse(json);
        if (!m.Files.ContainsKey(Rid)) return new UpdateCheck("none", m, $"release {m.Version} has no {Rid} file");
        var mine = ReleaseSig.ParseVersion(InfoVersion) ?? new Version(0, 0, 0);
        if (ReleaseSig.ParseVersion(m.Version) is not { } v || v <= mine) return new UpdateCheck("none", m);
        if (BadVersions().Contains(m.Version)) return new UpdateCheck("none", m, $"{m.Version} didn't start on this computer before");
        return new UpdateCheck(IsDev || NoUpdate ? "available-dev" : "available", m);
    }

    private static string CacheFile => Path.Combine(Paths.Root, "update-check.json");

    /// <summary>The last good check from the file, if fresh; its manifest is verified again (the file isn't trusted).</summary>
    private static (DateTime, byte[]?, string?)? LoadCache(string key)
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var j = JsonNode.Parse(File.ReadAllText(CacheFile))!;
            var at = DateTime.Parse(j["checked"]!.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
            if (!Fresh(at)) return null;
            if (j["manifest"]?.GetValue<string>() is not { } b64) return (at, null, null);
            var json = Convert.FromBase64String(b64);
            var sig = j["sig"]!.GetValue<string>();
            return ReleaseSig.Verify(json, sig, key) ? (at, json, sig) : null;
        }
        catch { return null; }
    }

    private static void SaveCache((DateTime At, byte[]? Manifest, string? Sig) c)
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            var j = new JsonObject
            {
                ["checked"] = c.At.ToString("o"),
                ["manifest"] = c.Manifest == null ? null : Convert.ToBase64String(c.Manifest),
                ["sig"] = c.Sig,
            };
            File.WriteAllText(CacheFile + ".part", j.ToJsonString());
            File.Move(CacheFile + ".part", CacheFile, true);
        }
        catch (Exception e) { Log.Info($"couldn't save update-check.json: {e.Message}"); }
    }

    private static string BadFile => Path.Combine(Paths.Root, "update-bad.json");

    /// <summary>Versions that didn't start here: never forced again (a newer release is).</summary>
    private static HashSet<string> BadVersions()
    {
        try { return File.Exists(BadFile) ? JsonNode.Parse(File.ReadAllText(BadFile))!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet() : []; }
        catch { return []; }
    }

    private static void MarkBad(string version)
    {
        try
        {
            var a = new JsonArray();
            foreach (var v in BadVersions().Append(version).Distinct()) a.Add(v);
            File.WriteAllText(BadFile, a.ToJsonString());
        }
        catch (Exception e) { Log.Error("couldn't write update-bad.json", e); }
    }

    // ---------------- download ----------------

    /// <summary>Downloads this platform's file from the release's own tag (not "latest") and checks it against the signed manifest.</summary>
    public static async Task<string> Download(ReleaseManifest m, Action<long, long> progress, CancellationToken ct)
    {
        var f = m.Files[Rid];
        if (f.Name.Length == 0 || Path.GetFileName(f.Name) != f.Name || f.Name.StartsWith('.') || f.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new UpdateException($"the release has a bad file name {f.Name}");
        var dir = Path.Combine(Paths.Root, "update");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, f.Name);
        var part = dest + ".part";
        var url = $"{BaseUrl}/download/v{Uri.EscapeDataString(m.Version)}/{Uri.EscapeDataString(f.Name)}";
        Log.Info($"update: downloading {m.Version} ({f.Size} bytes) from {url}");
        try
        {
            using (var res = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!res.IsSuccessStatusCode) throw new UpdateException($"the download answered HTTP {(int)res.StatusCode}");
                long done = 0;
                await using var fs = File.Create(part);
                await using var src = await res.Content.ReadAsStreamAsync(ct);
                await IdleCopy.CopyAsync(src, fs, ct, n =>
                {
                    done += n;
                    if (done > f.Size) throw new UpdateException("the download is bigger than the signed release says");
                    progress(done, f.Size);
                });
            }
            string hash;
            using (var fs = File.OpenRead(part))
            {
                if (fs.Length != f.Size) throw new UpdateException($"the download is {fs.Length} bytes, the signed release says {f.Size}");
                hash = Convert.ToHexString(SHA256.HashData(fs));
            }
            if (!hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("the download's SHA-256 doesn't match the signed release");
            File.Move(part, dest, true);
            Log.Info($"update: {f.Name} verified");
            return dest;
        }
        catch
        {
            try { File.Delete(part); } catch { /* ignore */ }
            throw;
        }
    }

    // ---------------- swap, trial, rollback ----------------

    /// <summary>
    /// Puts the verified download in place and starts it with --update-trial. When it reports healthy this process
    /// exits; otherwise the old copy is put back, the version goes in update-bad.json and this throws UpdateException.
    /// The caller makes sure no game is running.
    /// </summary>
    public static Task Apply(ReleaseManifest m, string file, IUpdateHost? host) =>
        Platform.IsMac ? ApplyMac(m, file, host) : ApplyWindows(m, file, host);

    private static async Task ApplyWindows(ReleaseManifest m, string file, IUpdateHost? host)
    {
        // The installed copy is updated even when this one runs from elsewhere (Downloads, dist); without one, this exe.
        var target = WindowsTarget();
        var old = target + ".old";
        var staged = target + ".new";
        File.Move(file, staged, true);
        File.Delete(old); // a running exe can be renamed but not deleted: .old is only deleted on a later start
        File.Move(target, old);
        try { File.Move(staged, target); }
        catch { File.Move(old, target); throw; }
        Log.Info($"update: {target} is now {m.Version} (old copy kept as .old)");
        await Trial(m.Version, host, () =>
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
            foreach (var a in TrialArgs()) psi.ArgumentList.Add(a);
            return Process.Start(psi);
        }, async () => await Retry(() => File.Move(old, target, true)));
    }

    private static async Task ApplyMac(ReleaseManifest m, string zip, IUpdateHost? host)
    {
        var bundle = MacBundle() ?? throw new UpdateException("not running from an .app bundle", CantUpdateHere);
        var parent = Path.GetDirectoryName(bundle)!;
        if (!Writable(parent)) throw new UpdateException($"{parent} isn't writable", CantUpdateHere);
        var staging = bundle + ".new";
        var old = bundle + ".old";
        DeleteDir(staging); DeleteDir(old);
        Directory.CreateDirectory(staging);
        try
        {
            if (await Run("ditto", "-x", "-k", zip, staging) != 0) throw new UpdateException("ditto couldn't unpack the download");
            var app = Directory.GetDirectories(staging, "*.app").SingleOrDefault() ?? throw new UpdateException("the download has no .app inside");
            await Run("xattr", "-dr", "com.apple.quarantine", app); // to be safe: our own download has no quarantine flag
            Directory.Move(bundle, old);
            try { Directory.Move(app, bundle); }
            catch { Directory.Move(old, bundle); throw; }
        }
        finally { DeleteDir(staging); try { File.Delete(zip); } catch { /* ignore */ } }
        Log.Info($"update: {bundle} is now {m.Version} (old copy kept as .old)");
        await Trial(m.Version, host, () =>
        {
            var psi = new ProcessStartInfo("open") { UseShellExecute = false };
            foreach (var a in new[] { "-n", bundle, "--args" }.Concat(TrialArgs())) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose(); // `open` exits at once: the new app reports its own pid (TrialStarted)
            return null;
        }, () => { DeleteDir(bundle); Directory.Move(old, bundle); return Task.CompletedTask; });
    }

    private static string[] TrialArgs() => ["--update-trial", InfoVersion, Environment.ProcessId.ToString()];

    private static async Task Trial(string version, IUpdateHost? host, Func<Process?> start, Func<Task> rollback)
    {
        var marker = HealthyMarker(version);
        var pidFile = PidFile(version);
        try { File.Delete(marker); File.Delete(pidFile); } catch { /* ignore */ }
        host?.Handover(true);
        Process? child = null;
        try
        {
            child = start();
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(marker))
                {
                    Log.Info($"update: {version} is healthy; closing {InfoVersion}");
                    Exit();
                    return;
                }
                child ??= FromPidFile(pidFile);
                if (child is { HasExited: true }) { Log.Info($"update: {version} closed during its trial (exit code {child.ExitCode})"); break; }
                await Task.Delay(500);
            }
        }
        catch (Exception e) { Log.Error($"update: couldn't start {version}", e); }

        Log.Info($"update: {version} didn't report healthy; putting {InfoVersion} back");
        try
        {
            child ??= FromPidFile(pidFile);
            if (child is { HasExited: false }) { child.Kill(true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        }
        catch (Exception e) { Log.Error("update: couldn't stop the new version", e); }
        try { await rollback(); }
        catch (Exception e) { Log.Error("update: putting the old version back failed", e); }
        MarkBad(version);
        try { File.Delete(marker); File.Delete(pidFile); } catch { /* ignore */ }
        host?.Handover(false);
        throw new UpdateException($"{version} didn't start", "The update didn't start, so UncleCraft kept the old version.");
    }

    private static string HealthyMarker(string version) => Path.Combine(Paths.Root, $"update-healthy-{ReleaseSig.ParseVersion(version)}");
    private static string PidFile(string version) => Path.Combine(Paths.Root, $"update-trial-{ReleaseSig.ParseVersion(version)}.pid");

    private static Process? FromPidFile(string pidFile)
    {
        try { return File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) ? Process.GetProcessById(pid) : null; }
        catch (ArgumentException) { return null; } // already gone (HasExited can't be told apart: the deadline handles it)
        catch (IOException) { return null; }
    }

    /// <summary>Trial side, right after taking the one-copy lock: lets the old copy find (and if needed stop) this one.</summary>
    public static void TrialStarted()
    {
        if (!IsTrial) return;
        try { Directory.CreateDirectory(Paths.Root); File.WriteAllText(PidFile(InfoVersion), Environment.ProcessId.ToString()); }
        catch (Exception e) { Log.Error("update trial: couldn't write the pid file", e); }
    }

    /// <summary>Trial side: the window is up and the host answered (or 45 s passed): the old copy may close.</summary>
    public static void MarkHealthy()
    {
        if (!IsTrial || _healthy) return;
        _healthy = true;
        try { File.WriteAllText(HealthyMarker(InfoVersion), DateTime.UtcNow.ToString("o")); Log.Info($"update trial: {InfoVersion} is healthy"); }
        catch (Exception e) { Log.Error("update trial: couldn't write the healthy marker", e); }
    }

    /// <summary>Normal start: removes what earlier updates left behind (old copy, download folder, markers).</summary>
    public static void CleanUp()
    {
        try
        {
            if (Directory.Exists(Paths.Root))
                foreach (var f in Directory.EnumerateFiles(Paths.Root, "update-healthy-*").Concat(Directory.EnumerateFiles(Paths.Root, "update-trial-*.pid")).ToList())
                    File.Delete(f);
            DeleteDir(Path.Combine(Paths.Root, "update"));
            if (Platform.IsMac)
            {
                if (MacBundle() is { } b) { DeleteDir(b + ".old"); DeleteDir(b + ".new"); }
            }
            else
                foreach (var exe in new[] { InstalledExe, Environment.ProcessPath ?? "" }.Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                    foreach (var f in new[] { exe + ".old", exe + ".new" })
                        if (File.Exists(f)) File.Delete(f);
        }
        catch (Exception e) { Log.Info($"update cleanup: {e.Message}"); }
    }

    /// <summary>The running .app bundle (walking up from Contents/MacOS), or null when not run from one.</summary>
    private static string? MacBundle()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/', '\\')); d != null; d = d.Parent)
            if (d.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return d.FullName;
        return null;
    }

    private static bool Writable(string dir)
    {
        try
        {
            var t = Path.Combine(dir, $".unclecraft-write-test-{Environment.ProcessId}");
            File.WriteAllText(t, "");
            File.Delete(t);
            return true;
        }
        catch { return false; }
    }

    private static void DeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception e) { Log.Info($"couldn't delete {d}: {e.Message}"); } }

    private static async Task<int> Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) Log.Info($"{file} exited {p.ExitCode}: {(await err).Trim()}");
        return p.ExitCode;
    }

    /// <summary>A just-killed exe can stay locked for a moment.</summary>
    private static async Task Retry(Action a)
    {
        for (var i = 0; ; i++)
        {
            try { a(); return; }
            catch (Exception e) when (i < 20 && e is IOException or UnauthorizedAccessException) { await Task.Delay(500); }
        }
    }
}
