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

    /// <summary>macOS can't move other apps' windows without accessibility permission: size the games, don't tile.
    /// Updates: no one-copy lock on macOS (open -n starts the new version), so the handover only minimizes the window.</summary>
    private sealed class MacWindows : IGameWindows, IUpdateHost
    {
        public void Handover(bool toNewVersion)
        {
            var w = _window;
            try { w?.Invoke(() => w.SetMinimized(toNewVersion)); } catch (Exception e) { Log.Error("update handover failed", e); }
        }

        /// <summary>Each game starts at its SplitLayout size on the launcher's monitor (40 pt less height for the title
        /// bar); the players drag the windows into place, since the app can't move them.</summary>
        public (int width, int height)? SplitSize(int index, int count)
        {
            if (count < 2) return null;
            System.Drawing.Rectangle area;
            try { var a = _window!.MainMonitor.WorkArea; area = new(a.X, a.Y, a.Width, a.Height); }
            catch { area = new(0, 0, 1920, 1040); }
            var r = SplitLayout.Rects(count, area)[Math.Clamp(index, 0, count - 1)];
            return (Math.Max(320, r.Width), Math.Max(240, r.Height - 40));
        }

        public Task Arrange(IReadOnlyList<GameProcess> games) => Task.CompletedTask;
    }
}
