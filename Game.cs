using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace McShared;

public static class Paths
{
    // Windows: %LOCALAPPDATA%\mc-shared (keeps the old name: settings, game files, remembered logins).
    // macOS: ~/Library/Application Support/UncleCraft, logs in ~/Library/Logs/UncleCraft.
    public static readonly string Root = Platform.IsMac
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "UncleCraft")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mc-shared");
    public static readonly string Logs = Platform.IsMac
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "UncleCraft")
        : Root;
    public static string Libraries => Path.Combine(Root, "libraries");
    public static string Assets => Path.Combine(Root, "assets");
    public static string Versions => Path.Combine(Root, "versions");
    public static string Runtime => Path.Combine(Root, "runtime");
    public static string ModCache => Path.Combine(Root, "mod-cache");
    /// <summary>Each player slot on this PC has its own game folder (options, Controllable config, logs).</summary>
    public static string GameDir(int slot) => Path.Combine(Root, "games", $"p{slot}");
    public static string Settings => Path.Combine(Root, "settings.json");
}

/// <summary>Everything needed to start the game, resolved once by the installer.</summary>
public sealed class InstalledGame
{
    public required string VersionId { get; init; }
    public required string JavaExe { get; init; }
    public required string MainClass { get; init; }
    public required List<string> Classpath { get; init; }
    public required JsonArray VanillaGameArgs { get; init; }
    public required JsonArray VanillaJvmArgs { get; init; }
    public required List<string> FabricJvmArgs { get; init; }
    public required List<string> FabricGameArgs { get; init; }
    public required string AssetIndex { get; init; }
    public string? LoggingArg { get; init; }
    public required List<ModInfo> Mods { get; init; }
    public bool TestMode { get; init; }
}

/// <summary>
/// Stream copy that gives up when no bytes arrive for a while: HttpClient.Timeout stops applying to the body once
/// ResponseHeadersRead returns, so a stalled download would otherwise wait forever. Throws TimeoutException.
/// </summary>
internal static class IdleCopy
{
    public static async Task CopyAsync(Stream src, Stream dest, CancellationToken ct, Action<long>? onBytes = null, int idleSeconds = 30)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buf = new byte[81920];
        while (true)
        {
            idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds));
            int n;
            try { n = await src.ReadAsync(buf, idle.Token); }
            catch (Exception e) when (!ct.IsCancellationRequested && idle.IsCancellationRequested && e is OperationCanceledException or IOException)
            { throw new TimeoutException($"The download stalled (no data for {idleSeconds} seconds)."); }
            if (n == 0) return;
            await dest.WriteAsync(buf.AsMemory(0, n), ct);
            onBytes?.Invoke(n);
        }
    }
}

/// <summary>
/// "jar name -> sha256" for one mods folder (sha256.json beside the jars), written after each verified download or copy,
/// so a launch trusts size + recorded hash without re-hashing every jar. A jar with the right size but no record
/// (folders from before this file existed) is hashed once and recorded.
/// </summary>
internal sealed class ModHashes
{
    private readonly string _dir, _path;
    private readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    public ModHashes(string dir)
    {
        _dir = dir;
        _path = Path.Combine(dir, "sha256.json");
        try
        {
            if (File.Exists(_path) && JsonNode.Parse(File.ReadAllText(_path)) is JsonObject o)
                foreach (var (k, v) in o) if (v is JsonValue s && s.TryGetValue<string>(out var h)) _map[k] = h;
        }
        catch (System.Text.Json.JsonException) { _map.Clear(); } // damaged: jars get re-checked
    }

    public bool IsCurrent(ModInfo m)
    {
        var f = Path.Combine(_dir, m.File);
        if (!File.Exists(f) || new FileInfo(f).Length != m.Size) return false;
        if (_map.TryGetValue(m.File, out var h)) return h.Equals(m.Sha256, StringComparison.OrdinalIgnoreCase);
        using (var fs = File.OpenRead(f))
            if (!Convert.ToHexString(SHA256.HashData(fs)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
        Set(m);
        return true;
    }

    public void Set(ModInfo m) { _map[m.File] = m.Sha256; Save(); }
    public void Remove(string file) { if (_map.Remove(file)) Save(); }

    private void Save()
    {
        var o = new JsonObject();
        foreach (var (k, v) in _map) o[k] = v;
        File.WriteAllText(_path + ".part", o.ToJsonString());
        File.Move(_path + ".part", _path, true);
    }
}

/// <summary>Downloads Minecraft (Mojang's public manifests), Mojang's Java runtime, Fabric and the host's mods.</summary>
/// <summary>Download progress for one stage ("Java", "Minecraft", "Fabric", "Mods"). Done = stage finished.</summary>
public sealed record InstallProgress(string Stage, long DoneBytes, long TotalBytes, int DoneFiles, int TotalFiles, bool Done);

public sealed class GameInstaller(BrokerClient broker, Action<string> status, Action<InstallProgress> progress)
{
    public static readonly string[] Stages = ["Java", "Minecraft", "Fabric", "Mods"];
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private const string VersionManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string JavaRuntimeIndex = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";
    private const string AssetBase = "https://resources.download.minecraft.net/";

    private sealed record Download(string Url, string Dest, string? Sha1, long Size, string Stage = "Minecraft");

    public async Task<InstalledGame> Install(CancellationToken ct)
    {
        status("Asking the host which game version to use...");
        var manifest = await broker.GetManifest(ct);
        var ver = manifest.MinecraftVersion;

        status($"Reading Minecraft {ver} details from Mojang...");
        var verDir = Path.Combine(Paths.Versions, ver);
        var verJsonPath = Path.Combine(verDir, ver + ".json");
        try
        {
            var vm = JsonNode.Parse(await Http.GetStringAsync(VersionManifest, ct))!;
            var entry = vm["versions"]!.AsArray().FirstOrDefault(v => v!["id"]!.GetValue<string>() == ver)
                ?? throw new Exception($"Mojang has no Minecraft version {ver}.");
            await Fetch(new Download(entry["url"]!.GetValue<string>(), verJsonPath, entry["sha1"]?.GetValue<string>(), -1), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && File.Exists(verJsonPath))
        {
            // Mojang unreachable: the version details we already have are enough to play
            Log.Info($"Mojang version manifest failed ({e.Message}); using the saved {ver}.json");
        }
        var vj = JsonNode.Parse(await File.ReadAllTextAsync(verJsonPath, ct))!;

        status($"Reading Fabric {manifest.FabricLoaderVersion}...");
        var fabricPath = Path.Combine(Paths.Versions, $"fabric-{manifest.FabricLoaderVersion}-{ver}.json");
        var fabric = await SavedJson(fabricPath, async () =>
        {
            Directory.CreateDirectory(Paths.Versions);
            var fj = await Http.GetStringAsync($"https://meta.fabricmc.net/v2/versions/loader/{ver}/{manifest.FabricLoaderVersion}/profile/json", ct);
            await File.WriteAllTextAsync(fabricPath + ".part", fj, ct);
            File.Move(fabricPath + ".part", fabricPath, true);
        }, ct);

        var downloads = new List<Download>();

        // client jar
        var clientJar = Path.Combine(verDir, ver + ".jar");
        var cd = vj["downloads"]!["client"]!;
        downloads.Add(new Download(cd["url"]!.GetValue<string>(), clientJar, cd["sha1"]!.GetValue<string>(), cd["size"]!.GetValue<long>()));

        // libraries: Fabric's win over vanilla's with the same group:artifact:classifier
        var libs = new Dictionary<string, string>();
        var order = new List<string>();
        foreach (var lib in fabric["libraries"]!.AsArray())
        {
            var name = lib!["name"]!.GetValue<string>();
            var rel = MavenPath(name);
            var dest = Path.Combine(Paths.Libraries, rel);
            downloads.Add(new Download(lib["url"]!.GetValue<string>().TrimEnd('/') + "/" + rel.Replace(Path.DirectorySeparatorChar, '/'), dest, lib["sha1"]?.GetValue<string>(), lib["size"]?.GetValue<long>() ?? -1, "Fabric"));
            var key = LibKey(name);
            if (libs.TryAdd(key, dest)) order.Add(key);
        }
        foreach (var lib in vj["libraries"]!.AsArray())
        {
            if (lib!["rules"] is JsonArray rules && !RulesAllow(rules, null)) continue;
            var art = lib["downloads"]?["artifact"];
            if (art == null) continue;
            var key = LibKey(lib["name"]!.GetValue<string>());
            if (libs.ContainsKey(key)) continue;
            var dest = Path.Combine(Paths.Libraries, Local(art["path"]!.GetValue<string>()));
            downloads.Add(new Download(art["url"]!.GetValue<string>(), dest, art["sha1"]?.GetValue<string>(), art["size"]?.GetValue<long>() ?? -1));
            libs[key] = dest; order.Add(key);
        }

        // logging config
        string? loggingArg = null;
        var log = vj["logging"]?["client"];
        if (log != null)
        {
            var lf = log["file"]!;
            var dest = Path.Combine(Paths.Assets, "log_configs", lf["id"]!.GetValue<string>());
            downloads.Add(new Download(lf["url"]!.GetValue<string>(), dest, lf["sha1"]?.GetValue<string>(), lf["size"]?.GetValue<long>() ?? -1));
            loggingArg = log["argument"]!.GetValue<string>().Replace("${path}", dest);
        }

        // assets
        var ai = vj["assetIndex"]!;
        var assetIndexId = ai["id"]!.GetValue<string>();
        var indexPath = Path.Combine(Paths.Assets, "indexes", assetIndexId + ".json");
        await Fetch(new Download(ai["url"]!.GetValue<string>(), indexPath, ai["sha1"]?.GetValue<string>(), ai["size"]?.GetValue<long>() ?? -1), ct);
        var index = JsonNode.Parse(await File.ReadAllTextAsync(indexPath, ct))!;
        foreach (var (_, obj) in index["objects"]!.AsObject())
        {
            var hash = obj!["hash"]!.GetValue<string>();
            downloads.Add(new Download(AssetBase + hash[..2] + "/" + hash, Path.Combine(Paths.Assets, "objects", hash[..2], hash), hash, obj["size"]!.GetValue<long>()));
        }

        // Java runtime from Mojang
        var component = vj["javaVersion"]?["component"]?.GetValue<string>() ?? "java-runtime-epsilon";
        var runtimeDir = Path.Combine(Paths.Runtime, component);
        var javaExe = Path.Combine(runtimeDir, Local(Platform.JavaExeRelative));
        var rtKey = Platform.JavaRuntimeKey;
        var rtManifestPath = Path.Combine(Paths.Runtime, rtKey == "windows-x64" ? component + ".manifest.json" : $"{component}.{rtKey}.manifest.json");
        var rtFiles = (await SavedJson(rtManifestPath, async () =>
        {
            status("Finding the Java runtime...");
            var all = JsonNode.Parse(await Http.GetStringAsync(JavaRuntimeIndex, ct))!;
            var rt = all[rtKey]?[component]?.AsArray().FirstOrDefault()
                ?? throw new Exception($"Mojang has no Java runtime '{component}' for {rtKey}.");
            var m = rt["manifest"]!;
            await Fetch(new Download(m["url"]!.GetValue<string>(), rtManifestPath, m["sha1"]?.GetValue<string>(), m["size"]?.GetValue<long>() ?? -1, "Java"), ct);
        }, ct))["files"]!.AsObject();
        var executables = new List<string>();
        var links = new List<(string path, string target)>();
        foreach (var (rel, f) in rtFiles)
        {
            var type = f!["type"]?.GetValue<string>();
            var dest = Path.Combine(runtimeDir, Local(rel));
            if (type == "link") { links.Add((dest, f["target"]!.GetValue<string>())); continue; }
            if (type != "file") continue;
            var raw = f["downloads"]!["raw"]!;
            downloads.Add(new Download(raw["url"]!.GetValue<string>(), dest, raw["sha1"]?.GetValue<string>(), raw["size"]?.GetValue<long>() ?? -1, "Java"));
            if (f["executable"]?.GetValue<bool>() == true) executables.Add(dest);
        }
        Log.Info($"Minecraft {ver}, Java runtime {component} ({rtKey}), {downloads.Count} files known");

        // fetch stage by stage (existing files with the right size are kept), with byte progress
        foreach (var stage in Stages.Where(s => s != "Mods"))
        {
            var todo = downloads.Where(d => d.Stage == stage && !IsPresent(d)).ToList();
            var total = todo.Sum(d => Math.Max(0, d.Size));
            if (todo.Count == 0) { progress(new InstallProgress(stage, 0, 0, 0, 0, true)); continue; }
            status($"Downloading {stage} ({todo.Count} files)...");
            long doneBytes = 0; var doneFiles = 0; var lastReport = 0L;
            void Report(bool force = false)
            {
                var now = Environment.TickCount64;
                if (!force && now - Interlocked.Read(ref lastReport) < 150) return;
                Interlocked.Exchange(ref lastReport, now);
                progress(new InstallProgress(stage, Interlocked.Read(ref doneBytes), total, doneFiles, todo.Count, false));
            }
            Report(true);
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (d, c) =>
            {
                await Fetch(d, c, n => { Interlocked.Add(ref doneBytes, n); Report(); });
                Interlocked.Increment(ref doneFiles);
                Report();
            });
            progress(new InstallProgress(stage, total, total, todo.Count, todo.Count, true));
            Log.Info($"{stage}: downloaded {todo.Count} files ({total / 1e6:F1} MB)");
        }

        // macOS/Linux: Mojang's runtime needs its executable bits and symlinks (Windows ignores both).
        if (!OperatingSystem.IsWindows())
        {
            foreach (var exe in executables)
                File.SetUnixFileMode(exe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            foreach (var (path, target) in links)
            {
                if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget != null) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.CreateSymbolicLink(path, target);
            }
            if (!File.Exists(javaExe)) throw new Exception($"Java was not installed correctly (missing {javaExe}).");
        }

        // the host's mods
        Directory.CreateDirectory(Paths.ModCache);
        var cached = new ModHashes(Paths.ModCache);
        var modTodo = manifest.Mods.Where(m => !cached.IsCurrent(m)).ToList();
        var modTotal = modTodo.Sum(m => m.Size);
        long modDone = 0;
        for (var i = 0; i < modTodo.Count; i++)
        {
            progress(new InstallProgress("Mods", modDone, modTotal, i, modTodo.Count, false));
            status($"Downloading mod {modTodo[i].File} from the host...");
            await broker.DownloadMod(modTodo[i], Path.Combine(Paths.ModCache, modTodo[i].File), ct); // checks the sha256
            cached.Set(modTodo[i]);
            modDone += modTodo[i].Size;
        }
        progress(new InstallProgress("Mods", modTotal, modTotal, modTodo.Count, modTodo.Count, true));
        status("Game files are ready.");

        var classpath = order.Select(k => libs[k]).ToList();
        classpath.Add(clientJar);
        return new InstalledGame
        {
            VersionId = ver,
            JavaExe = javaExe,
            MainClass = fabric["mainClass"]?.GetValue<string>() ?? vj["mainClass"]!.GetValue<string>(),
            Classpath = classpath,
            VanillaGameArgs = vj["arguments"]!["game"]!.AsArray(),
            VanillaJvmArgs = vj["arguments"]!["jvm"]!.AsArray(),
            FabricJvmArgs = StringArgs(fabric["arguments"]?["jvm"]),
            FabricGameArgs = StringArgs(fabric["arguments"]?["game"]),
            AssetIndex = assetIndexId,
            LoggingArg = loggingArg,
            Mods = manifest.Mods,
            TestMode = manifest.TestMode,
        };
    }

    private static List<string> StringArgs(JsonNode? n) =>
        n is JsonArray a ? a.Where(x => x is JsonValue).Select(x => x!.GetValue<string>()).ToList() : [];

    /// <summary>A small JSON file saved by an earlier run: fetched when missing, and fetched again once if it is cut off.</summary>
    private static async Task<JsonNode> SavedJson(string path, Func<Task> fetch, CancellationToken ct)
    {
        if (!File.Exists(path)) await fetch();
        try { return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!; }
        catch (System.Text.Json.JsonException e)
        {
            Log.Info($"{Path.GetFileName(path)} is damaged ({e.Message}); downloading it again");
            File.Delete(path);
            await fetch();
            return JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!;
        }
    }

    private static bool IsPresent(Download d) => File.Exists(d.Dest) && (d.Size < 0 || new FileInfo(d.Dest).Length == d.Size);

    private static async Task Fetch(Download d, CancellationToken ct, Action<long>? onBytes = null)
    {
        if (IsPresent(d) && d.Size >= 0) return;
        if (d.Size < 0 && File.Exists(d.Dest) && d.Sha1 != null && Sha1(d.Dest) == d.Sha1) return;
        Directory.CreateDirectory(Path.GetDirectoryName(d.Dest)!);
        var tmp = d.Dest + ".part";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var res = await Http.GetAsync(d.Url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    res.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmp);
                    await using var src = await res.Content.ReadAsStreamAsync(ct);
                    await IdleCopy.CopyAsync(src, fs, ct, onBytes);
                }
                if (d.Sha1 != null && Sha1(tmp) != d.Sha1) throw new IOException($"checksum mismatch for {Path.GetFileName(d.Dest)}");
                File.Move(tmp, d.Dest, true);
                return;
            }
            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested) { await Task.Delay(1000 * attempt, ct); }
        }
    }

    private static string Sha1(string file)
    {
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }

    private static string MavenPath(string name)
    {
        var p = name.Split(':');
        var file = p.Length > 3 ? $"{p[1]}-{p[2]}-{p[3]}.jar" : $"{p[1]}-{p[2]}.jar";
        return Path.Combine([.. p[0].Split('.'), p[1], p[2], file]);
    }

    /// <summary>A '/'-separated path from Mojang's JSON as a local relative path.</summary>
    private static string Local(string rel) => rel.Replace('/', Path.DirectorySeparatorChar);

    private static string LibKey(string name)
    {
        var p = name.Split(':');
        return p.Length > 3 ? $"{p[0]}:{p[1]}:{p[3]}" : $"{p[0]}:{p[1]}";
    }

    /// <summary>Mojang argument/library rules for this OS (windows / osx) and 64-bit CPU, with the given launcher features.</summary>
    public static bool RulesAllow(JsonArray rules, IReadOnlyDictionary<string, bool>? features)
    {
        var allowed = false;
        foreach (var r in rules)
        {
            var match = true;
            if (r!["os"] is JsonObject os)
            {
                if (os["name"] is JsonValue n && n.GetValue<string>() != Platform.RulesOs) match = false;
                if (os["arch"] is JsonValue a)
                {
                    var arch = a.GetValue<string>();
                    var mine = arch is "arm64" or "aarch64" ? Platform.IsArm64 : arch is "x64" or "x86_64" ? !Platform.IsArm64 : false; // "x86" = 32-bit: never
                    if (!mine) match = false;
                }
            }
            if (r["features"] is JsonObject fs)
                foreach (var (k, v) in fs)
                    if ((features?.GetValueOrDefault(k) ?? false) != v!.GetValue<bool>()) match = false;
            if (match) allowed = r["action"]!.GetValue<string>() == "allow";
        }
        return allowed;
    }
}

/// <summary>A started game: the Java process plus a signal for "the game window is up".</summary>
public sealed class GameProcess(Process proc, int slot)
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Process Proc { get; } = proc;
    public int Slot { get; } = slot;
    public bool WindowReady => _ready.Task.IsCompleted
        || (OperatingSystem.IsWindows() && Refresh() && Proc.MainWindowHandle != IntPtr.Zero);
    public void MarkWindowReady() => _ready.TrySetResult();
    private bool Refresh() { try { Proc.Refresh(); return true; } catch { return false; } }
}

public static class GameLauncher
{
    private static readonly string[] DefaultOptions =
    [
        "pauseOnLostFocus:false", "inactivityFpsLimit:\"minimized\"", "fullscreen:false", "maxFps:60",
        "renderDistance:8", "simulationDistance:8", "onboardAccessibility:false", "skipMultiplayerWarning:true",
        "joinedFirstServer:true", "tutorialStep:none", "narrator:0",
    ];

    /// <summary>
    /// Starts the game for one player slot. The access token only goes on the command line, never to disk.
    /// Java's console output goes to games/pN/launcher-java.log; WindowReady completes when the game window exists.
    /// </summary>
    public static GameProcess Launch(InstalledGame g, int slot, McToken token, string serverAddress, int? width, int? height)
    {
        var gameDir = Paths.GameDir(slot);
        Directory.CreateDirectory(gameDir);
        var options = Path.Combine(gameDir, "options.txt");
        if (!File.Exists(options)) File.WriteAllLines(options, DefaultOptions);

        // exactly the host's mods
        var modsDir = Path.Combine(gameDir, "mods");
        Directory.CreateDirectory(modsDir);
        var wanted = g.Mods.Select(m => m.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(modsDir, "*.jar"))
            if (!wanted.Contains(Path.GetFileName(f))) File.Delete(f);
        var copied = new ModHashes(modsDir);
        foreach (var m in g.Mods)
        {
            if (copied.IsCurrent(m)) continue;
            copied.Remove(m.File);
            File.Copy(Path.Combine(Paths.ModCache, m.File), Path.Combine(modsDir, m.File), true); // the cache copy was verified
            copied.Set(m);
        }

        var natives = Path.Combine(gameDir, "natives");
        var vars = new Dictionary<string, string>
        {
            ["auth_player_name"] = token.Username,
            ["version_name"] = g.VersionId,
            ["game_directory"] = gameDir,
            ["assets_root"] = Paths.Assets,
            ["assets_index_name"] = g.AssetIndex,
            ["auth_uuid"] = token.Uuid.Replace("-", ""),
            ["auth_access_token"] = token.AccessToken,
            ["clientid"] = "mc-shared",
            ["auth_xuid"] = "0",
            ["user_type"] = "msa",
            ["version_type"] = "release",
            ["natives_directory"] = natives,
            ["launcher_name"] = "UncleCraft",
            ["launcher_version"] = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0",
            ["classpath"] = string.Join(Path.PathSeparator, g.Classpath),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["library_directory"] = Paths.Libraries,
            ["quickPlayMultiplayer"] = serverAddress,
            ["resolution_width"] = (width ?? 1280).ToString(),
            ["resolution_height"] = (height ?? 720).ToString(),
        };
        var features = new Dictionary<string, bool>
        {
            ["is_quick_play_multiplayer"] = true,
            ["has_custom_resolution"] = width.HasValue,
        };

        var psi = new ProcessStartInfo(g.JavaExe)
        {
            WorkingDirectory = gameDir, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-Xms1G");
        psi.ArgumentList.Add("-Xmx3G");
        foreach (var a in g.FabricJvmArgs) psi.ArgumentList.Add(Subst(a, vars));
        foreach (var a in Expand(g.VanillaJvmArgs, features)) psi.ArgumentList.Add(Subst(a, vars)); // includes -XstartOnFirstThread on macOS
        if (g.LoggingArg != null) psi.ArgumentList.Add(g.LoggingArg);
        psi.ArgumentList.Add(g.MainClass);
        foreach (var a in Expand(g.VanillaGameArgs, features)) psi.ArgumentList.Add(Subst(a, vars));
        foreach (var a in g.FabricGameArgs) psi.ArgumentList.Add(Subst(a, vars));

        // Log the command with secrets removed (token, and the join name, which is a one-time pass in interim mode).
        var shown = psi.ArgumentList.Select(a => a == token.AccessToken || a == token.Username ? "***" : a.Contains(Path.PathSeparator) && a.Length > 300 ? "<classpath>" : a);
        string Mask(string line)
        {
            if (token.AccessToken.Length > 0) line = line.Replace(token.AccessToken, "***");
            if (token.Username.Length > 0) line = line.Replace(token.Username, "***");
            return line;
        }
        Log.Info($"launch p{slot}: {g.JavaExe} {string.Join(' ', shown)}");

        var javaLog = new StreamWriter(Path.Combine(gameDir, "launcher-java.log"), false) { AutoFlush = true };
        var gp = new GameProcess(Process.Start(psi) ?? throw new Exception("Java did not start."), slot);
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (javaLog) { try { javaLog.WriteLine(Mask(line)); } catch { /* closed */ } }
            // LWJGL logs its backend when the game window is created; "Connecting to" = quick-play join started.
            if (line.Contains("Backend library:") || line.Contains("Connecting to")) gp.MarkWindowReady();
        }
        gp.Proc.OutputDataReceived += (_, e) => OnLine(e.Data);
        gp.Proc.ErrorDataReceived += (_, e) => OnLine(e.Data);
        gp.Proc.EnableRaisingEvents = true;
        gp.Proc.Exited += (_, _) =>
        {
            Log.Info($"p{slot} java exited with code {SafeExit(gp.Proc)}");
            lock (javaLog) javaLog.Dispose();
        };
        gp.Proc.BeginOutputReadLine();
        gp.Proc.BeginErrorReadLine();
        return gp;
    }

    private static string SafeExit(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    private static IEnumerable<string> Expand(JsonArray args, IReadOnlyDictionary<string, bool> features)
    {
        foreach (var a in args)
        {
            if (a is JsonValue v) { yield return v.GetValue<string>(); continue; }
            if (a!["rules"] is JsonArray rules && !GameInstaller.RulesAllow(rules, features)) continue;
            var value = a["value"]!;
            if (value is JsonArray arr) foreach (var x in arr) yield return x!.GetValue<string>();
            else yield return value.GetValue<string>();
        }
    }

    private static string Subst(string s, Dictionary<string, string> vars)
    {
        foreach (var (k, v) in vars) s = s.Replace("${" + k + "}", v);
        return s;
    }
}
