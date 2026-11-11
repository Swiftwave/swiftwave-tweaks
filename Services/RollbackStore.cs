using System.IO;
using System.Text.Json;

namespace SwiftwaveTweaks.Services;

public sealed record RollbackItem(string Id, string Name, string PreviousState, DateTimeOffset CreatedAt);

public static class RollbackStore
{
    private static readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks", "rollback.json");

    // Single lock around read-modify-write cycles: concurrent applies (now serialized by the
    // Optimization.ApplyGate) and History restores must never lose a rollback record.
    private static readonly object FileGate = new();

    public static IReadOnlyList<RollbackItem> Read()
    {
        lock (FileGate)
        {
            try { return JsonSerializer.Deserialize<List<RollbackItem>>(File.ReadAllText(path)) ?? []; }
            catch { return []; }
        }
    }

    public static void Add(RollbackItem item)
    {
        lock (FileGate)
        {
            var entries = Read().Where(x => x.Id != item.Id).Append(item).ToList();
            WriteAll(entries);
        }
    }

    public static void Remove(string id)
    {
        lock (FileGate)
        {
            var entries = Read().Where(x => x.Id != id).ToList();
            WriteAll(entries);
        }
    }

    /// <summary>Atomic write (temp file + move) so a crash mid-write can never corrupt the rollback history.</summary>
    private static void WriteAll(List<RollbackItem> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
        File.Move(tmp, path, overwrite: true);
    }
}
