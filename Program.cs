using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace McShared;

internal static class Program
{
    private static Mutex? _single;

    [STAThread]
    private static void Main(string[] args)
    {
        // Crashes go to launcher.log with a short friendly message instead of the raw .NET dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Log.Error("unhandled UI exception", e.Exception);
            MessageBox.Show("Something went wrong. The details are in launcher.log.", Installer.App);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("unobserved task exception", e.Exception); e.SetObserved(); };
        ApplicationConfiguration.Initialize();
        Updater.ParseArgs(args);
        // A new version on trial (--update-trial) is already in place: no install offer.
        if (!Updater.IsTrial && Installer.OfferInstall()) return;
        // One copy at a time (two would share game folders). Taken after the install step: the installer starts the
        // installed copy and exits without ever holding it. During an update the old copy hands it over (retry briefly).
        _single = new Mutex(false, $@"Local\UncleCraft-{Environment.UserName}");
        if (!TakeSingleInstance(Updater.IsTrial ? 15_000 : 0))
        {
            if (Updater.IsTrial) Log.Info("update trial: the old version still holds the lock; closing");
            else MessageBox.Show("UncleCraft is already open.", Installer.App);
            return;
        }
        if (Updater.IsTrial) Updater.TrialStarted(); else Updater.CleanUp();
        // The pixel launcher needs the WebView2 runtime (built into Windows 11); without it, use the classic window.
        if (WebUiForm.RuntimeAvailable()) Application.Run(new WebUiForm());
        else Application.Run(new MainForm());
    }

    /// <summary>The one-copy lock. A mutex belongs to the thread that took it: call these on the UI thread.</summary>
    internal static bool TakeSingleInstance(int waitMs)
    {
        try { return _single!.WaitOne(waitMs); }
        catch (AbandonedMutexException) { return true; } // its last owner was killed (a rolled-back update): ours now
    }

    internal static void ReleaseSingleInstance()
    {
        try { _single?.ReleaseMutex(); } catch (ApplicationException) { /* not held */ }
    }
}

/// <summary>"Installing" = copy the exe to %LOCALAPPDATA%\Programs\UncleCraft and add Start menu + desktop shortcuts.
/// The app was called mc-shared before: its shortcuts and program folder are removed here. Game data, settings and
/// remembered logins stay in %LOCALAPPDATA%\mc-shared (Paths.Root), so nothing needs migrating.</summary>
internal static class Installer
{
    private static readonly string Programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
    private static readonly string Dir = Path.Combine(Programs, "UncleCraft");
    private static string Exe => Path.Combine(Dir, "UncleCraft.exe");
    public const string App = MainForm.AppName;

    /// <summary>Returns true if the installed copy was started instead (caller should exit).</summary>
    public static bool OfferInstall()
    {
        var self = Environment.ProcessPath!;
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase)) return false;
        var update = File.Exists(Exe);
        var oldInstall = Directory.Exists(Path.Combine(Programs, "mc-shared"));
        if (update && FileVer(self) is { } mine && FileVer(Exe) is { } installed && mine < installed)
        {
            if (MessageBox.Show($"This copy (v{mine.ToString(3)}) is older than the installed one (v{installed.ToString(3)}). Replace it anyway?",
                    App, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
                return true;
            }
        }
        else
        {
            var answer = MessageBox.Show(update
                    ? $"{App} is already installed on this PC. Replace it with this copy?"
                    : oldInstall
                        ? $"mc-shared is now called {App}. Update it on this PC? Your saved players and game files are kept."
                        : $"Install {App} on this PC? It adds a Start menu and desktop shortcut. (No admin rights needed.)",
                App, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel) return true;
            if (answer == DialogResult.No) return false;
        }
        try
        {
            Directory.CreateDirectory(Dir);
            // Copy beside it first, so a failed copy (disk full, antivirus) never leaves a broken exe behind the shortcuts.
            File.Copy(self, Exe + ".new", true);
            File.Move(Exe + ".new", Exe, true);
            var start = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Shortcut(Path.Combine(start, $"{App}.lnk"));
            Shortcut(Path.Combine(desktop, $"{App}.lnk"));
            RemoveOldName(start, desktop);
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
            return true;
        }
        catch (Exception e) when (update && e is IOException or UnauthorizedAccessException) // a running exe can't be replaced
        {
            try { File.Delete(Exe + ".new"); } catch { /* ignore */ }
            MessageBox.Show($"The installed {App} is still open, so it can't be replaced. Close it (and its games), then run this file again.", App);
            return false;
        }
        catch (Exception e)
        {
            MessageBox.Show("Install failed: " + e.Message + "\nRunning from here instead.", App);
            return false;
        }
    }

    private static Version? FileVer(string exe)
    {
        try { var v = FileVersionInfo.GetVersionInfo(exe); return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart); }
        catch { return null; }
    }

    /// <summary>Removes the shortcuts and program folder from when the app was called mc-shared (best effort).</summary>
    private static void RemoveOldName(string start, string desktop)
    {
        foreach (var lnk in new[] { Path.Combine(start, "mc-shared.lnk"), Path.Combine(desktop, "mc-shared.lnk") })
            try { File.Delete(lnk); } catch { /* ignore */ }
        try { Directory.Delete(Path.Combine(Programs, "mc-shared"), true); } catch { /* old copy still running: harmless leftover */ }
    }

    private static void Shortcut(string lnk)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic sc = shell.CreateShortcut(lnk);
        sc.TargetPath = Exe;
        sc.IconLocation = Exe + ",0";
        sc.WorkingDirectory = Dir;
        sc.Description = "UncleCraft: play on the shared Minecraft server";
        sc.Save();
    }
}

/// <summary>Split-screen on Windows: both hosts (pixel page and classic window) size and tile the games with this. Games
/// with the tile mod (GameProcess.Tiled) place themselves borderless; LaunchSize and Tile are the fallback for the rest.</summary>
internal static class WindowTiler
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT r, uint style, bool menu, uint exStyle);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

    /// <summary>The tile mod's rectangle. The app is per-monitor DPI aware (ApplicationHighDpiMode PerMonitorV2), so the
    /// working area is in physical pixels of the virtual screen, which is also what SDL3 window coordinates are on Windows.</summary>
    public static Rectangle? TileRect(int index, int count, Rectangle workingArea) =>
        count < 2 ? null : SplitLayout.Rects(count, workingArea)[Math.Clamp(index, 0, count - 1)];

    /// <summary>
    /// Minecraft's --width/--height set the game area inside the window; the title bar and frame come on top. So each
    /// game starts with the client size that fits its SplitLayout rectangle, and Tile only nudges it into place.
    /// </summary>
    public static (int width, int height)? LaunchSize(int index, int count, Rectangle workingArea)
    {
        if (count < 2) return null;
        var r = SplitLayout.Rects(count, workingArea)[Math.Clamp(index, 0, count - 1)];
        var frame = new RECT();
        AdjustWindowRectEx(ref frame, 0x00CF0000 /* WS_OVERLAPPEDWINDOW, like GLFW's windows */, false, 0);
        return (Math.Max(320, r.Width - (frame.Right - frame.Left)), Math.Max(240, r.Height - (frame.Bottom - frame.Top)));
    }

    /// <summary>Waits for every game window, then puts them in their SplitLayout rectangles of the given working area.</summary>
    public static async Task Tile(IReadOnlyList<GameProcess> all, Rectangle workingArea)
    {
        if (all.Count < 2) return; // one player: the game keeps its own window, as before split-screen
        if (all.All(g => g.Tiled)) return; // the tile mod placed every window itself
        var games = all.Select(g => g.Proc).ToList();
        var deadline = DateTime.Now.AddMinutes(4);
        while (DateTime.Now < deadline)
        {
            foreach (var p in games) p.Refresh();
            if (games.Any(p => p.HasExited)) return;
            if (games.All(p => p.MainWindowHandle != IntPtr.Zero)) break;
            await Task.Delay(2000);
        }
        await Task.Delay(3000); // let the windows finish creating
        var rects = SplitLayout.Rects(games.Count, workingArea);
        for (var i = 0; i < games.Count; i++)
        {
            if (all[i].Tiled) continue;
            try { games[i].Refresh(); if (!games[i].HasExited) Place(games[i].MainWindowHandle, rects[i]); }
            catch (Exception e) { Log.Info($"tile window {i + 1}: {e.Message}"); }
        }
    }

    private static void Place(IntPtr h, Rectangle r)
    {
        if (h == IntPtr.Zero) return;
        ShowWindow(h, 9); // restore
        SetWindowPos(h, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, 0x0004 /* SWP_NOZORDER */);
        // Windows 10/11 frames have invisible resize borders (about 7 px left, right and bottom): without this the
        // visible windows would have gaps between them. Grow the window by exactly those borders.
        if (GetWindowRect(h, out var outer) && DwmGetWindowAttribute(h, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var seen, Marshal.SizeOf<RECT>()) == 0)
        {
            int l = seen.Left - outer.Left, t = seen.Top - outer.Top, rr = outer.Right - seen.Right, b = outer.Bottom - seen.Bottom;
            if (l is > 0 and < 32 || t is > 0 and < 32 || rr is > 0 and < 32 || b is > 0 and < 32)
                SetWindowPos(h, IntPtr.Zero, r.X - l, r.Y - t, r.Width + l + rr, r.Height + t + b, 0x0004);
        }
    }
}
