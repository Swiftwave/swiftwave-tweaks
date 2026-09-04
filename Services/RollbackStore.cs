using System.IO;
using System.Text.Json;

namespace SwiftwaveTweaks.Services;

public sealed record RollbackItem(string Id, string Name, string PreviousState, DateTimeOffset CreatedAt);

public static class RollbackStore
{
    private static readonly string path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks", "rollback.json");

    public static IReadOnlyList<RollbackItem> Read()
    {
        try { return JsonSerializer.Deserialize<List<RollbackItem>>(File.ReadAllText(path)) ?? []; }
        catch { return []; }
    }

    public static void Add(RollbackItem item)
    {
        var entries = Read().Where(x => x.Id != item.Id).Append(item).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }

    public static void Remove(string id)
    {
        var entries = Read().Where(x => x.Id != id).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries));
    }
}
