namespace McShared;

/// <summary>
/// launcher.log for troubleshooting: Windows %LOCALAPPDATA%\mc-shared\launcher.log, macOS ~/Library/Logs/UncleCraft/launcher.log.
/// Never write passwords, device tokens, Minecraft tokens or join names here (callers redact).
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    public static string FilePath => Path.Combine(Paths.Logs, "launcher.log");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Error(string msg, Exception? e = null) => Write("ERROR", e == null ? msg : $"{msg}: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.Logs);
                var f = new FileInfo(FilePath);
                if (f.Exists && f.Length > 5_000_000) File.Move(FilePath, FilePath + ".old", true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}{Environment.NewLine}");
            }
        }
        catch { /* logging must never break the app */ }
    }
}
