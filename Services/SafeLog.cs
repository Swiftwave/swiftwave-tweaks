using System.IO;

namespace SwiftwaveTweaks.Services;
public static class SafeLog
{
    private static readonly string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks", "logs", "activity.log");
    private const long MaxLogBytes = 5 * 1024 * 1024; // rotate instead of growing without bound

    public static void Write(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RotateIfLarge();
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{(exception is null ? "" : $" :: {exception.Message}")}\n");
        }
        catch { }
    }

    private static void RotateIfLarge()
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > MaxLogBytes)
            {
                var old = Path.Combine(Path.GetDirectoryName(path)!, "activity.old.log");
                File.Delete(old);
                File.Move(path, old);
            }
        }
        catch { }
    }
}
