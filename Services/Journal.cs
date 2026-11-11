using System.IO;
using System.Text;
using System.Text.Json;

namespace SwiftwaveTweaks.Services;

/// <summary>One target written by a transaction, with the exact prior and new raw representation.</summary>
public sealed class JournalTarget
{
    public string Target { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? Prior { get; set; }
    public string New { get; set; } = "";
}

/// <summary>A single append-only journal line.</summary>
public sealed class JournalRecord
{
    public string Tx { get; set; } = "";
    public string Feature { get; set; } = "";
    /// <summary>committed | rolledback | rollback-failed</summary>
    public string Status { get; set; } = "committed";
    public string Utc { get; set; } = "";
    public List<JournalTarget> Targets { get; set; } = new();
}

/// <summary>
/// Append-only transaction journal. State is derived from this file alone — the UI never
/// infers "applied" from observed values (spec §4.4 C4: a value matching ours with no
/// journal entry is still Unapplied).
///
/// Storage: %LocalAppData%\SwiftwaveTweaks\journal.jsonl, one JSON object per line.
/// Each append opens the file with FileOptions.WriteThrough and flushes to disk before
/// returning, so an intent/commit survives a process kill (spec §4.2).
/// </summary>
public static class Journal
{
    private static readonly object Gate = new();

    public static string Dir =>
        Environment.GetEnvironmentVariable("SWIFTWAVE_JOURNAL_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks");

    public static string FilePath => Path.Combine(Dir, "journal.jsonl");

    public static bool IsCorrupt { get; private set; }

    /// <summary>Appends a committed transaction and returns its id.</summary>
    public static string Commit(string featureId, IEnumerable<JournalTarget> targets)
    {
        var record = new JournalRecord
        {
            Tx = Guid.NewGuid().ToString("N"),
            Feature = featureId,
            Status = "committed",
            Utc = DateTime.UtcNow.ToString("O"),
            Targets = targets.ToList()
        };
        Append(record);
        return record.Tx;
    }

    /// <summary>Marks the latest committed transaction for a feature as rolled back.</summary>
    public static void MarkRolledBack(string featureId, bool failed = false)
    {
        var live = LatestCommitted(featureId);
        if (live is null) return;
        Append(new JournalRecord
        {
            Tx = live.Tx,
            Feature = featureId,
            Status = failed ? "rollback-failed" : "rolledback",
            Utc = DateTime.UtcNow.ToString("O")
        });
    }

    /// <summary>The latest committed, not-yet-rolled-back transaction for a feature.</summary>
    public static JournalRecord? LatestCommitted(string featureId)
    {
        var all = ReadAll();
        JournalRecord? current = null;
        foreach (var r in all.Where(r => r.Feature == featureId))
        {
            if (r.Status == "committed")
                current = r;
            else if (current is not null && current.Tx == r.Tx)
                current = null;
        }
        return current;
    }

    /// <summary>Rebuildable ownership index: target → owning live feature.</summary>
    public static string? OwnerOf(string target)
    {
        foreach (var r in ReadAll())
        {
            if (r.Status != "committed") continue;
            if (r.Targets.Any(t => string.Equals(t.Target, target, StringComparison.OrdinalIgnoreCase)))
            {
                // Only the latest committed transaction for that feature still owns it.
                var live = LatestCommitted(r.Feature);
                if (live is not null && live.Tx == r.Tx) return r.Feature;
            }
        }
        return null;
    }

    public static IReadOnlyList<JournalRecord> ReadAll()
    {
        lock (Gate)
        {
            var list = new List<JournalRecord>();
            if (!File.Exists(FilePath)) return list;
            IsCorrupt = false;
            foreach (var line in File.ReadLines(FilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<JournalRecord>(line);
                    if (rec is not null) list.Add(rec);
                }
                catch { IsCorrupt = true; }
            }
            return list;
        }
    }

    private static void Append(JournalRecord record)
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(Dir);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
            using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite, 4096, FileOptions.WriteThrough);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
    }
}
