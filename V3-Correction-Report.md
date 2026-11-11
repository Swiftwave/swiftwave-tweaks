# V3 Implementation Correction Pass — Report

Date: 2026-11-11
Scope: corrections required before UI integration. A clean Debug + Release build is treated as a
prerequisite, **not** as evidence of functional correctness.

---

## 1. Files modified

### New
| File | Purpose |
|---|---|
| `Services/Journal.cs` | Append-only transaction journal (`%LocalAppData%\SwiftwaveTweaks\journal.jsonl`), `WriteThrough` + `Flush(true)`; `Commit`, `MarkRolledBack`, `LatestCommitted`, `OwnerOf`, `ReadAll`, `IsCorrupt`. |
| `Services/V3State.cs` | Journal-only state evaluation (spec C3/C4): no transaction → Unapplied; committed + match → Optimized; committed + mismatch → `E-FOREIGN-EDIT`. |
| `Services/CpuTopologyService.cs` | `GetLogicalProcessorInformationEx` topology discovery, CPU-set EfficiencyClass sampling, CPUID hybrid calibration, affinity derivation with all gates. |
| `Services/PnpDeviceService.cs` | PnP enumeration, class GUIDs, boot-display detection, hard-deny IDs, VM/server/battery/chassis detection. |
| `Core/V3DeviceAffinity.cs` | F3 replacement (was `Core/V3PciInterrupts.cs`, deleted). |
| `Core/V3Msi.cs` | F4 split out of the former combined PCI class. |
| `Optimizations/DebloatOptimization.cs` | Merged debloat feature (§3). |
| `Tests/V3SelfTest.cs` | Headless `--v3-selftest` harness. |
| `Assets/Swiftwave-Ultimate-Performance.pow` | Bundled power plan (12,288 bytes) copied from the supplied V5 reconstruction. |

### Rewritten
| File | Change |
|---|---|
| `Services/PowerSchemeService.cs` | Locale-independent `/list` parsing, `/import` without GUID, duplicate, rename, activate, delete. |
| `Core/V3K6ad89bg.cs` | F9 desktop `.pow` import vs laptop High-Performance duplication; D-1-compliant rollback; 27-row AC/DC verification. |
| `Core/MultiOpOptimization.cs` | `PriorData`/`PriorKind` as strings (fixes `JsonElement` round-trip), journal commit, `JournalDetection`, `AdoptPrior`/`OnApplied`/`OnRolledBack`, `RestoreCommand`, absent-target skipping, partial-failure auto-undo. |
| `Core/V35Q8oOtIH.cs` | F1 rewritten as a journaling `MultiOpOptimization` with spec subset and exclusions. |
| `Core/V33WwEZo0M.cs` | F2 rewritten with the 4-LP / 8-GiB gate, dynamic svchost discovery, spec NTFS subset. |
| `Core/V3XLSEdxVW.cs` | F5 rewritten as `MultiOpOptimization`, crash removed, NIC identity fixed, inverse-netsh restore. |
| `Core/V3Optimizations.cs` | F6 `Detect()` now journal-based with build/VM gates. |

### Modified
| File | Change |
|---|---|
| `Services/RegHelper.cs` | Added `KeyExists`; `ReadValue` now uses `DoNotExpandEnvironmentNames` so `REG_EXPAND_SZ` is reversible. |
| `Services/ScheduledTaskService.cs` | Batch cache (`PrimeCache`), fixed `Exists` always-true bug, cache invalidation on writes. |
| `Optimizations/WindowsDebloatOptimization.cs` | Category → `Legacy` (history resolution only). |
| `Optimizations/StorageOptimization.cs` | Registers `DebloatOptimization` and the V3 backend features; legacy card retained as `Legacy`. |
| `MainWindow.xaml.cs` | Debloat section/filters now keyed on `debloat`. |
| `GameReadyOptimizer.csproj` | Ships the `.pow` asset next to `GameReady.ico`. |
| `App.xaml.cs` | `--v3-selftest` headless entry point. |

### Deleted
| File | Reason |
|---|---|
| `Core/V3PciInterrupts.cs` | Replaced by `V3DeviceAffinity.cs` + `V3Msi.cs`. |
| `Core/V39C0X9LiC.cs` | Absorbed into `DebloatOptimization`; was unreferenced. |
| `Core/V3Pe9bgwi0.cs` | Absorbed into `DebloatOptimization`; was unreferenced. |

---

## 2. Implementation decisions

### 2.1 Device affinity (F3)
- **No hardcoded CPU numbers anywhere.** `CpuTopologyService.Discover()` parses
  `RelationProcessorCore` and `RelationGroup` records from `GetLogicalProcessorInformationEx`,
  then reads CPU sets via `GetSystemCpuSetInformation` for group, LP index, LLC index and
  EfficiencyClass.
- **Hybrid calibration (E-1).** CPUID leaf `0x1A` is sampled on each logical processor. The
  observed P/E split set is compared to the EfficiencyClass partition. A machine is accepted
  only if the two partitions agree (either `EfficiencyClass == max` is the P set, or the inverse
  mapping). Otherwise `E-HYBRID-AMBIGUOUS`. `EfficiencyClass == 0` is never assumed to mean P.
- **E-2/E-3.** `E-HYBRID-AMBIGUOUS` is returned whenever hybrid hardware cannot be calibrated;
  no fallback mask is produced.
- **Gate order** in `DeriveAffinity`: unreadable topology (`E-CPU-UNKNOWN`) → `<2` physical cores
  or `<4` logical processors (`E-TOPO-DEGENERATE`) → `Groups != 1 || LogicalProcessors > 64`
  (`E-GRP-GT64`) → hybrid uncalibrated (`E-HYBRID-AMBIGUOUS`) → derive.
- **No group-0 leakage.** The derivation only runs on a single group of ≤ 64 logical processors,
  so a group-0-only mask can never be produced for a target in another group.
- **Mask shape.** Two bits at most, chosen from primary cores, never CPU 0, preferring distinct
  LLC domains; emitted as an 8-byte little-endian REG_BINARY.
- F3 requires a pre-existing `Affinity Policy` key; boot-display GPUs are skipped
  (`E-DEV-BOOT-DISPLAY`); a post-write PnP health query guards each device; per-device failures
  produce `E-PARTIAL-FAILURE`.

### 2.2 Power plans (F9)
- **Desktop:** `powercfg /import Assets\Swiftwave-Ultimate-Performance.pow` with no GUID (the
  plan supplies its own), diffing `/list` before and after to capture the new GUID, `/changename`
  to `Swiftwave Ultimate Performance`, then verification of the 27 AC/DC rows from the supplied
  comparison CSV, then `/setactive`.
- **Laptop:** `/duplicatescheme 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` →
  `Swiftwave High Performance`. The canonical Windows scheme is never modified.
- **Ambiguous form factor:** `E-CLASS-AMBIGUOUS` (documented extension — the closed set has no
  dedicated desktop/laptop code). Battery query failure falls back to chassis types; if both are
  unknown the feature fails closed instead of guessing.
- **Rollback (D-1):** the previously active GUID is captured and re-activated only if our scheme
  is still the active one; if the user changed the scheme externally we leave it alone. Only the
  Swiftwave-owned GUID is deleted.
- Hidden settings are preserved because the plan is imported as an opaque blob — no setting is
  reconstructed from the Hone registry findings.

### 2.3 Debloat consolidation (§3)
- One card: **"Debloat Windows & Reduce Background Activity"** (`debloat`).
- 9 HKCU ContentDeliveryManager values, 8 services (`WalletService`, `SEMgrSvc`, `PhoneSvc`,
  `AxInstSV`, `Ndu`, `PcaSvc`, `WerSvc`, `wercplsupport`), 33 scheduled-task paths, and the
  13 WER/AppCompat/device-install values.
- Legacy prior adoption: `AdoptPrior` reads the old `windows-suggestions` rollback record
  (`{"settings":[{"k","v"}]}`) so the true original CDM values are preserved instead of the
  post-optimization zeros. After a successful apply, the legacy record is removed —
  ownership transfers to `debloat`.
- Overlap removed: the five services formerly in F7 and the service/task set formerly in F8 are
  now one list, so each target has exactly one owner.

### 2.4 Framework fidelity fixes (§4)
- `OperationPriorState.PriorData` was `object?`; `System.Text.Json` round-tripped it to
  `JsonElement`, silently breaking restore. Now `string?` + `PriorKind` with explicit encoding
  (DWord decimal, String raw, Binary base64, MultiString JSON array).
- `REG_EXPAND_SZ` capture now uses `DoNotExpandEnvironmentNames`; restoring an expanded literal
  path would otherwise rewrite the user's `%VARS%`.
- Absent services/tasks: skipped and excluded from the journal, so no phantom `Services\<name>`
  key is created and no foreign-edit is reported.
- Partial failure: already-applied operations are undone in reverse before Apply returns
  `E-PARTIAL-FAILURE`.
- `ScheduledTaskService.Exists` returned true on any parse; fixed. Task queries are cached for 20 s
  and invalidated on every write.

---

## 3. Tests performed and actual results

Harness: `SwiftwaveTweaks.exe --v3-selftest` (headless, writes `%LocalAppData%\SwiftwaveTweaks\v3-selftest.txt`).
Run on the development host (hybrid CPU, 12 physical cores / 16 logical processors, 1 group).

```
V3 self-test: 45 passed, 0 failed
```

### 3.1 Hardware scenarios

| Scenario | Method | Result |
|---|---|---|
| Supported hardware (synthetic desktop) | 8 LP / 4 cores, non-hybrid | PASS — mask derived, CPU 0 excluded, ≤ 2 bits, 8-byte mask |
| Supported hardware (synthetic hybrid) | EfficiencyClass 1 = P set, CPUID agrees | PASS — mask chosen from performance class only |
| Live hardware | `Discover()` on this host | `Ok=True groups=1 lp=16 cores=12 hybrid=True perfClass=1`; affinity `ok=True lps=[2,4] mask=0x0000000000000014` |
| Unsupported hardware (unreadable topology) | `Ok=false` | PASS — `E-CPU-UNKNOWN` |
| Unsupported hardware (degenerate) | 1 core / 2 LP | PASS — `E-TOPO-DEGENERATE` |
| Ambiguous hardware | hybrid, `PerformanceClass=-1` | PASS — `E-HYBRID-AMBIGUOUS` |
| Processor groups | 2 groups / 64 LP | PASS — `E-GRP-GT64` |
| > 64 LPs | 1 group / 128 LP | PASS — `E-GRP-GT64` |

> The live mask is `0x14` (logical processors 2 and 4) computed from topology — not a hardcoded
> `2+3`, which would be `0x0C`.

### 3.2 Apply / restore / compatibility

| Test | Result |
|---|---|
| Typed registry round-trip (DWORD, REG_EXPAND_SZ, REG_BINARY, absent value) | PASS — exact kind + data restored, absent value deleted |
| Repeated apply/restore (2 full cycles) | PASS — both cycles applied and restored exactly |
| Journal: committed + matching | PASS — Optimized |
| Journal: committed + mismatch | PASS — `E-FOREIGN-EDIT` with observed value |
| Journal: matching values but no transaction (C4) | PASS — reported as Unapplied |
| Journal: after rollback | PASS — Unapplied |
| Journal: empty transaction | PASS — Optimized |
| Absent service target | PASS — applied successfully, no phantom service key, no foreign-edit |
| Partial failure (second op rejected) | PASS — apply fails, earlier write rolled back, detail carries `E-PARTIAL-FAILURE`, no rollback record |
| Feature conflict (C1) | PASS — first apply succeeds, second returns `AlreadyOptimized` |
| Real feature F6 (HKLM single value) | **Blocked** — harness process is not elevated; HKLM write denied. Recorded, not counted as a pass. |

### 3.3 Builds
- Debug: success, 0 warnings, 0 errors.
- Release: success, 0 warnings, 0 errors.

---

## 4. Remaining limitations

1. **Real-feature HKLM apply/restore was not exercised.** The self-test process runs unelevated,
   so the F6 end-to-end cycle stops at "access denied". The registry fixture proves the
   apply/restore engine, but a run of the packaged (elevated) binary is still required for F1/F2/
   F3/F4/F5/F6 and for the debloat service/task writes.
2. **F9 was not activated on this host.** Importing and activating a power plan changes the
   machine's active scheme; the code path is implemented and verified by inventory, but the
   `/import` → rename → 27-row verification → activate → restore sequence has not been run here.
3. **F3/F4 were not applied to real devices.** Mask derivation and gates are tested; the PnP
   write + health-assertion path is not.
4. **Hybrid calibration relies on CPUID leaf 0x1A** (Intel hybrid). Non-Intel hybrids without a
   usable efficiency signal will correctly fail closed with `E-HYBRID-AMBIGUOUS` rather than
   guess — but that means they cannot use F3 yet.
5. **`E-CLASS-AMBIGUOUS` is an extension** to the closed reason-code set. It is used only for
   desktop/laptop classification failure in F9 and is documented here.
6. **F5 congestion provider** is probed at build time; a machine that reports `bbr2` support is
   set to `bbr2`, otherwise left unchanged and annotated `E-BBR2-UNSUPPORTED`.
7. **Debloat**: WSearch, SharedAccess, upnphost, RemoteRegistry, TrkWks, WMPNetworkSvc and icssvc
   remain excluded; the exclusion reasons are in `DebloatOptimization`.
8. **Bloatware removal / app uninstall** is out of scope and not present.

---

## 5. Requirement confirmations

| Requirement | Status |
|---|---|
| **E-1** — `EfficiencyClass == 0` is never assumed to mean P-core | Enforced. Classification requires a CPUID/EfficiencyClass partition match; no ordering assumption. |
| **E-2/E-3** — fail closed with `E-HYBRID-AMBIGUOUS` when classification cannot be established | Enforced in `CpuTopologyService.DeriveAffinity`; no fallback mask. |
| **E-GRP-GT64** — enforced before any affinity write | Enforced; `Groups != 1 \|\| LogicalProcessors > 64` returns the code before a mask is produced. |
| **No hardcoded CPU fallback** | Enforced. No literal processor index appears in `CpuTopologyService` or `V3DeviceAffinity`. |
| **No group-0-only mask for another group** | Impossible by construction: derivation only runs on a single group of ≤ 64 LPs. |
| **Journal-only state (C3/C4)** | Enforced through `V3State.Evaluate`; values that merely match without a transaction report Unapplied. |
| **D-1..D-4 rollback defects** | D-1 fixed (prior scheme captured/restored); D-2 fixed (typed prior + exact restore); D-3 avoided (no WMI Autologger creation); D-4 fixed (journal touches only performed targets). |
| **UI integration** | Not started. Backend only, per instruction. |
