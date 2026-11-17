using System.Reflection;
using Photino.NET;

namespace McShared;

/// <summary>
/// macOS host: a Photino window (WKWebView) showing the embedded pixel UI from app://localhost/, wired to AppCore.
/// Everything is logged to ~/Library/Logs/UncleCraft/launcher.log for the tester to send back.
/// </summary>
public static class MacProgram
{
    private static PhotinoWindow? _window;

    [STAThread]
    public static void Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("unobserved task exception", e.Exception); e.SetObserved(); };
        Log.Info($"--- {Platform.AppName} (macOS) starting on {Platform.Describe()}");
        try
        {
            var core = new AppCore(Post, new MacWindows());
            _window = new PhotinoWindow()
                .SetTitle(Platform.AppName)
                .SetUseOsDefaultSize(false)
                .SetSize(1200, 800)
                .SetMinSize(900, 640)
                .Center()
                .SetResizable(true)
                .SetContextMenuEnabled(false)
                .SetDevToolsEnabled(false)
                .RegisterCustomSchemeHandler("app", Serve)
                .RegisterWebMessageReceivedHandler((_, message) => _ = core.HandleMessage(message))
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
                "Minecraft is still running. If you close UncleCraft, the host's server frees your account a few minutes after you quit.\n\nClose anyway?",
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
        return stream;
    }

    /// <summary>macOS can't move other apps' windows without accessibility permission: size the games, don't tile.</summary>
    private sealed class MacWindows : IGameWindows
    {
        public (int width, int height)? SplitSize()
        {
            try
            {
                var area = _window!.MainMonitor.WorkArea;
                return (area.Width / 2, area.Height - 40);
            }
            catch { return (960, 1000); }
        }

        public Task Arrange(IReadOnlyList<GameProcess> games) => Task.CompletedTask;
    }
}
