using System.Text;

namespace WinBoost.Services;

/// <summary>Thread-safe file logger: %LocalAppData%\WinBoost\winboost.log (rotated at ~2 MB). Never throws.</summary>
public static class AppLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string Folder => AppConstants.DataFolder;
    public static string FilePath => Path.Combine(Folder, AppConstants.LogFileName);

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}{Environment.NewLine}{ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    string old = FilePath + ".1";
                    try { File.Delete(old); File.Move(FilePath, old); } catch { }
                }
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}", new UTF8Encoding(false));
            }
        }
        catch { /* logging must never crash the app */ }
    }

    /// <summary>Writes a file atomically: temp file then replace.</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}
