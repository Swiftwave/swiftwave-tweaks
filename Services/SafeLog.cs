using System.IO;

namespace SwiftwaveTweaks.Services;
public static class SafeLog
{
    private static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks", "logs", "activity.log");
    public static void Write(string message, Exception? exception = null)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{(exception is null ? "" : $" :: {exception.Message}")}\n"); } catch { }
    }
}
