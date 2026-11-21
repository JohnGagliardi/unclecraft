using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace McShared;

internal static class Program
{
    [STAThread]
    private static void Main()
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
        if (Installer.OfferInstall()) return;
        // One copy at a time (two would share game folders). Taken after the install step: the installer starts the
        // installed copy and exits without ever holding it.
        using var single = new Mutex(true, $@"Local\UncleCraft-{Environment.UserName}", out var first);
        if (!first)
        {
            MessageBox.Show("UncleCraft is already open.", Installer.App);
            return;
        }
        // The pixel launcher needs the WebView2 runtime (built into Windows 11); without it, use the classic window.
        if (WebUiForm.RuntimeAvailable()) Application.Run(new WebUiForm());
        else Application.Run(new MainForm());
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

internal static class WindowTiler
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);

    /// <summary>Waits for both game windows, then puts them left | right in the given working area (default: the primary screen's).</summary>
    public static async Task TileSideBySide(Process a, Process b, Rectangle? workingArea = null)
    {
        var deadline = DateTime.Now.AddMinutes(4);
        while (DateTime.Now < deadline)
        {
            a.Refresh(); b.Refresh();
            if (a.HasExited || b.HasExited) return;
            if (a.MainWindowHandle != IntPtr.Zero && b.MainWindowHandle != IntPtr.Zero) break;
            await Task.Delay(2000);
        }
        await Task.Delay(3000); // let the windows finish creating
        var area = workingArea ?? Screen.PrimaryScreen!.WorkingArea;
        var w = area.Width / 2;
        Place(a.MainWindowHandle, area.X, area.Y, w, area.Height);
        Place(b.MainWindowHandle, area.X + w, area.Y, w, area.Height);
    }

    private static void Place(IntPtr h, int x, int y, int w, int hgt)
    {
        if (h == IntPtr.Zero) return;
        ShowWindow(h, 9); // restore
        SetWindowPos(h, IntPtr.Zero, x, y, w, hgt, 0x0004 /* SWP_NOZORDER */);
    }
}
