using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Photino.NET;

namespace McShared;

/// <summary>
/// macOS host: a Photino window (WKWebView) showing the embedded pixel UI from app://localhost/, wired to AppCore.
/// Everything is logged to ~/Library/Logs/UncleCraft/launcher.log for the tester to send back.
/// </summary>
public static class MacProgram
{
    private static PhotinoWindow? _window;

    /// <summary>
    /// Photino has no navigation guard or page-URL check, so a dropped or navigated-to page also gets
    /// window.external.sendMessage. Only our index.html knows this per-launch key (served into its bridge-key meta).
    /// </summary>
    private static readonly string BridgeKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private const string BridgeKeyPlaceholder = "<meta name=\"bridge-key\" content=\"\">";

    private static bool FromLauncherPage(string message)
    {
        try
        {
            var key = JsonNode.Parse(message)?["key"]?.GetValue<string>() ?? "";
            return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(BridgeKey));
        }
        catch { return false; }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("unobserved task exception", e.Exception); e.SetObserved(); };
        Log.Info($"--- {Platform.AppName} (macOS) starting on {Platform.Describe()}");
        Updater.ParseArgs(args);
        if (Updater.IsTrial) Updater.TrialStarted(); else Updater.CleanUp();
        try
        {
            var host = new MacWindows();
            var core = new AppCore(Post, host, host);
            host.Notice = core.HostNotice;
            _window = new PhotinoWindow()
                .SetTitle(Platform.Title)
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .SetMinSize(900, 640)
                .Center()
                .SetResizable(true)
                .SetContextMenuEnabled(false)
                .SetDevToolsEnabled(false)
                .RegisterCustomSchemeHandler("app", Serve)
                .RegisterWebMessageReceivedHandler((_, message) => { if (FromLauncherPage(message)) _ = core.HandleMessage(message); else Log.Info("ignored a message from a page that isn't the launcher"); })
                .RegisterWindowClosingHandler((_, _) => core.Running > 0 && !ConfirmClose())
                .Load(new Uri("app://localhost/index.html"));
            _window.WaitForClose();
            Log.Info("window closed");
        }
        catch (Exception e)
        {
            Log.Error("UncleCraft could not start", e);
            throw;
        }
    }

    /// <summary>True = really close although games are running.</summary>
    private static bool ConfirmClose()
    {
        try
        {
            var r = _window!.ShowMessage(Platform.AppName,
                "Minecraft is still running. Closing UncleCraft also disconnects you from the server (the game connection runs through UncleCraft).\n\nClose anyway?",
                PhotinoDialogButtons.YesNo, PhotinoDialogIcon.Question);
            return r == PhotinoDialogResult.Yes;
        }
        catch { return true; }
    }

    private static void Post(string json)
    {
        var w = _window;
        if (w == null) return;
        try { w.Invoke(() => w.SendWebMessage(json)); } catch (Exception e) { Log.Error("send to page failed", e); }
    }

    /// <summary>Serves ui/*.html|css|js from the app's embedded resources (nothing from disk or the network).</summary>
    private static Stream Serve(object sender, string scheme, string url, out string contentType)
    {
        var path = new Uri(url).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "index.html";
        contentType = Path.GetExtension(path) switch { ".html" => "text/html", ".css" => "text/css", ".js" => "text/javascript", ".png" => "image/png", _ => "application/octet-stream" };
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + path);
        if (stream == null) { Log.Info($"ui resource not found: {path}"); contentType = "text/plain"; return new MemoryStream("not found"u8.ToArray()); }
        if (path != "index.html") return stream;
        using var reader = new StreamReader(stream);
        var html = reader.ReadToEnd().Replace(BridgeKeyPlaceholder, $"<meta name=\"bridge-key\" content=\"{BridgeKey}\">");
        return new MemoryStream(Encoding.UTF8.GetBytes(html));
    }

    /// <summary>Split-screen: the tile mod in each game removes its title bar and puts it in its tile (SplitTile), from
    /// inside the game, so nothing here moves windows (no Accessibility or Automation permission needed).
    /// Updates: no one-copy lock on macOS (open -n starts the new version), so the handover only minimizes the window.</summary>
    private sealed class MacWindows : IGameWindows, IUpdateHost
    {
        /// <summary>Shows a home-screen notice (AppCore.HostNotice).</summary>
        public Action<string>? Notice { get; set; }

        public void Handover(bool toNewVersion)
        {
            var w = _window;
            try { w?.Invoke(() => w.SetMinimized(toNewVersion)); } catch (Exception e) { Log.Error("update handover failed", e); }
        }

        /// <summary>The player's SplitLayout rectangle on the launcher's screen (visible frame, top-left origin, points:
        /// SDL's window coordinates on macOS). Null if the screen can't be found: the game then just starts at SplitSize.</summary>
        public async Task<System.Drawing.Rectangle?> SplitTile(int index, int count)
        {
            if (count < 2) return null;
            try { return await LauncherScreen() is { } area ? SplitLayout.Rects(count, area)[Math.Clamp(index, 0, count - 1)] : null; }
            catch (Exception e) { Log.Error("finding the split-screen tile failed", e); return null; }
        }

        /// <summary>Fallback without the tile mod: the SplitLayout size on the launcher's monitor (40 pt less height for
        /// the title bar); the player drags the windows into place.</summary>
        public (int width, int height)? SplitSize(int index, int count)
        {
            if (count < 2) return null;
            System.Drawing.Rectangle area;
            try { var a = _window!.MainMonitor.WorkArea; area = new(a.X, a.Y, a.Width, a.Height); }
            catch { area = new(0, 0, 1920, 1040); }
            var r = SplitLayout.Rects(count, area)[Math.Clamp(index, 0, count - 1)];
            return (Math.Max(320, r.Width), Math.Max(240, r.Height - 40));
        }

        /// <summary>Nothing to do: tiled games placed themselves, and the others keep their launch size.</summary>
        public Task Arrange(IReadOnlyList<GameProcess> games)
        {
            if (games.Count > 1 && games.Any(g => !g.Tiled)) Log.Info("some game windows run without the tile mod; they keep their launch size");
            return Task.CompletedTask;
        }

        /// <summary>
        /// Visible frame (menu bar and Dock excluded) of the screen the launcher window is on, converted from AppKit's
        /// bottom-left origin to the top-left one SDL and CoreGraphics use: top = mainScreenHeight - (origin.y + height). The launcher's
        /// window is found with CGWindowListCopyWindowInfo (already top-left; needs no permission); if that fails, the
        /// main (menu bar) screen. Output "x y w h"; null = couldn't tell, so no tile. Needs no permission.
        /// </summary>
        private const string ScreenScript = """
            function run(argv) {
              ObjC.import('AppKit');
              ObjC.import('CoreGraphics');
              const screens = $.NSScreen.screens;
              const mainH = screens.objectAtIndex(0).frame.size.height;
              const list = [];
              for (let i = 0; i < screens.count; i++) {
                const s = screens.objectAtIndex(i), f = s.frame, v = s.visibleFrame;
                list.push({ x: f.origin.x, y: mainH - (f.origin.y + f.size.height), w: f.size.width, h: f.size.height,
                  vx: v.origin.x, vy: mainH - (v.origin.y + v.size.height), vw: v.size.width, vh: v.size.height });
              }
              let pick = list[0];
              try {
                const pid = Number(argv[0]);
                const wins = ObjC.deepUnwrap(ObjC.castRefToObject($.CGWindowListCopyWindowInfo(1, 0))) || [];
                const win = wins.find(w => w.kCGWindowOwnerPID === pid && w.kCGWindowLayer === 0 && w.kCGWindowBounds && w.kCGWindowBounds.Width > 100);
                if (win) {
                  const b = win.kCGWindowBounds, cx = b.X + b.Width / 2, cy = b.Y + b.Height / 2;
                  pick = list.find(r => cx >= r.x && cx < r.x + r.w && cy >= r.y && cy < r.y + r.h) || pick;
                }
              } catch (e) {}
              return [pick.vx, pick.vy, pick.vw, pick.vh].map(Math.round).join(' ');
            }
            """;

        private static async Task<System.Drawing.Rectangle?> LauncherScreen()
        {
            var (code, output, error) = await Osascript(TimeSpan.FromSeconds(8), "-l", "JavaScript", "-e", ScreenScript, Environment.ProcessId.ToString());
            var n = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (code == 0 && n.Length == 4 && n.All(s => int.TryParse(s, out _)) && int.Parse(n[2]) >= 640 && int.Parse(n[3]) >= 480)
                return new System.Drawing.Rectangle(int.Parse(n[0]), int.Parse(n[1]), int.Parse(n[2]), int.Parse(n[3]));
            Log.Info($"can't find the launcher's screen (exit {code}: {output} {error}); the games start at their launch size, not tiled");
            return null;
        }

        /// <summary>Runs /usr/bin/osascript with literal arguments (no shell); a timeout kills it and returns exit -1.</summary>
        private static async Task<(int code, string output, string error)> Osascript(TimeSpan timeout, params string[] args)
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("osascript did not start");
            p.StandardInput.Close();
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                return (-1, "", "osascript timed out");
            }
            return (p.ExitCode, (await output).Trim(), (await error).Trim());
        }
    }
}
