using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SwiftwaveTweaks.Core;
using SwiftwaveTweaks.Services;

namespace SwiftwaveTweaks.Tests;

/// <summary>
/// Pre-UI-integration self test. Runs synthetic gate tests, a real registry apply/restore
/// round-trip, journal-only state checks, an absent-service skip, a partial-failure rollback,
/// and live CPU topology discovery. Invoke with <c>--v3-selftest</c>; results are written to
/// %LocalAppData%\SwiftwaveTweaks\v3-selftest.txt and the process exits with 0/1.
/// </summary>
internal static class V3SelfTest
{
    private static readonly List<string> Lines = new();
    private static int _pass, _fail;

    public static int Run()
    {
        Lines.Clear(); _pass = 0; _fail = 0;
        var journalDir = Path.Combine(Path.GetTempPath(), "swiftwave-selftest-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("SWIFTWAVE_JOURNAL_DIR", journalDir);

        try
        {
            EmbeddedAssets();
            UiIntegration();
            TopologyGates();
            JournalState();
            RegistryRoundTrip();
            AbsentServiceSkip();
            PartialFailureRollback();
            FeatureConflictC1();
            StartupRegistration();
            RealFeatureApplyRestore();
            LiveMachine();
        }
        catch (Exception ex)
        {
            Fail("harness", "unhandled: " + ex);
        }
        finally
        {
            try { Directory.Delete(journalDir, recursive: true); } catch { }
        }

        string report = $"V3 self-test: {_pass} passed, {_fail} failed{Environment.NewLine}" +
                        string.Join(Environment.NewLine, Lines);
        try
        {
            string outDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks");
            System.IO.Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "v3-selftest.txt"), report);
        }
        catch { }
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ tests

    private static void EmbeddedAssets()
    {
        const string expectedSha = "62AC159AB77F228D022CED2DAC447BF4B7371CFF9CFBE099742842FBB3B2A3BB";

        Check("asset: power plan is embedded", PowerPlanAsset.IsEmbedded, null);

        string? path = PowerPlanAsset.ExtractToTemp(out var error);
        Check("asset: extraction succeeds", path is not null, error);
        if (path is null) return;

        byte[] bytes = File.ReadAllBytes(path);
        Check("asset: extracted size is 12,288 bytes", bytes.Length == 12288, bytes.Length.ToString());
        Check("asset: SHA-256 matches the supplied plan",
            Convert.ToHexString(SHA256.HashData(bytes)) == expectedSha, null);
        Check("asset: extracted outside the executable directory",
            !path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), path);

        PowerPlanAsset.TryDelete(path);
        Check("asset: temp file removed after use", !File.Exists(path), null);
        Check("asset: no Assets folder beside the executable",
            !Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Assets")),
            Path.Combine(AppContext.BaseDirectory, "Assets"));
    }

    private static void TopologyGates()
    {
        var ok = new CpuTopologyResult
        {
            Ok = true, Hybrid = false, LogicalProcessors = 8, PhysicalCores = 4, Groups = 1,
            Cores = Cores(4, 0, smt: 0)
        };
        bool r = CpuTopologyService.DeriveAffinity(ok, out var mask, out var lps, out var reason, out _);
        Check("supported desktop: mask derived", r, reason);
        Check("supported desktop: no CPU 0 bit", mask is not null && (mask[0] & 1) == 0, null);
        Check("supported desktop: at most 2 bits", lps.Count is >= 1 and <= 2, $"{lps.Count}");
        Check("supported desktop: 8-byte mask", mask?.Length == 8, $"{mask?.Length}");

        var hybrid = new CpuTopologyResult
        {
            Ok = true, Hybrid = true, PerformanceClass = 1, LogicalProcessors = 12, PhysicalCores = 6, Groups = 1,
            Cores = new List<LogicalCoreInfo>
            {
                new(0, 0, new[]{0,1}, 1, 0), new(0, 2, new[]{2,3}, 1, 0), new(0, 4, new[]{4,5}, 1, 0),
                new(0, 8, new[]{8,9}, 0, 0), new(0, 10, new[]{10,11}, 0, 0),
            }
        };
        r = CpuTopologyService.DeriveAffinity(hybrid, out mask, out lps, out reason, out _);
        Check("hybrid calibrated: mask from performance class", r && lps.All(x => x is 2 or 4), reason);

        var ambiguous = new CpuTopologyResult
        {
            Ok = true, Hybrid = true, PerformanceClass = -1, LogicalProcessors = 8, PhysicalCores = 4, Groups = 1,
            Cores = Cores(4, 0, 0)
        };
        r = CpuTopologyService.DeriveAffinity(ambiguous, out _, out _, out reason, out _);
        Check("ambiguous hybrid → E-HYBRID-AMBIGUOUS", !r && reason == "E-HYBRID-AMBIGUOUS", reason);

        var over64 = new CpuTopologyResult
        {
            Ok = true, Hybrid = false, LogicalProcessors = 128, PhysicalCores = 64, Groups = 1,
            Cores = Cores(64, 0, smt: 0)
        };
        r = CpuTopologyService.DeriveAffinity(over64, out _, out _, out reason, out _);
        Check("LP > 64 → E-GRP-GT64", !r && reason == "E-GRP-GT64", reason);

        var multiGroup = new CpuTopologyResult
        {
            Ok = true, Hybrid = false, LogicalProcessors = 64, PhysicalCores = 32, Groups = 2,
            Cores = Cores(32, 0, smt: 0)
        };
        r = CpuTopologyService.DeriveAffinity(multiGroup, out _, out _, out reason, out _);
        Check("multiple groups → E-GRP-GT64", !r && reason == "E-GRP-GT64", reason);

        var oneCore = new CpuTopologyResult
        {
            Ok = true, Hybrid = false, LogicalProcessors = 2, PhysicalCores = 1, Groups = 1,
            Cores = Cores(1, 0, smt: 0)
        };
        r = CpuTopologyService.DeriveAffinity(oneCore, out _, out _, out reason, out _);
        Check("1 physical core → E-TOPO-DEGENERATE", !r && reason == "E-TOPO-DEGENERATE", reason);

        var twoLp = new CpuTopologyResult
        {
            Ok = true, Hybrid = false, LogicalProcessors = 2, PhysicalCores = 4, Groups = 1,
            Cores = Cores(4, 0, smt: 0)
        };
        r = CpuTopologyService.DeriveAffinity(twoLp, out _, out _, out reason, out _);
        Check("LP < 4 → E-TOPO-DEGENERATE", !r && reason == "E-TOPO-DEGENERATE", reason);

        var unreadable = new CpuTopologyResult { Ok = false, Reason = "E-CPU-UNKNOWN" };
        r = CpuTopologyService.DeriveAffinity(unreadable, out _, out _, out reason, out _);
        Check("unreadable topology → E-CPU-UNKNOWN", !r && reason == "E-CPU-UNKNOWN", reason);
    }

    private static List<LogicalCoreInfo> Cores(int count, int unused, int smt)
    {
        var list = new List<LogicalCoreInfo>();
        for (int i = 0; i < count; i++)
        {
            int first = i * 2;
            list.Add(new LogicalCoreInfo(0, first, new[] { first, first + 1 }, 0, 0));
        }
        return list;
    }

    private static void JournalState()
    {
        string id = "selftest-journal";
        const string target = "REG:HKCU\\SelfTest\\Value";
        Journal.Commit(id, new[]
        {
            new JournalTarget { Target = target, Kind = "DWord", Prior = "2", New = "4" }
        });

        var match = V3State.Evaluate(id, new[] { (target, (string?)"4") }, "optimized", "unapplied");
        Check("journal: committed + matching → Optimized", match.State == OptState.Optimized, match.CurrentState);

        var foreign = V3State.Evaluate(id, new[] { (target, (string?)"3") }, "optimized", "unapplied");
        Check("journal: mismatch → E-FOREIGN-EDIT", foreign.Reason == "E-FOREIGN-EDIT", foreign.Reason);

        var other = V3State.Evaluate("selftest-other", new[] { (target, (string?)"4") }, "optimized", "unapplied");
        Check("journal: matching value with no transaction → Unapplied (C4)",
            other.State == OptState.NeedsAttention && other.Reason == "", other.CurrentState);

        Journal.MarkRolledBack(id);
        var rolled = V3State.Evaluate(id, new[] { (target, (string?)"4") }, "optimized", "unapplied");
        Check("journal: rolled back → Unapplied", rolled.State == OptState.NeedsAttention, rolled.CurrentState);
    }

    private static void RegistryRoundTrip()
    {
        string keyPath = @"Software\SwiftwaveTweaks\SelfTest\" + Guid.NewGuid().ToString("N");
        using (var seed = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
        {
            seed.SetValue("DWordVal", 7, RegistryValueKind.DWord);
            seed.SetValue("StringVal", "%TEMP%\\seed", RegistryValueKind.ExpandString);
            seed.SetValue("BinVal", new byte[] { 0xAA, 0xBB }, RegistryValueKind.Binary);
        }

        var ops = new List<Operation>
        {
            new(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default, keyPath, "DWordVal", IntValue: 9),
            new(OpKind.RegString, RegistryHive.CurrentUser, RegistryView.Default, keyPath, "StringVal", StringValue: "new"),
            new(OpKind.RegBinary, RegistryHive.CurrentUser, RegistryView.Default, keyPath, "BinVal", BinaryValue: new byte[] { 1, 2, 3 }),
            new(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default, keyPath, "AbsentVal", IntValue: 5),
        };
        var opt = new TestMultiOp("selftest-registry", ops);

        for (int cycle = 1; cycle <= 2; cycle++)
        {
            var applied = opt.Apply();
            Check($"registry cycle {cycle}: applied", applied.Status == ApplyStatus.Applied, applied.Status + " " + applied.Message);

            using (var k = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Check($"registry cycle {cycle}: DWord written", (int?)k?.GetValue("DWordVal") == 9, null);
                Check($"registry cycle {cycle}: Binary written",
                    k?.GetValue("BinVal") is byte[] b && b.SequenceEqual(new byte[] { 1, 2, 3 }), null);
                Check($"registry cycle {cycle}: absent value created", (int?)k?.GetValue("AbsentVal") == 5, null);
            }

            var record = RollbackStore.Read().FirstOrDefault(x => x.Id == "selftest-registry");
            Check($"registry cycle {cycle}: rollback record stored", record is not null, null);
            if (record is null) break;

            var rolled = opt.Rollback(record.PreviousState);
            Check($"registry cycle {cycle}: rolled back", rolled.Status == ApplyStatus.Applied, rolled.Message + " " + rolled.Detail);

            using var after = Registry.CurrentUser.OpenSubKey(keyPath);
            Check($"registry cycle {cycle}: DWord type+data restored",
                after?.GetValueKind("DWordVal") == RegistryValueKind.DWord && (int?)after.GetValue("DWordVal") == 7, null);
            Check($"registry cycle {cycle}: ExpandString type preserved",
                after?.GetValueKind("StringVal") == RegistryValueKind.ExpandString &&
                (string?)after.GetValue("StringVal", null, RegistryValueOptions.DoNotExpandEnvironmentNames) == "%TEMP%\\seed",
                $"{after?.GetValueKind("StringVal")}");
            Check($"registry cycle {cycle}: binary restored",
                after?.GetValue("BinVal") is byte[] rb && rb.SequenceEqual(new byte[] { 0xAA, 0xBB }), null);
            Check($"registry cycle {cycle}: absent value removed",
                after?.GetValueNames().Contains("AbsentVal") == false, null);

            RollbackStore.Remove("selftest-registry");
        }

        try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); } catch { }
    }

    private static void AbsentServiceSkip()
    {
        string svc = "SwiftwaveSelfTestNonexistent_" + Guid.NewGuid().ToString("N")[..8];
        string path = $@"SYSTEM\CurrentControlSet\Services\{svc}";
        var opt = new TestMultiOp("selftest-absent-service", new[]
        {
            new Operation(OpKind.ServiceStart, RegistryHive.LocalMachine, RegistryView.Registry64,
                path, "Start", IntValue: 4)
        });

        Journal.Commit(opt.Id, Array.Empty<JournalTarget>());
        var emptyState = opt.Detect();
        Check("journal: empty transaction → Optimized", emptyState.State == OptState.Optimized,
            emptyState.CurrentState + " committed=" + (Journal.LatestCommitted(opt.Id) is not null));
        Journal.MarkRolledBack(opt.Id);

        var result = opt.Apply();
        string diag = $"committed={(Journal.LatestCommitted(opt.Id) is not null)} corrupt={Journal.IsCorrupt}";
        Check("absent service: apply still succeeds", result.Status == ApplyStatus.Applied,
            result.Message + " | " + diag);
        Check("absent service: no phantom service key created",
            !RegHelper.KeyExists(RegistryHive.LocalMachine, RegistryView.Registry64, path), null);

        var record = RollbackStore.Read().FirstOrDefault(x => x.Id == "selftest-absent-service");
        if (record is not null)
        {
            opt.Rollback(record.PreviousState);
            RollbackStore.Remove("selftest-absent-service");
        }
    }

    private static void PartialFailureRollback()
    {
        string keyPath = @"Software\SwiftwaveTweaks\SelfTest\" + Guid.NewGuid().ToString("N");
        using (var seed = Registry.CurrentUser.CreateSubKey(keyPath, writable: true))
            seed.SetValue("Good", 1, RegistryValueKind.DWord);

        var ops = new List<Operation>
        {
            new(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default, keyPath, "Good", IntValue: 2),
            new(OpKind.NetshCommand, Command: "int tcp set global rss=definitely_invalid_value"),
        };
        var opt = new TestMultiOp("selftest-partial", ops);
        var result = opt.Apply();

        Check("partial failure: apply reports failure", result.Status == ApplyStatus.Failed, result.Status.ToString());
        Check("partial failure: detail names E-PARTIAL-FAILURE",
            result.Detail?.Contains("E-PARTIAL-FAILURE", StringComparison.Ordinal) == true, result.Detail);
        using (var k = Registry.CurrentUser.OpenSubKey(keyPath))
            Check("partial failure: earlier write rolled back", (int?)k?.GetValue("Good") == 1, $"{k?.GetValue("Good")}");
        Check("partial failure: no rollback record stored",
            RollbackStore.Read().All(x => x.Id != "selftest-partial"), null);

        try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); } catch { }
    }

    private static void FeatureConflictC1()
    {
        string keyPath = @"Software\SwiftwaveTweaks\SelfTest\" + Guid.NewGuid().ToString("N");
        var opt = new TestMultiOp("selftest-c1", new[]
        {
            new Operation(OpKind.RegDword, RegistryHive.CurrentUser, RegistryView.Default, keyPath, "V", IntValue: 1)
        });

        var first = opt.Apply();
        var second = opt.Apply();
        Check("C1: first apply succeeds", first.Status == ApplyStatus.Applied, first.Status.ToString());
        Check("C1: second apply is AlreadyOptimized", second.Status == ApplyStatus.AlreadyOptimized, second.Status.ToString());

        var record = RollbackStore.Read().FirstOrDefault(x => x.Id == "selftest-c1");
        if (record is not null)
        {
            opt.Rollback(record.PreviousState);
            RollbackStore.Remove("selftest-c1");
        }
        try { Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); } catch { }
    }

    private static void RealFeatureApplyRestore()
    {
        var opt = new DynamicInterruptSteeringOptimization();
        var before = opt.Detect();
        if (before.State == OptState.Unsupported)
        {
            Lines.Add($"[real] F6 unsupported on this host: {before.Reason} (recorded, not a test failure)");
            return;
        }

        var applied = opt.Apply();
        if (applied.Status == ApplyStatus.Failed &&
            (applied.Detail?.Contains("denied", StringComparison.OrdinalIgnoreCase) == true))
        {
            Lines.Add("[real] F6 apply blocked: process is not elevated; HKLM write cannot be exercised " +
                      "(recorded, not a test failure)");
            return;
        }
        Check("F6 real apply + verify", applied.Status == ApplyStatus.Applied,
            applied.Message + " " + applied.Detail);
        if (applied.Status != ApplyStatus.Applied) return;

        var record = RollbackStore.Read().FirstOrDefault(x => x.Id == opt.Id);
        Check("F6 rollback record stored", record is not null, null);
        if (record is null) return;

        var rolled = opt.Rollback(record.PreviousState);
        Check("F6 real rollback + verify", rolled.Status == ApplyStatus.Applied, rolled.Message + " " + rolled.Detail);
        RollbackStore.Remove(opt.Id);

        var reapplied = opt.Apply();
        Check("F6 repeated apply", reapplied.Status == ApplyStatus.Applied, reapplied.Message);
        var record2 = RollbackStore.Read().FirstOrDefault(x => x.Id == opt.Id);
        if (record2 is not null)
        {
            opt.Rollback(record2.PreviousState);
            RollbackStore.Remove(opt.Id);
        }
    }

    private static void LiveMachine()
    {
        var t = CpuTopologyService.Discover();
        Lines.Add($"[live] topology: Ok={t.Ok} reason={t.Reason ?? "-"} groups={t.Groups} " +
                  $"lp={t.LogicalProcessors} cores={t.PhysicalCores} hybrid={t.Hybrid} perfClass={t.PerformanceClass}");
        if (t.Ok)
        {
            bool r = CpuTopologyService.DeriveAffinity(t, out var mask, out var lps, out var reason, out _);
            Lines.Add($"[live] affinity: ok={r} reason={reason ?? "-"} lps=[{string.Join(",", lps)}] " +
                      $"mask={(mask is null ? "-" : Convert.ToHexString(mask))}");
            if (r) _pass++; else Lines.Add("[live] affinity rejected by a gate (recorded, not a test failure)");
        }
        else
            Lines.Add("[live] topology unavailable on this machine (recorded, not a test failure)");

        var catalog = OptimizationCatalog.Build(null);
        foreach (var id in new[] { "debloat", "v3-device-affinity" })
        {
            var opt = catalog.FirstOrDefault(o => o.Id == id);
            if (opt is null) { Lines.Add($"[live] {id}: not registered"); continue; }
            try
            {
                var d = opt.Detect();
                Lines.Add($"[live] {id}: state={d.State} current='{d.CurrentState}' " +
                          $"reason='{d.Reason}' selectable={d.Selectable}");
            }
            catch (Exception ex) { Lines.Add($"[live] {id}: detect threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ UI integration

    /// <summary>The eight V3 features exactly as the UI, presets, logs and documentation name them.</summary>
    private static readonly (string Id, string Name, string Category)[] V3Features =
    [
        ("v3-fps-input-lag", "General Windows FPS & Input Delay Optimizations", "Gaming"),
        ("v3-device-affinity", "Optimize Device Affinities", "Gaming"),
        ("v3-msi", "Optimize Message Signal Interrupts", "Gaming"),
        ("v3-network-performance", "Optimize Network Performance", "Network"),
        ("v3-dynamic-interrupt-steering", "Disable Dynamic Interrupt Steering", "Gaming"),
        ("v3-storage-memory", "Optimize Storage & Memory", "Storage"),
        ("debloat", "Debloat Windows & Reduce Background Activity", "Gaming"),
        ("v3-max-performance-power", "Enable Maximum Performance Power Mode", "Power"),
    ];

    /// <summary>
    /// Integration checks that do not need an elevated session: every V3 feature is registered
    /// once under the name and category its page filters on, retired cards are hidden from pages
    /// but remain resolvable by History, and every preset resolves without duplicates or
    /// references to retired features.
    /// </summary>
    private static void UiIntegration()
    {
        var catalog = OptimizationCatalog.Build(null);

        foreach (var f in V3Features)
        {
            var matches = catalog.Where(o => o.Id == f.Id).ToList();
            Check($"ui: {f.Id} registered exactly once", matches.Count == 1, $"count={matches.Count}");
            if (matches.Count != 1) continue;

            Check($"ui: {f.Id} name/category match the UI contract",
                matches[0].Name == f.Name && matches[0].Category == f.Category,
                $"{matches[0].Name} / {matches[0].Category}");
            Check($"ui: {f.Id} requires administrator", matches[0].RequiresAdmin, null);

            try
            {
                var d = matches[0].Detect();
                Check($"ui: {f.Id} detection returns a legal state",
                    Enum.IsDefined(d.State) && d.CurrentState.Length > 0, $"{d.State} / {d.CurrentState}");
            }
            catch (Exception ex) { Fail($"ui: {f.Id} detection threw", ex.Message); }
        }

        Check("ui: optimization ids are unique",
            catalog.Select(o => o.Id).Distinct().Count() == catalog.Count, $"{catalog.Count} entries");

        foreach (var retired in new[] { "power-plan", "windows-suggestions" })
        {
            var m = catalog.Where(o => o.Id == retired).ToList();
            Check($"ui: retired '{retired}' is hidden but resolvable",
                m.Count == 1 && m[0].Category == "Legacy",
                $"count={m.Count} category={m.FirstOrDefault()?.Category ?? "-"}");
        }

        foreach (var preset in Presets.All)
        {
            var members = Presets.Resolve(preset, catalog);
            Check($"preset '{preset.Id}': every member resolves",
                members.Count > 0 && preset.OptimizationIds.All(id => id == "storage" || members.Any(m => m.Id == id)),
                $"{members.Count} member(s)");
            Check($"preset '{preset.Id}': no retired feature",
                !preset.OptimizationIds.Contains("power-plan") &&
                !preset.OptimizationIds.Contains("windows-suggestions"), null);
            Check($"preset '{preset.Id}': no duplicate member",
                members.Select(m => m.Id).Distinct().Count() == members.Count, null);
        }

        var recommended = Presets.Resolve(Presets.Recommended, catalog).Select(m => m.Id).ToList();
        Check("preset 'recommended': includes the maximum-performance power mode",
            recommended.Contains("v3-max-performance-power"), null);
        Check("preset 'recommended': excludes advanced device/service tuning",
            !recommended.Contains("v3-device-affinity") && !recommended.Contains("v3-msi") &&
            !recommended.Contains("debloat") && !recommended.Contains("v3-storage-memory"), null);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Exercises the real HKCU Run registration (Bug 2). The original value is restored in a
    /// finally block, so a failed assertion cannot leave the machine registered.
    /// </summary>
    private static void StartupRegistration()
    {
        const string runKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        string? original = RegHelper.ReadString(RegistryHive.CurrentUser, RegistryView.Default, runKey, SelfStartup.ValueName);
        try
        {
            // A value that points at a file which no longer exists must not read as "on".
            RegHelper.WriteString(RegistryHive.CurrentUser, runKey, SelfStartup.ValueName,
                "\"" + Path.Combine(Path.GetTempPath(), "swiftwave-not-installed.exe") + "\"");
            Check("startup: dead target detected as mismatched",
                SelfStartup.GetState() == SelfStartup.State.Mismatched, SelfStartup.RegisteredCommand);

            var enable = SelfStartup.SetEnabled(true);
            Check("startup: enable succeeds", enable.Success, enable.Message);
            string? expected = SelfStartup.CurrentCommand;
            string? written = SelfStartup.RegisteredCommand;
            Check("startup: entry points at the running executable",
                expected is not null && string.Equals(written, expected, StringComparison.Ordinal), written);
            Check("startup: command is quoted for paths with spaces",
                written is not null && written.StartsWith('"') &&
                written.EndsWith(SelfStartup.StartupArgument, StringComparison.Ordinal), written);
            Check("startup: state is enabled", SelfStartup.GetState() == SelfStartup.State.Enabled, null);

            // Enabling twice must not add a second value or change the command.
            var again = SelfStartup.SetEnabled(true);
            using (var key = Registry.CurrentUser.OpenSubKey(runKey))
            {
                int matches = key?.GetValueNames()
                    .Count(n => string.Equals(n, SelfStartup.ValueName, StringComparison.OrdinalIgnoreCase)) ?? 0;
                Check("startup: enable is idempotent (one value, unchanged)",
                    again.Success && matches == 1 &&
                    string.Equals(key?.GetValue(SelfStartup.ValueName) as string, written, StringComparison.Ordinal),
                    $"values={matches}");
            }

            var disable = SelfStartup.SetEnabled(false);
            Check("startup: disable succeeds", disable.Success, disable.Message);
            Check("startup: entry removed", SelfStartup.RegisteredCommand is null, SelfStartup.RegisteredCommand);
            Check("startup: disable is idempotent", SelfStartup.SetEnabled(false).Success, null);
        }
        finally
        {
            try
            {
                if (original is null)
                    RegHelper.DeleteValue(RegistryHive.CurrentUser, runKey, SelfStartup.ValueName);
                else
                    RegHelper.WriteString(RegistryHive.CurrentUser, runKey, SelfStartup.ValueName, original);
            }
            catch { }
        }
    }

    private static void Check(string name, bool ok, string? detail)
    {
        if (ok) { _pass++; Lines.Add($"PASS  {name}"); }
        else { _fail++; Lines.Add($"FAIL  {name}" + (detail is null ? "" : $" — {detail}")); }
    }

    private static void Fail(string name, string detail)
    {
        _fail++;
        Lines.Add($"FAIL  {name} — {detail}");
    }

    private sealed class TestMultiOp : MultiOpOptimization
    {
        private readonly string _id;
        private readonly List<Operation> _ops;

        public TestMultiOp(string id, IEnumerable<Operation> ops)
        {
            _id = id;
            _ops = ops.ToList();
            RollbackStore.Remove(id);
        }

        public override string Id => _id;
        public override string Name => "Self-test " + _id;
        public override string Description => "Self-test fixture.";
        public override string Category => "Test";
        public override Safety Safety => Safety.Safe;
        public override bool RequiresAdmin => false;

        protected override IReadOnlyList<Operation> Operations => _ops;

        public override Detection Detect() => JournalDetection("self-test applied");
    }
}
