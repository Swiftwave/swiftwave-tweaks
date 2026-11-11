using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace SwiftwaveTweaks.Services;

/// <summary>One physical core as reported by GetLogicalProcessorInformationEx.</summary>
public sealed record LogicalCoreInfo(int Group, int FirstLp, IReadOnlyList<int> Lps, int EfficiencyClass, int L3Key)
{
    public bool HasSmts => Lps.Count > 1;
}

/// <summary>
/// Result of D-TOPO discovery. <see cref="Reason"/> is non-null only when topology could not
/// be trusted at all (E-CPU-UNKNOWN). Affinity-specific gates (E-TOPO-DEGENERATE,
/// E-GRP-GT64, E-HYBRID-AMBIGUOUS) are applied in <see cref="DeriveAffinity"/>.
/// </summary>
public sealed class CpuTopologyResult
{
    public bool Ok { get; init; }
    public string? Reason { get; init; }
    public bool Hybrid { get; init; }
    public int LogicalProcessors { get; init; }
    public int PhysicalCores { get; init; }
    public int Groups { get; init; }
    public int PerformanceClass { get; init; } = -1;
    public IReadOnlyList<LogicalCoreInfo> Cores { get; init; } = Array.Empty<LogicalCoreInfo>();
    public string Description { get; init; } = "";

    public IReadOnlyList<int> PrimaryLps => Hybrid
        ? Cores.Where(c => c.EfficiencyClass == PerformanceClass).SelectMany(c => c.Lps).ToList()
        : Cores.SelectMany(c => c.Lps).ToList();
}

/// <summary>
/// CPU topology discovery and interrupt-affinity mask derivation (spec §3.2 D-TOPO / §3.3 D-AFF).
///
/// Hybrid classification is CALIBRATED, not assumed. CPUID.1A core type and
/// GetSystemCpuSetInformation EfficiencyClass are sampled independently and must describe the
/// same partition; a mismatch — including an inverted ordering — is reported as
/// E-HYBRID-AMBIGUOUS rather than guessed. This directly addresses error E-1.
///
/// The affinity mask is built only when Groups == 1 and LogicalProcessors &lt;= 64 (E-GRP-GT64),
/// because AssignmentSetOverride is an 8-byte group-0 mask. It never contains CPU 0, never
/// contains two SMT siblings of the same physical core, and never contains more than two bits.
/// </summary>
public static class CpuTopologyService
{
    private const int RelationProcessorCore = 0;
    private const int RelationGroup = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetLogicalProcessorInformationEx(int relationshipType, IntPtr buffer, ref uint returnedLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemCpuSetInformation(IntPtr info, uint length, out uint returnedLength, IntPtr process, uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr affinityMask);

    public static CpuTopologyResult Discover()
    {
        try
        {
            var coreMasks = ReadCoreRelationships();
            var groupCount = ReadGroupCount();
            if (coreMasks.Count == 0)
                return new CpuTopologyResult { Ok = false, Reason = "E-CPU-UNKNOWN", Description = "Processor core topology unavailable." };

            var cpuSets = ReadCpuSets();
            var cores = new List<LogicalCoreInfo>();
            int lpCount = 0;
            foreach (var (group, mask) in coreMasks)
            {
                var lps = Bits(mask).ToList();
                if (lps.Count == 0) continue;
                lpCount += lps.Count;
                byte eff = 0;
                byte l3 = 0;
                if (cpuSets.TryGetValue((group, lps[0]), out var cs)) { eff = cs.Efficiency; l3 = cs.LastLevelCache; }
                cores.Add(new LogicalCoreInfo(group, lps[0], lps, eff, l3));
            }

            int physical = cores.Count;
            int distinctClasses = cores.Select(c => c.EfficiencyClass).Distinct().Count();

            bool hybrid;
            int perfClass = -1;
            string? hybridReason = null;

            if (distinctClasses <= 1)
            {
                hybrid = false; // single class is a valid non-hybrid result
            }
            else
            {
                hybrid = true;
                var maxClass = cores.Max(c => c.EfficiencyClass);
                var minClass = cores.Min(c => c.EfficiencyClass);
                var effMax = cores.Where(c => c.EfficiencyClass == maxClass).SelectMany(c => c.Lps).ToHashSet();
                var effMin = cores.Where(c => c.EfficiencyClass == minClass).SelectMany(c => c.Lps).ToHashSet();

                // CPUID.1A sample per logical processor in group 0 (affinity masks are group-0 only).
                var byCpuIdType = new Dictionary<int, HashSet<int>>();
                bool sampleFailed = false;
                foreach (var lp in effMax.Concat(effMin).Distinct())
                {
                    if (lp >= 64) { sampleFailed = true; break; }
                    byte type = CpuIdCoreType(lp, out bool ok);
                    if (!ok || (type != 0x40 && type != 0x20)) { sampleFailed = true; break; }
                    if (!byCpuIdType.TryGetValue(type, out var set)) { set = new HashSet<int>(); byCpuIdType[type] = set; }
                    set.Add(lp);
                }

                if (sampleFailed || !byCpuIdType.TryGetValue(0x40, out var pSet) || !byCpuIdType.TryGetValue(0x20, out var eSet))
                {
                    hybridReason = "E-HYBRID-AMBIGUOUS";
                }
                else if (pSet.SetEquals(effMax) && eSet.SetEquals(effMin))
                {
                    perfClass = maxClass;                 // calibrated: higher EfficiencyClass = performance
                }
                else if (pSet.SetEquals(effMin) && eSet.SetEquals(effMax))
                {
                    perfClass = minClass;                 // calibrated: firmware inverted the ordering
                }
                else
                {
                    hybridReason = "E-HYBRID-AMBIGUOUS";  // partitions disagree
                }
            }

            string desc = $"{physical} physical cores / {lpCount} logical processors, " +
                          $"{groupCount} group(s), " +
                          (hybrid ? (perfClass >= 0 ? $"hybrid (performance class {perfClass})" : "hybrid (uncalibrated)")
                                  : "non-hybrid");

            return new CpuTopologyResult
            {
                Ok = true,
                Hybrid = hybrid,
                LogicalProcessors = lpCount,
                PhysicalCores = physical,
                Groups = groupCount,
                PerformanceClass = perfClass,
                Cores = cores,
                Description = hybridReason is null ? desc : desc + " — " + hybridReason
            };
        }
        catch (Exception ex)
        {
            SafeLog.Write("CPU topology discovery failed", ex);
            return new CpuTopologyResult { Ok = false, Reason = "E-CPU-UNKNOWN", Description = "Topology discovery threw: " + ex.Message };
        }
    }

    /// <summary>
    /// Derives the 8-byte little-endian AssignmentSetOverride mask per §3.3.
    /// Returns false with a reason code when a gate rejects the machine.
    /// </summary>
    public static bool DeriveAffinity(CpuTopologyResult t, out byte[]? mask, out IReadOnlyList<int> chosenLps, out string? reason, out bool reduced, int wanted = 2)
    {
        mask = null;
        chosenLps = Array.Empty<int>();
        reason = null;
        reduced = false;

        if (!t.Ok) { reason = t.Reason ?? "E-CPU-UNKNOWN"; return false; }
        if (t.PhysicalCores < 2) { reason = "E-TOPO-DEGENERATE"; return false; }
        if (t.LogicalProcessors < 4) { reason = "E-TOPO-DEGENERATE"; return false; }
        if (t.Groups != 1 || t.LogicalProcessors > 64) { reason = "E-GRP-GT64"; return false; }
        if (t.Hybrid && t.PerformanceClass < 0) { reason = "E-HYBRID-AMBIGUOUS"; return false; }

        var eligible = t.Cores.Where(c => !t.Hybrid || c.EfficiencyClass == t.PerformanceClass).ToList();
        var candidates = eligible.Where(c => c.FirstLp != 0).ToList();

        if (candidates.Count == 0)
        {
            // L1: every performance core hosts CPU 0 (single-cluster part) — use any non-CPU-0 core.
            candidates = t.Cores.Where(c => c.FirstLp != 0).ToList();
            reduced = true;
        }
        if (candidates.Count == 0) { reason = "E-TOPO-DEGENERATE"; return false; }

        // Prefer distinct L3 groups, then distinct physical cores.
        var chosen = new List<LogicalCoreInfo>();
        foreach (var grp in candidates.GroupBy(c => c.L3Key))
        {
            chosen.Add(grp.First());
            if (chosen.Count >= wanted) break;
        }
        foreach (var c in candidates)
        {
            if (chosen.Count >= wanted) break;
            if (!chosen.Contains(c)) chosen.Add(c);
        }
        if (chosen.Count == 0) { reason = "E-TOPO-DEGENERATE"; return false; }
        if (chosen.Count < wanted) reduced = true;

        ulong bits = 0;
        foreach (var c in chosen) bits |= 1UL << c.FirstLp;
        if (bits == 0 || (bits & 1UL) != 0) { reason = "E-TOPO-DEGENERATE"; return false; } // never CPU 0

        var bytes = new byte[8];
        for (int i = 0; i < 8; i++) bytes[i] = (byte)((bits >> (i * 8)) & 0xFF);
        mask = bytes;
        chosenLps = chosen.Select(c => c.FirstLp).ToList();
        return true;
    }

    // ---------------------------------------------------------------- native parsing

    private static List<(int Group, ulong Mask)> ReadCoreRelationships()
    {
        var result = new List<(int, ulong)>();
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref len);
        if (len == 0) return result;
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buf, ref len)) return result;
            int offset = 0;
            while (offset + 8 <= len)
            {
                int size = Marshal.ReadInt32(buf, offset + 4);
                if (size <= 0 || offset + size > len) break;
                int b = offset + 8;
                ushort groupCount = unchecked((ushort)Marshal.ReadInt16(buf, b + 22));
                for (int g = 0; g < groupCount; g++)
                {
                    int ga = b + 24 + g * 16;
                    if (ga + 8 > offset + size) break;
                    ulong mask = unchecked((ulong)Marshal.ReadInt64(buf, ga));
                    int group = unchecked((ushort)Marshal.ReadInt16(buf, ga + 8));
                    result.Add((group, mask));
                }
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    private static int ReadGroupCount()
    {
        uint len = 0;
        GetLogicalProcessorInformationEx(RelationGroup, IntPtr.Zero, ref len);
        if (len == 0) return 1;
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationGroup, buf, ref len)) return 1;
            if (len < 12) return 1;
            return unchecked((ushort)Marshal.ReadInt16(buf, 8 + 2)); // ActiveGroupCount
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static Dictionary<(int Group, int Lp), (byte Efficiency, byte LastLevelCache)> ReadCpuSets()
    {
        var map = new Dictionary<(int, int), (byte, byte)>();
        uint len = 0;
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
        if (len == 0) return map;
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetSystemCpuSetInformation(buf, len, out uint returned, IntPtr.Zero, 0)) return map;
            int offset = 0;
            while (offset + 20 <= returned)
            {
                int size = Marshal.ReadInt32(buf, offset);
                int type = Marshal.ReadInt32(buf, offset + 4);
                if (size <= 0 || offset + size > returned) break;
                if (type == 0)
                {
                    int group = unchecked((ushort)Marshal.ReadInt16(buf, offset + 12));
                    byte lpIndex = Marshal.ReadByte(buf, offset + 14);
                    byte l3 = Marshal.ReadByte(buf, offset + 16);
                    byte eff = Marshal.ReadByte(buf, offset + 18);
                    map[(group, lpIndex)] = (eff, l3);
                }
                offset += size;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    private static IEnumerable<int> Bits(ulong mask)
    {
        for (int i = 0; i < 64; i++) if ((mask & (1UL << i)) != 0) yield return i;
    }

    /// <summary>CPUID.1A core type for one logical processor: 0x40 = P-core, 0x20 = E-core.</summary>
    private static byte CpuIdCoreType(int lp, out bool ok)
    {
        ok = false;
        if (!X86Base.IsSupported || lp is < 0 or >= 64) return 0;
        var prev = SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)(1UL << lp));
        if (prev == UIntPtr.Zero) return 0;
        try
        {
            var r = X86Base.CpuId(0x1A, 0);
            ok = true;
            return (byte)((r.Eax >> 24) & 0xFF);
        }
        finally { SetThreadAffinityMask(GetCurrentThread(), prev); }
    }
}
