using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace McShared;

/// <summary>
/// Windows host for the pixel launcher: a WebView2 page (ui/*.html|css|js, embedded in the exe and served from
/// https://unclecraft.local/). All logic lives in AppCore (shared with the macOS build).
/// </summary>
public sealed class WebUiForm : Form, IGameWindows, IUpdateHost
{
    private const string Origin = "https://unclecraft.local/";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(18, 26, 48) };
    private CoreWebView2Environment? _env;
    private readonly AppCore _core;

    public static bool RuntimeAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); } catch { return false; }
    }

    public WebUiForm()
    {
        _core = new AppCore(Post, this, this);
        Text = Platform.Title;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { /* default */ }
        BackColor = Color.FromArgb(18, 26, 48);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(_web);
        Load += async (_, _) => await Init();
        FormClosing += (_, e) =>
        {
            if (_core.Running > 0 && MessageBox.Show("Minecraft is still running. Closing UncleCraft also disconnects you from the server (the game connection runs through UncleCraft).\n\nClose anyway?",
                    Platform.AppName, MessageBoxButtons.YesNo) == DialogResult.No) e.Cancel = true;
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var on = 1; // dark title bar to match the page
        try { DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)); } catch { /* older Windows */ }
        var k = DeviceDpi / 96f; // DPI-aware sizing (the app is PerMonitorV2)
        var area = Screen.FromHandle(Handle).WorkingArea;
        ClientSize = new Size(Math.Min((int)(1200 * k), area.Width - 40), Math.Min((int)(800 * k), area.Height - 40));
        MinimumSize = new Size((int)(900 * k), (int)(640 * k));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private async Task Init()
    {
        try
        {
            _env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Paths.Root, "webview"));
            await _web.EnsureCoreWebView2Async(_env);
        }
        catch (Exception e)
        {
            Log.Error("WebView2 failed to start", e);
            MessageBox.Show("The UncleCraft window could not start (" + e.Message + "). Opening the simple window instead.", Platform.AppName);
            Hide(); new MainForm().ShowDialog(); Close(); return;
        }
        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false; // passwords never stored, not even by the browser engine
        core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += ServeResource;
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.WebMessageReceived += async (_, e) => { if (e.Source.StartsWith(Origin, StringComparison.OrdinalIgnoreCase)) await _core.HandleMessage(e.WebMessageAsJson); };
        core.Navigate(Origin + "index.html");
    }

    private void ServeResource(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = "index.html";
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + path);
        if (stream == null) { e.Response = _env!.CreateWebResourceResponse(null, 404, "Not Found", ""); return; }
        var type = Path.GetExtension(path) switch { ".html" => "text/html", ".css" => "text/css", ".js" => "text/javascript", ".png" => "image/png", _ => "application/octet-stream" };
        e.Response = _env!.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}; charset=utf-8\nCache-Control: no-store");
    }

    private void Post(string json)
    {
        void Send() { try { _web.CoreWebView2?.PostWebMessageAsJson(json); } catch (InvalidOperationException) { /* closing */ } }
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(Send); else Send();
    }

    // ---------------- IGameWindows ----------------

    /// <summary>The working area of the screen the launcher is on: games are sized and tiled there.</summary>
    private Rectangle LauncherArea() => (Screen.FromHandle(Handle) ?? Screen.PrimaryScreen)!.WorkingArea;

    public (int width, int height)? SplitSize(int index, int count) => WindowTiler.LaunchSize(index, count, LauncherArea());

    public Task Arrange(IReadOnlyList<GameProcess> games) => WindowTiler.Tile([.. games.Select(g => g.Proc)], LauncherArea());

    // ---------------- IUpdateHost ----------------

    public void Handover(bool toNewVersion) => UpdateHandover.Run(this, toNewVersion);
}

/// <summary>Both Windows hosts: hand the one-copy lock to the new version and hide, or take it back and show again.</summary>
internal static class UpdateHandover
{
    public static void Run(Form form, bool toNewVersion) => form.Invoke(() =>
    {
        if (toNewVersion) { Program.ReleaseSingleInstance(); form.Hide(); return; }
        if (!Program.TakeSingleInstance(5000)) Log.Info("update rollback: couldn't take the one-copy lock back");
        form.Show();
        form.Activate();
    });
}
