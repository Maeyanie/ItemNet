using System.IO;

namespace ItemNet.App.Services;

/// <summary>
/// Plain-text log in %LOCALAPPDATA%\ItemNet\ItemNet.log, flushed per line so the last line
/// survives a native crash. Kept to one session; the previous one is renamed to ItemNet.old.log.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _writer;

    public static string FilePath => Path.Combine(AppSettings.Folder, "ItemNet.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            if (File.Exists(FilePath)) File.Move(FilePath, Path.Combine(AppSettings.Folder, "ItemNet.old.log"), overwrite: true);
            _writer = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch { _writer = null; }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { _writer?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId,2}] {message}"); }
            catch { /* logging must never take the app down */ }
        }
    }
}
