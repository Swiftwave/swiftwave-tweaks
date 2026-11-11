# Hone Nine-Optimization Set — Coding-Agent Specification

**Version** 1.0 · **Date** 2026-11-10 · **Supersedes** the feature sections of `Swiftwave-Handoff.md` where they disagree

---

## 0. How to use this document

### 0.1 Normative language

`MUST` / `MUST NOT` = hard requirement, an agent that violates it has produced a defect.
`SHOULD` = implement unless the reason is recorded in the journal.
`MAY` = optional, never on by default.

### 0.2 Precedence of artifacts

When this document and the evidence disagree, the evidence wins and the discrepancy is a bug to be filed:

1. `runtime-lab\evidence\<tweakId>.diff.md` — raw before/after diff, authoritative
2. `runtime-lab\evidence\<tweakId>.card.md` — human summary; **may under-count** (see 0.4)
3. `Hone-PowerPlan-V5\Hone-Ultimate-Power-Plan-V5-Comparison.csv` — authoritative for F9 scheme values
4. This document

### 0.3 The one rule that produces most of the safety

**Never infer applied state from observed values.** Hone's own product gets this wrong: its UI showed two features OFF
while their registry effects were present. State is derived from *our* journal, and only from the journal. A value that
matches ours but is not in our journal is a **foreign edit**, not a success (see §4.4, C3).

### 0.4 Corrections to earlier documents (must not be implemented as written)

| ID | Where | Wrong | Correct |
|---|---|---|---|
| E-1 | `Swiftwave-Handoff.md` §F3 step 2 | "keep `EfficiencyClass == 0` (P-cores)" | In `PROCESSOR_RELATIONSHIP` / `SYSTEM_CPU_SET_INFORMATION` the **higher** `EfficiencyClass` is the more performant core class. The literal instruction is inverted. Worse, the ordering is not stable across firmware/builds, so §3.3 forbids hard-coding it at all: the mapping MUST be calibrated and MUST fail closed. |
| E-2 | `Final-Report.md` §3 F9 | "12 setting rows in the power plan" | The plan stores **27**. The 12 were visible non-default rows from a diff capture; 15 are hidden. |
| E-3 | `Swiftwave-Handoff.md` §F9 | "generate the scheme with `duplicatescheme` + `setacvalueindex`" | Generate it by `powercfg /import` of the verified `.pow`, then assert all 27 values. `duplicatescheme` cannot express hidden settings that have no alias, and silently inherits the donor plan's values. |
| E-4 | `Hone-Runtime-Evidence.md` | `AssignmentSetOverride` "REG_BINARY (8)" | Correct *shape*, incomplete *model*: 8 bytes = one `KAFFINITY` mask for **processor group 0 only**. §3.3 requires a hard gate at >64 logical processors. |

---

## 1. System model

### 1.1 Five layers, strictly ordered

```
L1 DISCOVER  →  HardwareProfile   (read-only, no writes, no elevation)
L2 DERIVE    →  Plan             (pure function: Profile + UserSelection → Operations + Skips)
L3 APPLY     →  Journal          (only layer that writes; one transaction per feature)
L4 VERIFY    →  VerifyReport     (written / accepted / effective, §4.6)
L5 RESTORE   →  Journal replay   (reverse seq, with §4.7 exceptions)
```

**L2 MUST be pure.** Derivation takes a `HardwareProfile` and never touches the machine. This is what makes the
device-specific maths reviewable and testable without a mixed-CPU lab.

### 1.2 The three state stores

| Store | Lifetime | Contents | Loss consequence |
|---|---|---|---|
| `journal.jsonl` | persistent | append-only transactions, prior values, results | **unrecoverable** — the machine can be changed but not reverted |
| `ownership.json` | persistent | target → owning transaction (derived from journal, rebuildable) | rebuild by replay, no data loss |
| `HardwareProfile` | per-session | discovery snapshot | re-discover; but a changed profile invalidates a pending reboot's V3 claims |

The journal is the only irreplaceable object. §4.2 specifies durability requirements.

### 1.3 Feature identifiers (fixed, used in journal and UI)

| ID | Hone tweak | Hone UI name | Layer-1 dependency |
|---|---|---|---|
| F1 | `5Q8oOtIH` | Optimize FPS & Input Lag | CPU topology, timer capability, services |
| F2 | `3WwEZo0M` | Optimize Storage & Memory | service→svchost map, RAM |
| F3 | `RqbxUufM` | Optimize Device Affinities | CPU topology, hybrid classes, PCI devices |
| F4 | `oPu4x2Ef` | Optimize Message Signal Interrupts | PCI devices, driver MSI capability |
| F5 | `XLSEdxVW` | Optimize Network Performance | NIC identity, driver feature probe |
| F6 | `istk0001` | Disable Dynamic Interrupt Steering | kernel build number only |
| F7 | `PE9bgwi0` | Disable Windows Services | installed services |
| F8 | `9C0X9LiC` | Reduce Windows Background Activity | installed tasks, policies |
| F9 | `k6ad89bg` | Enable Maximum Performance Power Mode | power capability, storage/NVMe, PCI ASPM |

---

## 2. Layer 1 — Hardware discovery contract

### 2.1 Profile schema (the only data L2 may consume)

```csharp
sealed record HardwareProfile(
    OsInfo Os, CpuInfo Cpu, IReadOnlyList<ProcessorGroup> Groups,
    IReadOnlyList<DeviceInfo> Pci, GpuInfo? Gpu, NicInfo? PrimaryNic,
    IReadOnlyList<StorageInfo> Storage, ServiceIndex Services, PowerInfo Power,
    EnvironmentFlags Flags, DiscoveryDiagnostics Diag);
```

Every field carries a `Confidence` enum: `Observed` (read from the machine), `Derived` (computed from observed),
`Unknown`. **`Unknown` is a legal value and must propagate** — a derivation that needs an `Unknown` input produces a
skip, never a default. No field may be filled with a "typical" value.

### 2.2 Acquisition table

| Field | Source (primary → fallback) | Notes |
|---|---|---|
| `Os.Build`, `Os.Ubr`, `Os.DisplayVersion`, `Os.Edition`, `Os.ProductType` | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` → `Get-CimInstance Win32_OperatingSystem` | `ProductType` distinguishes client (1) / server (2+) — several tweaks are client-only |
| `Os.IsServer`, `Os.IsWindows11` | derived (`ProductType`, `Build >= 22000`) | |
| `Os.LastBootTime` | `Win32_OperatingSystem.LastBootUpTime` | used as the reboot epoch, §4.6 |
| `Cpu.Vendor`, `Cpu.Name`, `Cpu.Family/Model/Stepping`, `Cpu.Architecture` | `CPUID` (`__cpuidex(0)`, `(1)`, `(7,0)`, `(0x1A,0)`) → `Win32_Processor` | CPUID is authoritative; WMI declines on some VMs |
| `Cpu.PhysicalCores`, `Cpu.LogicalProcessors` | `GetLogicalProcessorInformationEx(RelationProcessorCore)` core count; `Environment.ProcessorCount` for logical | `Win32_Processor.NumberOfCores` is per-package and lies on multi-package |
| `Cpu.SmtEnabled`, `Cpu.SmtSiblings` | derived: siblings per `RelationProcessorCore` record | `SmtEnabled = any core has >1 logical` |
| `Cpu.IsHybrid`, `Cpu.PerformanceClass`, `Cpu.EfficiencyClass` | CPUID leaf `0x1A` (Hybrid Information: core type per logical processor) **and** `GetSystemCpuSetInformation` (`EfficiencyClass`) | **Both must agree.** Disagreement ⇒ `Cpu.IsHybrid = Unknown` (§3.3 gate G2) |
| `Groups[]` | `GetLogicalProcessorInformationEx(RelationGroup)` | group count, CPUs per group, active mask |
| `Pci[]` | `Get-PnpDevice` / `Win32_PnPEntity` joined to `HKLM\SYSTEM\CurrentControlSet\Enum\PCI\…` | instance id, vendor, device, subsys, class, service, `ConfigManagerErrorCode`, driver provider/version, presence of `Interrupt Management` subtrees |
| `Gpu` | `Win32_VideoController` where `PNPDeviceID` starts `PCI\` | discrete vs integrated: `AdapterCompatibility`, segment order, `HKLM\…\Control\Class\{4d36e968…}` |
| `PrimaryNic` | `Get-NetAdapter -Physical` where `Status = Up` and not `Virtual`; tie-break by lowest `ifIndex` with a default route | never pick by name; a VPN/Hyper-V vSwitch is `Virtual` |
| `Storage[]` | `Get-PhysicalDisk` (`BusType`, `MediaType`) → `Win32_DiskDrive` (`PNPDeviceID`, `InterfaceType`) | `BusType = NVMe` is the only thing that makes F9's NVMe timeouts meaningful |
| `Services` | `Get-Service` + `Win32_Service` (`StartMode`, `PathName`, `ProcessId`) | `PathName` decides whether a service is svchost-hosted (F2) |
| `Power.SleepStates` | `powercfg /a` | F9 gate: `PlatformAoAcOverride` requires S3 to exist |
| `Power.ActiveScheme`, `Power.Schemes` | `powercfg /getactivescheme`, `/list` | |
| `Power.HasBattery` | `Win32_Battery` present | laptop vs desktop changes F9's risk class, not its values |
| `Flags.IsVm`, `Flags.IsSandbox`, `Flags.HasSecureBoot`, `Flags.IsDomainJoined` | `Win32_ComputerSystem.Model`/`Manufacturer`, `Confirm-SecureBootUEFI`, `Win32_ComputerSystem.PartOfDomain` | gates many features (§3.9) |

### 2.3 Discovery invariants

* **D-INV-1** Discovery runs unelevated. If a field needs elevation it is `Unknown`, and the feature that needs it
  degrades. Discovery must never trigger a UAC prompt.
* **D-INV-2** Discovery is cached per session with a `profileHash`. Apply refuses to run if the profile hash changed
  since the Plan was derived (someone hot-plugged a GPU — masks are now wrong).
* **D-INV-3** Every `Unknown` is logged with the reason the source failed (exception type, exit code), never as
  "not present".
* **D-INV-4** Discovery never writes. In particular it must not call `powercfg /setacvalueindex`, must not start or
  stop services, and must not load registry hives.

---

## 3. Layer 2 — Derivation rules

Each rule has the same four parts: **Inputs → Gates → Algorithm → Fallback ladder**. The ladder is mandatory and must
produce one of `L0 Full`, `L1 Reduced`, `L2 Disabled(reasonCode)`.

### 3.1 Reason codes (closed set — do not invent new strings at runtime)

```
E-NOT-ADMIN            E-OS-UNSUPPORTED       E-OS-SERVER
E-VM                   E-CPU-UNKNOWN          E-CPU-SMT-UNKNOWN
E-HYBRID-AMBIGUOUS     E-GRP-GT64             E-TOPO-DEGENERATE
E-NO-DEVICE            E-DRV-NO-MSI           E-DRV-NO-AFFINITY
E-DEV-SYSTEM-CLASS     E-DEV-BOOT-DISPLAY     E-DEV-CONFIG-ERROR
E-NIC-NOT-FOUND        E-BBR2-UNSUPPORTED     E-NETSH-FAILED
E-SVC-ABSENT           E-TASK-ABSENT          E-POWER-S3-UNSUPPORTED
E-FOREIGN-EDIT         E-PROFILE-CHANGED      E-JOURNAL-CORRUPT
E-PARTIAL-FAILURE      E-REBOOT-PENDING
```

### 3.2 Rule D-TOPO — CPU topology (feeds F1, F3, and the F1 timer task)

**Gates**: `Cpu.PhysicalCores >= 2`; else `L2 E-TOPO-DEGENERATE`.

**Algorithm**

```
cores   = GetLogicalProcessorInformationEx(RelationProcessorCore)
each core → { efficiencyClass, smtSiblings[], groupIndex, l3Key }
primary = cores where efficiencyClass == max(efficiencyClass)     // see gate G2
secondary = cores where efficiencyClass <  max(efficiencyClass)    // only when hybrid
groups  = GetLogicalProcessorInformationEx(RelationGroup)
```

**Gate G2 (hybrid calibration)** — the mapping from `EfficiencyClass` to "performance core" MUST be validated, not
assumed, because it has been observed inverted across firmware:

1. If `Cpu.IsHybrid == Unknown` → no per-class logic anywhere; `L1 Reduced` for F3 (see 3.3).
2. If CPUID `0x1A` and `GetSystemCpuSetInformation` disagree on the class partition → `L2 E-HYBRID-AMBIGUOUS`.
3. If a partition has a single class → treat as non-hybrid (valid: some U-series parts report one class).

**Ladder**: `L0` full core map · `L1` topology without class information (F3 can still pin to a single
non-CPU-0 set) · `L2 E-CPU-UNKNOWN`.

### 3.3 Rule D-AFF — interrupt-affinity mask (F3)

**Inputs**: `Cpu`, `Groups`, `Pci`, target device instance id.
**Gates** (all must pass, in this order; each has a distinct reason code):

| G | Check | Reject |
|---|---|---|
| G1 | `Cpu.LogicalProcessors >= 4` | `L2 E-TOPO-DEGENERATE` (masking 2 CPUs on a 2-thread box is a net loss) |
| G2 | hybrid class mapping calibrated (§3.2) | see 3.2 |
| G3 | `Groups.Count == 1` **and** `LogicalProcessors <= 64` | `L2 E-GRP-GT64` — the 8-byte `AssignmentSetOverride` can only express group 0; writing it on a multi-group machine silently pins to the wrong CPUs |
| G4 | device `Class ∉ {System, Processor, …}` and vendor/device not in the hard-deny list (`8086:51ED`, `8086:51F1`, `8086:51CA` Intel Serial IO) | `L2 E-DEV-SYSTEM-CLASS` |
| G5 | device is not the boot/display adapter currently driving the primary monitor | warn `E-DEV-BOOT-DISPLAY`, require explicit user confirmation |
| G6 | `ConfigManagerErrorCode == 0` | `L2 E-DEV-CONFIG-ERROR` |
| G7 | `Device Parameters\Interrupt Management\Affinity Policy` key **already exists** | `L1` (create it only with an explicit user opt-in; Hone creates it, we prefer not to guess that a driver honours a key it never registered) |

**Algorithm**

```
1. candidates = primary cores (or all cores when non-hybrid) excluding the core hosting CPU 0
2. l3Key      = the L3 group that contains the *majority* of the target device's current interrupts
                (fallback: the L3 group of the core that currently services the device)
3. chosen     = up to 2 cores from candidates ∩ l3Key, preferring distinct physical cores
4. if chosen.Count < 2 → chosen = 1 core (still valid; log L1)
5. mask = OR(1UL << core.FirstLogicalProcessor) over chosen
6. emit: DevicePolicy      = REG_DWORD 4        (IrqPolicySpecifiedProcessors)
         DevicePriority    = REG_DWORD 3        (only on the feature's primary target)
         AssignmentSetOverride = REG_BINARY LE(mask, 8 bytes)
```

**Non-goals inside the algorithm**: never mask to a single SMT sibling pair of the *same* physical core for a
throughput device (halves available interrupt throughput); never include CPU 0 (kernel/DPC housekeeping); never
pin more than 2 cores (latency, not throughput, is the goal, and over-pinning serialises interrupts).

**Fallback ladder**: `L0` 2 cores · `L1` 1 core, or 2 cores in the nearest available L3 group · `L2` + reason.
A device rejected by any gate is **skipped individually**; the feature continues with the remaining devices and
reports `PartiallyApplied` with the per-device reason.

### 3.4 Rule D-MSI — MSI eligibility (F4)

**Gates**

| G | Check | Reject |
|---|---|---|
| G1 | `Class ∈ {Display, Net, SCSIAdapter, HDC, NVMeController}` | `skip` (not an error) |
| G2 | `\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties` exists | `E-DRV-NO-MSI` — absence means the driver never declared MSI; writing `MSISupported=1` is unsupported and can stop the device |
| G3 | `ConfigManagerErrorCode == 0` before write | `L2 E-DEV-CONFIG-ERROR` |
| G4 | device is not `Class=System` | `E-DEV-SYSTEM-CLASS` |
| G5 | `MessageNumberLimit` present (GPU case) | skip the delete, keep `MSISupported` |

**Algorithm**: per eligible device write `MSISupported = 1`; **record** `MessageNumberLimit` (type + data) before any
delete; delete it only on the single discrete GPU selected as primary.

**Post-write hard assertion (mandatory, not optional)**: after the write, re-read `ConfigManagerErrorCode` and
`Get-PnpDevice -Status`. A non-zero code or an `Error` state triggers immediate per-device restore and
`E-PARTIAL-FAILURE`. This is the difference between "wrote a value" and "did not break the GPU".

**Fallback ladder**: `L0` GPU + NVMe + NIC · `L1` GPU only · `L2` + reason.

### 3.5 Rule D-NIC — network target selection (F5)

* Mute any adapter that is `Virtual`, part of a Hyper-V vSwitch, or has no default route.
* If multiple physical adapters are `Up`, the plan writes per-adapter values to **all** of them but applies the
  `UpperBind` rewrite to **none** (see §7).
* `bbr2` is a **probe**, not an assumption: run the read-only query first; if the provider is absent, emit
  `E-BBR2-UNSUPPORTED` and fall back to the current provider instead of forcing `newreno`→`bbr2`.
* Refuse the whole feature on `Os.IsServer` and on `Flags.IsVm` (`L2 E-OS-SERVER` / `E-VM`).

### 3.6 Rule D-SVC — service sets (F2, F7)

* Enumerate only services that **exist**, and for F2 only those whose `PathName` is `svchost.exe -k …`.
* F2's rule is `SvcHostSplitDisable = 1` on the *host it is consolidating*; F5 reverses this on `BITS`
  (`= 0`). **This is a real cross-feature conflict** — see §4.5 C2-1. The plan MUST detect that both features would
  write the same target and force the user to choose, or apply the documented precedence (F2 wins; F5's BITS write is
  dropped) and record it.
* Never include: `HoneSvc`-like vendor services, third-party security agents, `WinDefend`, `wscsvc`, `mpssvc`.
* F7 uses the captured seven only. Absent service → `skip` with `E-SVC-ABSENT`, never create.

### 3.7 Rule D-TASK — task set (F8)

* Enumerate `Get-ScheduledTask` and match on **exact** `TaskPath + TaskName`; a missing task is skipped, not created.
* Never touch tasks under a third-party vendor folder, or any task whose action path is outside `%SystemRoot%`.
* Hone's `\DisablePowerSaving` task (F9) is **not** recreated — its action was never captured (§7, N-05).

### 3.8 Rule D-PWR — power plan (F9)

**Gates**: `Power.SleepStates` contains `Standby (S3)` before `PlatformAoAcOverride` may be written;
`Storage.Any(BusType == NVMe)` before NVMe timeouts are claimed as meaningful; `Os.IsServer == false`.

**Algorithm**

```
1. powercfg /import "<verified .pow>" 44444444-4444-4444-4444-444444444452
2. assert all 27 stored values (subgroup GUID + setting GUID + AC + DC) against the CSV
3. powercfg /active  → record the previous scheme in the journal (this is F9's restore path)
4. activation is a separate, explicitly confirmed operation; never silent
```

**Do not** build the scheme with `duplicatescheme` + `setacvalueindex` (E-3). Hidden settings without aliases cannot be
addressed that way and the donor plan's values leak in.

**Ladder**: `L0` import + activate · `L1` import only (scheme present, not active) · `L2` + reason.

### 3.9 Global environment gates

| Flag | Effect |
|---|---|
| `Flags.IsVm` | F3, F4, F5, F9 → `L2 E-VM` (interrupt/affinity and NIC tuning are meaningless or destabilising on a hypervisor) |
| `Os.IsServer` | F1 timer, F2, F5, F9 → `L2 E-OS-SERVER` |
| `Flags.IsSandbox` | everything `L2` |
| `Os.Build < 19041` | F6, F9, F5-bbr2 → `L2 E-OS-UNSUPPORTED` |
| pending reboot | all features `L2 E-REBOOT-PENDING` except read-only verify |

---

## 4. Layers 3–5 — Apply, verify, restore

### 4.1 Transaction state machine

```
Planned ──► Applying ──► Verifying ──► Committed
                 │            │
                 │            ├──► PartiallyApplied ──► (auto-rollback) ──► RolledBack
                 │            └──► RebootPending   (values written, effect not claimable)
                 └──────────────────────────────► Aborted (nothing written)
Committed ──► Reverting ──► Restored | RollbackFailed
```

Rules:

* `Applying` is entered only after the **intent** record for operation `seq=1` is durably written.
* A crash in `Applying` is recovered by reading the journal: every operation with `intent` but no `result` is treated
  as **unknown** — re-read its target, compare with `newData`; if equal, treat as written; else treat as not written.
* `RollbackFailed` is a terminal, loud state. The UI must show the exact operations that could not be reverted, and the
  journal must remain exportable so a human can finish manually.

### 4.2 Journal durability

* Append-only `jsonl`, one line per event, `O_APPEND`, flushed (`FlushFileBuffers`) before the corresponding write.
* Stored under `%ProgramData%\<product>\journal\<txid>.jsonl`, with a sidecar hash chain (`prevHash`) so tampering or
  truncation is detectable (`E-JOURNAL-CORRUPT`).
* Retain indefinitely; never prune a transaction that is not `Restored`.

### 4.3 Exact pre-change state (per operation kind)

| Kind | Must record before the write | Restore action |
|---|---|---|
| Registry value | existence, type, raw data (`DoNotExpandEnvironmentNames`), owning key existence (did *we* create the key?) | if existed → set value; if absent → delete value; if we created the key and it is now empty → delete key, else leave |
| Service | `StartMode` **and** `State` | restore `StartMode`; restore `State` **only** if we changed it and the service was `Stopped`/`Running` before — never stop a service the user started later |
| Scheduled task | enabled/disabled state, and whether the task existed | re-enable; never unregister a task we did not create |
| Power scheme | previous `ActivePowerScheme`, imported `SchemeGuid`, and our own exported `.pow` copy | `/setactive <previous>`; `/delete <ours>` **only if** it is ours |
| Command (`netsh`, `bcdedit`) | stdout/stderr, exit code, and the **query form** of the setting before the change (e.g. `netsh int tcp show global`) | replay the query's inverse; if no inverse is expressible, mark the operation `NotRestorable` at plan time and tell the user **before** applying |
| Device property | the `Affinity Policy` / `MessageSignaledInterruptProperties` values, and whether those keys pre-existed | restore values; do not delete pre-existing keys |

`NotRestorable` is a first-class plan-time verdict. The UI must show it before apply, not after.

### 4.4 Ownership tracking

```json
OwnedTargets: {
  "HKLM\\SYSTEM\\CurrentControlSet\\Services\\BITS\\SvcHostSplitDisable":
      { "txid": "…", "featureId": "F2", "seq": 12, "ourData": "0x00000001" },
  "HKLM\\SYSTEM\\CurrentControlSet\\Enum\\PCI\\VEN_10DE&DEV_22BE…\\Device Parameters\\Interrupt Management\\Affinity Policy\\AssignmentSetOverride":
      { "txid": "…", "featureId": "F3", "seq": 3, "ourData": "hex:0000fefe00000000" }
}
```

Ownership is a **rebuildable index**: delete it and it regenerates from the journal. It exists to answer three
questions in O(1): *is this target ours? which feature? is the current value still what we wrote?*

A feature MUST NOT write a target that is owned by another feature's live transaction. That is a conflict (§4.5).

### 4.5 Conflict classes

| Class | Situation | Required behaviour |
|---|---|---|
| **C1** | Same feature, already applied | no-op, return `AlreadyApplied`; never stack a second undo |
| **C2** | Two features legitimately target the same value | guard table; explicit precedence; record the loser as `SkippedConflict` |
| **C3** | Value differs from what we wrote and we still own the target | `E-FOREIGN-EDIT`: do **not** overwrite silently. Offer *re-apply* / *accept external* / *rollback*. Log the observed value. |
| **C4** | Value matches ours but no journal entry exists | treat as `Unapplied` (someone else, or a previous install). Never adopt it as our state. |
| **C5** | Profile changed since planning | `E-PROFILE-CHANGED`, re-derive; masks/schemes must not be applied to a machine that no longer matches |

Known C2 pairs (implement as code):

* **C2-1** `Services\BITS\SvcHostSplitDisable` — F2 writes `1`, F5 writes `0`. Precedence F2; F5's op is dropped.
* **C2-2** `Services\Psched\TimerResolution` (F5) vs F1's timer work — independent values, but the *effect* is shared.
  Verify them together and never report a latency win for both separately.
* **C2-3** `Control\Session Manager\kernel\InterruptSteeringFlags` (F6) vs F3/F4 — F6 is global; if F6 is on, F3/F4
  verification must not claim causality.
* **C2-4** F9's previous-scheme restore vs a user manually switching plans afterwards — restore must compare the
  current active scheme with the one we recorded; if the user changed it, ask before overwriting.

### 4.6 Verify semantics — three levels, never conflated

| Level | Meaning | Test | Claimable immediately |
|---|---|---|---|
| **V1 Written** | the value/state is what we set | read-back equals `newData` (byte-wise for `REG_BINARY`) | yes |
| **V2 Accepted** | the OS/driver acknowledged it | service `StartMode`/`State`; `Get-PnpDevice -Status` not `Error`; `ConfigManagerErrorCode == 0`; `netsh … show global` reflects it; `powercfg /getactivescheme` | yes |
| **V3 Effective** | the behaviour changed | requires `LastBootUpTime > txid.completedUtc` (a **reboot epoch**); then device re-enumeration, interrupt routing, or the improvement measurement | only after the epoch |

Rules:

* A feature whose effect needs a reboot can only ever reach V2 until the epoch advances. The UI must say
  *"written, requires restart"* — never *"applied"*.
* V3 for F3/F4 additionally requires the device to still be `OK` after the reboot; a device that fails is
  auto-restored and reported.
* Verify is **read-only** and safe to run at any time, including on a machine another feature changed.

### 4.7 Restore

* Reverse `seq` order, with these exceptions:
  1. **Reboot-bound ops restore last** (device affinity/MSI, PCI/NVMe registry, `PlatformAoAcOverride`) — they need a
     second reboot anyway; grouping them means one reboot, not seven.
  2. **Services restore after tasks** (a task may restart a service).
  3. **F9 scheme restore (`/setactive previous`) runs before the scheme's `/delete`**, and both before any reboot.
* Restore MUST be resumable: a crash during restore leaves the journal with completed restores marked, and the next
  run continues, not restarts.
* Restore MUST verify each reversal at V1 and report `RollbackFailed` per operation, not as a whole-feature verdict.

### 4.8 Reboot classes

| Class | Meaning | Examples |
|---|---|---|
| `None` | effective immediately | F5 `netsh` globals, F7/F8 service/task state, display power timeouts |
| `Restart-Required` | driver/device re-reads a value at start | F3, F4, F6, F9 NVMe/WDF/ASPM items |
| `FullReboot` | boot-phase consumer | F1 `bcdedit` items |
| `SignOut` | per-user hive | F1's `HKCU` Desktop/Mouse values |

Exactly one reboot prompt per transaction, listing every class present. Never reboot automatically.

---

## 5. The nine features — implementation contracts

Each row is what an agent needs; the algorithm lives in the referenced rule. Target paths are complete.

### F1 — Timer & Latency Profile (`5Q8oOtIH`)

| Item | Specification |
|---|---|
| Targets | `bcdedit /set disabledynamictick` / `useplatformtick` / `tscsyncpolicy` (3 ops, 2 are *deletes* of a value that may not exist → must be gated on `bcdedit /enum {current}` showing the value first); `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel` (`GlobalTimerResolutionRequests`, `DistributeTimers` — **only names confirmed in the diff**); `Control\WaitToKillServiceTimeout`, `WaitToKillAppTimeout`; `Control\Desktop` (`HungAppTimeout`, `WaitToKillAppTimeout` — drop `AutoEndTasks`); `Control\GraphicsDrivers` `SwapEffectUpgradeEnable`/`SwapEffectUpgradeCache`; `Explorer\Serialize` pair; `Control\Session Manager\kernel\Dsh`/`input` preload; 5 services; task `\LaunchSetTimerResolution` |
| Derivation deps | D-TOPO (timer resolution target = 1 ms only when `Cpu.LogicalProcessors >= 8` and no battery) |
| Gates | `Os.IsServer` false; `bcdedit` output parseable; `SetTimerResolution.exe` **not** required — replace with our own `NtSetTimerResolution` call (Hone's task is `NotRestorable`-prone) |
| Reboot | `FullReboot` for the `bcdedit` subset, `SignOut` for the `HKCU` subset, `None` for timeouts |
| Verify | V2 via `bcdedit /enum {current}` + registry read-back; V3 only for timer resolution, measured with `NtQueryTimerResolution` |
| Restore | `bcdedit /deletevalue` for each set value (**verify the value exists first**, or the delete fails and aborts the rollback) |
| Not in scope | the 10 kernel tunables, `NoLazyMode=0`, `RawMouse*` ×4, `VRROptimizeEnable=0`, `AllowAutoGameMode`, `AutoEndTasks` |

### F2 — Service-Host Consolidation (`3WwEZo0M`)

| Item | Specification |
|---|---|
| Targets | `Control\SvcHostSplitThresholdInKB = 0xFFFFFFFF`; per-service `SvcHostSplitDisable = 1`; `Control\FileSystem` 6 values (`NtfsDisable8dot3NameCreation`, `NtfsDisableLastAccessUpdate`, `NtfsMemoryUsage`, `NtfsScrub`…) — **exact six from the diff**; `Control\Session Manager\Memory Management\ReserveManager` 2 values |
| Derivation deps | D-SVC (only svchost-hosted, existing services) |
| Gates | `Cpu.LogicalProcessors >= 4` and physical RAM ≥ 8 GiB, else `L1` (threshold only, no per-service writes) |
| Conflicts | C2-1 with F5/BITS |
| Reboot | `Restart-Required` |
| Verify | V1 for values; V2 = `Get-Service` still healthy for every touched service; **must** assert no service entered `Stopped` |
| Restore | delete the created values; restore `SvcHostSplitThresholdInKB` prior (usually absent) |
| Not in scope | `NtfsAllowExtendedCharacterIn8dot3Name`; MFT zone 25 % |

### F3 — Device Interrupt Affinity (`RqbxUufM`)

| Item | Specification |
|---|---|
| Targets | per device: `HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<INSTANCE>\Device Parameters\Interrupt Management\Affinity Policy\{DevicePolicy, DevicePriority, AssignmentSetOverride}` |
| Derivation | **D-AFF (§3.3)** — the whole rule, including gates G1–G7 and the 64-LP gate |
| Order | GPU → NVMe → primary NIC |
| Reboot | `Restart-Required` |
| Verify | V1 all three values; V2 device `Status = OK` and `ConfigManagerErrorCode = 0`; V3 only after reboot epoch |
| Restore | delete the three values on recorded devices only; do not delete the `Affinity Policy` key unless we created it |
| Hard prohibitions | never re-apply periodically (Hone does this via `HoneSvc`); never copy Hone's literal masks; never pin to CPU 0; never pin a `Class=System` device; never write on a >64-LP machine |

### F4 — Message-Signalled Interrupts (`oPu4x2Ef`)

| Item | Specification |
|---|---|
| Targets | per device: `…\MessageSignaledInterruptProperties\MSISupported = 1`; GPU only: delete `MessageNumberLimit` after recording it |
| Derivation | **D-MSI (§3.4)** |
| Reboot | `Restart-Required` |
| Verify | V1 + the mandatory post-write device-health assertion; a failing device is restored immediately and named |
| Restore | restore `MSISupported` (delete if absent before); restore `MessageNumberLimit` type+data |
| Hard prohibitions | never write to a device lacking the `MessageSignaledInterruptProperties` key; never blanket-write every PCI function; never delete `MessageNumberLimit` without recording it (Hone did) |

### F5 — Network Stack Profile (`XLSEdxVW`)

| Item | Specification |
|---|---|
| Targets | `netsh` globals (rss, taskoffload, netdma, dca, uro/uso, loopbacklargemtu, hoplimit, isatap disable, congestion provider **probe**); `Services\Tcpip\Parameters` 9 values; `Services\Tcpip6\Parameters` 5 + `DisabledComponents`; `Services\Dnscache\Parameters` 4; `Services\Psched\TimerResolution`; `Services\lmhosts\Start`; `Services\iphlpsvc\config\IsatapState`; `Services\Tcpip\qos` `Do not use NLA`; `Services\LanmanServer\Parameters` 3; NIC class key power-save block (`*EEE`, `EnableGreenEthernet`, `MIMOPowerSaveMode`, `PnPCapabilities`, …) |
| Derivation | D-NIC (§3.5); NIC keys resolved by adapter GUID, never by hard-coded `0001` |
| Gates | not server, not VM; `bbr2` present else `E-BBR2-UNSUPPORTED` |
| Reboot | `None` for `netsh`, `Restart-Required` for the driver-level keys |
| Verify | V2 by re-running the `netsh … show global` for each item (Hone has no verification at all) |
| Restore | per-value prior; `netsh` items restored by their inverse command; anything with no inverse is `NotRestorable` and must be disclosed pre-apply |
| Not in scope | `initialRto=2000` (doubles first-retransmit latency — counterproductive); `UpperBind` rewrite (destroys IPv6/LLTD bindings on a live adapter); `NetbiosOptions=2` and `lmhosts=Disabled` unless explicitly opted in; the ~452-entry QoS DSCP list; BITS `SvcHostSplitDisable=0` (C2-1) |

### F6 — Kernel Interrupt Steering (`istk0001`)

| Item | Specification |
|---|---|
| Targets | `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\InterruptSteeringFlags = 0` |
| Gates | build ≥ 19041; not VM |
| Reboot | `Restart-Required` |
| Verify | V1 immediately; V2/V3 after reboot — and **do not** attribute F3/F4 gains to this or vice versa (C2-3) |
| Restore | delete the value (Hone's rollback was verified to do exactly this) |

### F7 — Background Service Trim (`PE9bgwi0`)

| Item | Specification |
|---|---|
| Targets | `Services\{WalletService, SharedAccess, SEMgrSvc, Ndu, upnphost, PhoneSvc, AxInstSV}\Start` |
| Derivation | D-SVC |
| Gates | each service must exist; `Start` prior must be recorded even though we only ever set `4` |
| Reboot | `None` (takes effect on next start); some require `Restart-Required` if running |
| Verify | V2 `Get-Service` shows `Stopped` and `StartType = Disabled` |
| Restore | restore prior `Start`; **never** start the service unless it was running before |
| Policy | `SharedAccess` and `upnphost` are gated behind an explicit confirmation (ICS / UPnP consumers) |

### F8 — Background Activity Reduction (`9C0X9LiC`)

| Item | Specification |
|---|---|
| Targets | 34 `TaskCache` tasks (disable via `Get-ScheduledTask | Disable-ScheduledTask`, path-matched); WER 10 values; `Policies\Microsoft\Windows\{AppCompat, System, deviceinstall, wcn}` values; `Services\{PcaSvc, wercplsupport}\Start` |
| Derivation | D-TASK (§3.7) |
| Gates | task must exist; skip any task whose action path is outside `%SystemRoot%` |
| Reboot | `None` |
| Verify | V2 per task `State = Disabled`; per service `Start = 4` |
| Restore | 34 re-enables + policy values removed (Hone's own rollback was verified clean for this feature) |
| Not in scope | `Autochk\Proxy`; `AllowBlockingAppsAtShutdown=0`; `SeLpacEnableWatsonThrottling`; `SbEnable=0`; `WSearch` handling beyond a separate toggle |

### F9 — Maximum Performance Power Mode (`k6ad89bg`)

| Item | Specification |
|---|---|
| In-scope | the power scheme only: import the verified `.pow`, activate, and record the previous scheme |
| Scheme | GUID `44444444-4444-4444-4444-444444444452`, 27 settings, 6 subgroup nodes — see `Hone-PowerPlan-V5\Hone-Ultimate-Power-Plan-V5-Comparison.csv` |
| Derivation | D-PWR (§3.8) |
| Gates | S3 present before `PlatformAoAcOverride`; NVMe present before claiming NVMe timeouts |
| Reboot | `Restart-Required` for the non-scheme items, `None` for the scheme itself |
| Verify | V1 all 27 values read back from `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\<guid>`; V2 `powercfg /getactivescheme` equals ours |
| Restore | `powercfg /setactive <previous>` then `powercfg /delete <ours>` |
| Out of scope (separate features or forbidden) | `PlatformAoAcOverride`, `PerfBoostAtGuaranteed`, `StorageD3InModernStandby`, `TaggedEnergy` ×3, `stornvme IdlePowerMode`, `pci ASPMOptOut`, NIC `PnPCapabilities`, WDF per-device values, ETW channels, WMI autologgers, WMI ACLs, SleepStudy DLL rename, 5 services, 2 tasks, `fssprov EncryptProtocol` — see §7 |

---

## 6. Acceptance tests

An implementation is not done until these pass on real hardware, not only on mocks.

| ID | Profile | Steps | Pass criteria |
|---|---|---|---|
| T-1 | 8 P-core / 8 E-core hybrid, 1 GPU, 1 NVMe, 1 NIC | F3 on GPU | mask contains only P-core CPUs, excludes CPU 0, ≤ 2 bits; device `OK` after reboot |
| T-2 | Same | F3 on a `Class=System` device | refused with `E-DEV-SYSTEM-CLASS`, zero writes, journal shows no intent |
| T-3 | Non-hybrid 6c/12t | F3 | mask uses 2 distinct physical cores not on CPU 0 |
| T-4 | 128-LP dual-socket (or simulated) | F3 | `L2 E-GRP-GT64`, zero writes |
| T-5 | Any desktop | F4 on GPU | `MSISupported=1`, `MessageNumberLimit` recorded then deleted, device `OK`; force a device error → auto-restore works |
| T-6 | Two NICs, one VPN | F5 | only the physical `Up` adapter gets keys; no `UpperBind` write anywhere |
| T-7 | Host without BBRv2 | F5 | `E-BBR2-UNSUPPORTED`, existing provider untouched, no partial apply |
| T-8 | Any | F2 then F5 | `BITS\SvcHostSplitDisable` written once, by F2, with F5 recorded as `SkippedConflict` |
| T-9 | Any | apply F3 → kill the process mid-transaction → restart | recovery identifies written vs unknown correctly; restore returns the machine to pre-state |
| T-10 | Any | F9 apply | 27/27 values match the CSV; previous scheme restored on rollback; GUID absent after rollback |
| T-11 | Any | apply F9, then manually change the active scheme, then roll back | asks before overwriting the user's choice (C2-4) |
| T-12 | Any | apply any feature, externally change one of its values, run verify | reports `E-FOREIGN-EDIT` with the observed value; does not silently re-apply |
| T-13 | Any | verify immediately after apply of a `Restart-Required` feature | reports V2 only, never V3 |
| T-14 | Any | run discovery on a locked-down box without admin | completes, marks fields `Unknown`, triggers no UAC |

---

## 7. Non-goals — operations an agent must not implement

### 7.1 Forbidden (unsafe or unjustifiable)

| ID | Operation | Why |
|---|---|---|
| N-01 | `Control\WMI\Security` provider ACL entry removal | Removes WMI security descriptors; integrity/security risk with no user-visible benefit |
| N-02 | Renaming `%SystemRoot%\System32\Microsoft-windows-sleepstudy-events.dll` | Mutating System32 to suppress telemetry breaks on servicing and trips AV/repair tooling; the same effect is available by disabling the ETW channel |
| N-03 | `Control\WMI\Autologger` block **deletion** | Deletion is irreversible without a full export; disable the block instead, which is reversible |
| N-04 | `Services\BITS\SvcHostSplitDisable = 0` while F2 owns the value | Self-contradictory with F2; C2-1 |
| N-05 | Creating the `\DisablePowerSaving` scheduled task | Its action was never captured — nothing about its behaviour is known |
| N-06 | `PlatformAoAcOverride = 0` on any machine without S3 | Forces S3 on an S0ix design; can break sleep/resume. Only behind a hard `powercfg /a` gate |
| N-07 | `UpperBind` rewrite on a live adapter | Drops `Tcpip6`, `lltdio`, `MsLldp`, `rspndr` bindings; disables IPv6 on that NIC |
| N-08 | `initialRto = 2000` | Doubles the first retransmit wait versus the 1000 ms default — a regression, not a tweak |
| N-09 | ~452 hard-coded QoS DSCP-46 game entries | Unmaintainable, and only meaningful on networks that honour DSCP |
| N-10 | Hone's periodic re-apply (`HoneSvc` re-writes two tweaks on USB arrival / audio render change) | Makes applied state unknowable and fights the user and other software; our model is journal-driven and explicit |
| N-11 | Copying Hone's literal affinity masks or `0001` NIC class index | Hardware-specific constants; must be derived (§3.3, §3.5) |
| N-12 | `AutoEndTasks`, `AllowBlockingAppsAtShutdown = 0` | Data-loss class behaviour on shutdown |
| N-13 | Disabling Windows security services (`WinDefend`, `wscsvc`, `mpssvc`, `Sense`) | Out of scope by product policy, and unsafe |
| N-14 | Any `bcdedit` write without first proving the value's current state | A blind `deletevalue` fails and aborts the whole rollback (D-2 defect in the D1 analysis) |

### 7.2 Not implementable yet (evidence incomplete)

| ID | Item | What is missing |
|---|---|---|
| U-01 | 10 kernel tunables in F1's target list | Value names/values were not individually captured |
| U-02 | `MiscPolicyInfo = 2` (F2) | Undocumented; semantics unknown |
| U-03 | `Session Manager\Power` `HiberbootEnabled` blob (F9) | Written as a side effect of activation; no documented inverse |
| U-04 | `TaggedEnergy` three values | Effect on energy estimation is unmeasurable in a way we can verify |
| U-05 | `fssprov EncryptProtocol = 0` | Provider-specific; no public documentation |
| U-06 | The `netsh` `initialRto`/supplemental-template churn | Order-dependent and partly counterproductive (N-08) |
| U-07 | Hone's `DisablePowerSaving` task | Action never captured (N-05) |

### 7.3 Explicit product decisions

* Report `NotRestorable` operations **before** apply, never after.
* Never claim a measured improvement without a before/after measurement recorded in the journal.
* Never claim V3 from a pre-reboot observation.
* A skipped item is a **successful outcome**, reported with its reason code — not an error dialog.

---

## 8. Traceability

| Feature | Evidence grade | What the agent may assume | What it must re-verify on each machine |
|---|---|---|---|
| F1 | VERIFIED-RUNTIME, partial | the listed value names | `bcdedit` current state, timer support |
| F2 | VERIFIED-RUNTIME, clean rollback | all values | service→svchost mapping |
| F3 | VERIFIED-RUNTIME (effect), UNCONFIRMED rollback | the mechanism, not the masks | topology, hybrid classes, device eligibility |
| F4 | VERIFIED-RUNTIME, UNCONFIRMED rollback | the mechanism | driver MSI capability, device health |
| F5 | VERIFIED-RUNTIME, clean rollback | the values | NIC identity, `bbr2` presence |
| F6 | VERIFIED-RUNTIME, verified rollback | the single value | build number |
| F7 | VERIFIED-RUNTIME, verified rollback | the seven services | service existence |
| F8 | VERIFIED-RUNTIME, verified rollback | the 34 tasks + policies | task existence |
| F9 | VERIFIED-ARTIFACT + VERIFIED-RUNTIME, 27/27 | the exact 27 settings | power capability, NVMe presence |

Anything marked UNCONFIRMED means: the *apply* was observed, the *rollback* was not. Those features must ship with a
mandatory pre-apply system-restore point or an exported registry backup of every target key, and must be labelled as
such in the UI.
