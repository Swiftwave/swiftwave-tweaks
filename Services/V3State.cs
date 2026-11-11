using SwiftwaveTweaks.Core;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Journal-only state evaluation (spec §4.4 C3/C4). The current state of a V3 feature is
/// decided by the journal, never by "the value looks right":
///
///   no committed transaction              → Unapplied, even if every value matches (C4)
///   committed and every target matches    → Optimized
///   committed but a target differs        → E-FOREIGN-EDIT, observed value reported (C3)
///
/// Callers supply (target, actual) pairs read live from the system.
/// </summary>
public static class V3State
{
    public static Detection Evaluate(
        string featureId,
        IEnumerable<(string Target, string? Actual)> current,
        string optimizedText,
        string notAppliedText)
    {
        var live = Journal.LatestCommitted(featureId);
        if (live is null)
            return new Detection(OptState.NeedsAttention, notAppliedText, true, "");

        if (Journal.IsCorrupt)
            return new Detection(OptState.NeedsAttention, "E-JOURNAL-CORRUPT — journal lines could not be parsed.", true, "E-JOURNAL-CORRUPT");

        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, actual) in current) map[target] = actual;

        foreach (var jt in live.Targets)
        {
            map.TryGetValue(jt.Target, out var actual);
            if (!Equivalent(actual, jt.New))
                return new Detection(
                    OptState.NeedsAttention,
                    $"E-FOREIGN-EDIT: {jt.Target} expected {jt.New}, observed {actual ?? "absent"}",
                    true, "E-FOREIGN-EDIT");
        }

        return new Detection(OptState.Optimized, optimizedText, true, "");
    }

    private static bool Equivalent(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string Enc(int? v) => v?.ToString() ?? "";
    public static string Enc(string? v) => v ?? "";
    public static string Enc(byte[]? v) => v is null ? "" : Convert.ToBase64String(v);
}
