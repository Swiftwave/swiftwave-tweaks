# Hone Ultimate Power Plan V5 — Reverse-Engineering and Reconstruction Report

| Field | Value |
|---|---|
| Subject | Hone **Ultimate Power Plan V5** (one component of Hone v2.1.0's `Enable Maximum Performance Power Mode` / tweak `k6ad89bg`) |
| Scheme GUID | `44444444-4444-4444-4444-444444444452` (reused successfully — no conflict) |
| Deliverable plan file | `Hone-Ultimate-Power-Plan-V5-Reconstructed.pow` (12,288 bytes) |
| Settings recovered | **27** AC/DC pairs across **6** stored subgroup nodes (**5** real subgroups + 1 plan-personality node) |
| Settings with verified original AC **and** DC | **27 / 27** |
| Reconstruction match | **27 / 27 Exact** against both the shipped artifact and the runtime-registered scheme |
| Confidence | **High for the stored setting set** (three independent sources agree); **Medium** for "the plan contains nothing else", which is *inferred* from the fact that no other value existed at runtime |
| Report date | 2026-11-10 |
| Evidence root | `C:\Users\alanz\Documents\Trae-CN\temp_20261110_01\runtime-lab\` |

---

## 0. Scope, method, and safety

### 0.1 What this report is about

Hone's `Enable Maximum Performance Power Mode` operation is **not** just a power plan. It is 71 granular operations,
of which only 27 are power-scheme settings (section 3). This report reconstructs **only the power plan** — the
`.pow` file and the scheme it produces — and documents everything else as out of scope.

### 0.2 Three sources of truth, and why that matters

| Source | What it is | Grade |
|---|---|---|
| **A — Shipped artifact** | `HoneV5.pow`, shipped inside the app, decoded offline into a registry-hive view | VERIFIED-ARTIFACT |
| **B — Runtime hive export** | `HKLM\SYSTEM\CurrentControlSet\Control` exported *after* Hone applied `k6ad89bg` on the live machine; contains the registered scheme in full | VERIFIED-RUNTIME-RECOVERED |
| **C — Reconstruction** | This deliverable: imported, enumerated, re-exported, re-imported | VERIFIED-RUNTIME (of the deliverable) |

Source B is the decisive one. A prior investigation had only a **diff-based capture that recorded ~12 setting rows**,
which is why the plan looked under-specified. The full post-apply hive export was already on disk and contains the
**complete** registered scheme: 33 keys, 27 AC/DC pairs, and the `FriendlyName` **"Hone Ultimate Power Plan V5"**.
All 27 values in B are byte-equal to A, and all 27 in C are equal to both.

### 0.3 Isolation and safety (read this before trusting the validation transcript)

A disposable VM, Windows Sandbox image, Hyper-V VM, VirtualBox and VMware were **all unavailable** on this host, and
enabling Windows Sandbox requires an elevated feature install plus a reboot — outside the authorised scope. Validation
therefore ran **on the host**, under these containment rules:

* the scheme was only ever **imported**, never **activated** — no power setting of the host changed;
* the active scheme was captured before *every* step and re-verified at the end (unchanged: `8c5e7fda…` Paragon Perf V4);
* every test scheme was deleted and its absence re-verified;
* no Hone optimisation was executed, no driver/firmware touched, no service or task modified;
* the irrelevant-looking control test (a `duplicatescheme` scratch plan) was created and deleted in the same job.

**The one requirement this cannot satisfy is task §5 item 6, "confirm the imported scheme can be activated".**
Activating it would apply the reconstructed plan to the user's primary machine, which §7 forbids. It is recorded as
**NOT TESTED / SAFETY OVERRIDE**, not as a pass. Everything else in §5 was performed.

### 0.4 Corrections to earlier conclusions in this investigation

| Earlier conclusion | Status now | Evidence |
|---|---|---|
| "`HoneV5.pow` is an offline registry hive, not a real `.pow`" | **Wrong** | A `.pow` export *is* a registry hive. `Balanced.pow`, `HighPerformance.pow` and `HoneV5.pow` all start with `regf`, and `powercfg /import` accepts `HoneV5.pow` (exit 0). |
| "The plan has approximately 12 settings" | **Incomplete** | The 12 came from a diff-based runtime capture. The plan stores **27**; 15 of them are hidden settings that never appear in Control Panel or in `powercfg /query`. |
| "The GUID `44444444-…452` may conflict" | **No conflict** | The GUID was absent from the host and imported cleanly under its original value. |
| "The plan is named V5 on the system but V4 in the hive" | **Confirmed, and explained** | The artifact's stored `FriendlyName` is `Hone Ultimate Power Plan V4`; the registered scheme is `Hone Ultimate Power Plan V5`. Hone rewrites the name at import (INFERRED — no `powercfg /changename` child process is visible because the import is in-process via `PowerImportPowerScheme`). |

---

## 1. Locating and recovering the original plan

### 1.1 The artifact

```
Path   : C:\Users\alanz\AppData\Local\Programs\Hone\resources\assets\hone-resources\HoneV5.pow
Size   : 12,288 bytes
SHA256 : 84BF2B26407801F4A8B9182177DCAB9C52FCC562E01283E427FA5C264D51C6EE
Magic  : 72 65 67 66  ("regf") — a Windows registry hive, which is what powercfg /export writes
```

The install contains exactly three `.pow` files — `Balanced.pow` (8,192 B), `HighPerformance.pow` (8,192 B) and
`HoneV5.pow` (12,288 B). Only `HoneV5.pow` is the Hone plan; the other two are stock Windows plans (presumably used
by Hone's "restore" path). No other plan definition exists anywhere in the installation: a content search of
`resources\` for `HoneV5.pow`, `44444444-4444-4444-4444-444444444452` and `PowerImportPowerScheme` returned **no
matches**, i.e. the plan is not described in configuration text — the binary artifact is the only definition.

### 1.2 The runtime-registered scheme (strongest evidence)

```
File : runtime-lab\exp\k6ad89bg\after\CCS_Control.reg   (30,294,622 bytes, exported after Hone applied k6ad89bg)
Key  : HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\44444444-4444-4444-4444-444444444452
Lines: 133,233–133,359  → 33 keys, 27 ACSettingIndex/DCSettingIndex pairs
FriendlyName : "Hone Ultimate Power Plan V5"
Description  : "The Ultimate Power Plan to increase FPS, improve latency and reduce input lag."
```

Extracted to `runtime-lab\analysis\runtime-scheme-subtree.reg` and used as the **original** column in the comparison.

### 1.3 Mechanism (how the plan gets installed)

`powercfg /LIST` before → after, from the runtime capture:

```
<= 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c  (Paragon Perf V4 Power Plan)   [active]
=> 44444444-4444-4444-4444-444444444452  (Hone Ultimate Power Plan V5)   [active]
```

`HKLM\…\Control\Power\User\PowerSchemes\ActivePowerScheme` was rewritten to the Hone GUID. No `powercfg.exe` child
process appeared in the 4688 process telemetry, so the import and activation are done **in-process via the PowrProf
API** (`PowerImportPowerScheme` + `PowerSetActiveScheme`). The mechanism is INFERRED from the absence of a child
process plus the observed effect; the effect itself is VERIFIED-RUNTIME.

Because no per-setting write call was observed, and because all 27 stored values are already present in the artifact,
**the artifact fully explains the runtime scheme state**. That is what makes the reconstruction faithful rather than
approximate.

---

## 2. Complete setting inventory

### 2.1 Shape of the plan

| Metric | Count |
|---|---|
| Stored subgroup nodes | 6 |
| Real subgroups carrying settings | 5 — Hard disk, USB settings, Idle Resiliency, Processor power management, Display |
| Plan-personality node (reported by `powercfg` under `SUB_NONE`) | 1 — `245d8541-…` stored at scheme root |
| Stored settings (AC/DC pairs) | **27** |
| Hidden settings (`Attributes` bit 0 = hidden) | **22** |
| Visible settings | **5** — `DISKIDLE`, USB selective suspend, `VIDEOIDLE`, `VIDEONORMALLEVEL`, dimmed display brightness |
| Settings that differ from the Windows **High performance** default for the same setting | **25** |
| Settings identical to that default | 2 — `CPMINCORES` 100/100 and `VIDEONORMALLEVEL` 100/100 |
| Settings with no Windows default recorded | 0 |

### 2.2 Subgroups present

| Subgroup | GUID | Settings stored |
|---|---|---|
| Hard disk | `0012ee47-9041-4b5d-9b77-535fba8b1442` | 3 |
| USB settings | `2a737441-1930-4402-8d77-b2bebba308a3` | 3 |
| Idle Resiliency | `2e601130-5351-4d9d-8e04-252966bad054` | 1 |
| Processor power management | `54533251-82be-4824-96c1-47b60b740d00` | 15 |
| Display | `7516b95f-f776-4464-8c53-06167f40cc99` | 4 |
| *no subgroup* (`SUB_NONE` = `fea3413e-7e05-4911-9a71-700331f1c294`) | — | 1 (plan personality, stored at scheme root) |

### 2.3 Full 27-row table

`Orig` = value recovered from the runtime-registered scheme (and identical in the shipped artifact).
`Recon` = value in `Hone-Ultimate-Power-Plan-V5-Reconstructed.pow` after import.
"Differs from HP default" compares against Windows' own *High performance* default for that setting
(`PowerSettings\…\DefaultPowerSchemeValues\8c5e7fda…`) — a comparison anchor, **not** a claim about Hone's intent.

| # | Subgroup | Setting | Alias | Hidden | Orig AC | Orig DC | Recon AC | Recon DC | Match | Differs from HP default |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | Hard disk `0012ee47` | Turn off hard disk after | `DISKIDLE` | no | 3600 | 600 | 3600 | 600 | Exact | yes |
| 2 | Hard disk `0012ee47` | Secondary NVMe Idle Timeout | — | yes | 4000 | 1000 | 4000 | 1000 | Exact | yes |
| 3 | Hard disk `0012ee47` | Primary NVMe Idle Timeout | `NVMEPRIMARYIDLETIMEOUT` | yes | 500 | 100 | 500 | 100 | Exact | yes |
| 4 | *SUB_NONE* `fea3413e` | Power plan type | `PERSONALITY` | yes | 1 | 2 | 1 | 2 | Exact | yes |
| 5 | USB `2a737441` | Hub Selective Suspend Timeout | — | yes | 200 | 50 | 200 | 50 | Exact | yes |
| 6 | USB `2a737441` | USB selective suspend setting | — | no | 0 | 0 | 0 | 0 | Exact | yes |
| 7 | USB `2a737441` | USB 3 Link Power Mangement *(sic)* | — | yes | 0 | 0 | 0 | 0 | Exact | yes |
| 8 | Idle Resiliency `2e601130` | Processor Idle Resiliency Timer Resolution | `PROCIR` | yes | 65000 | 65000 | 65000 | 65000 | Exact | yes |
| 9 | Processor `54533251` | Processor performance core parking min cores | `CPMINCORES` | yes | 100 | 100 | 100 | 100 | Exact | no (= HP default) |
| 10 | Processor `54533251` | …core parking min cores for Processor Power Efficiency Class 1 | `CPMINCORES1` | yes | 100 | 100 | 100 | 100 | Exact | yes |
| 11 | Processor `54533251` | …decrease threshold for Processor Power Efficiency Class 1 | `PERFDECTHRESHOLD1` | yes | 10 | 30 | 10 | 30 | Exact | yes |
| 12 | Processor `54533251` | Initial performance for … Class 1 when unparked | `HETEROCLASS1INITIALPERF` | yes | 100 | 50 | 100 | 50 | Exact | yes |
| 13 | Processor `54533251` | …core parking increase time | `CPINCREASETIME` | yes | 1 | 7 | 1 | 7 | Exact | yes |
| 14 | Processor `54533251` | …core parking parked performance state | `CPPERF` | yes | 1 | 1 | 1 | 1 | Exact | yes |
| 15 | Processor `54533251` | …parked performance state for … Class 1 | `CPPERF1` | yes | 2 | 1 | 2 | 1 | Exact | yes |
| 16 | Processor `54533251` | Processor performance time check interval | `PERFCHECK` | yes | 5000 | 5000 | 5000 | 5000 | Exact | yes |
| 17 | Processor `54533251` | Latency sensitivity hint min unparked cores/packages for … Class 1 | `LATENCYHINTUNPARK1` | yes | 25 | 0 | 25 | 0 | Exact | yes |
| 18 | Processor `54533251` | Latency sensitivity hint processor performance for … Class 1 | `LATENCYHINTPERF1` | yes | 100 | 50 | 100 | 50 | Exact | yes |
| 19 | Processor `54533251` | …decrease time for … Class 1 | `HETERODECREASETIME` | yes | 5 | 3 | 5 | 3 | Exact | yes |
| 20 | Processor `54533251` | Heterogeneous short running thread scheduling policy | `SHORTSCHEDPOLICY` | yes | 2 | 5 | 2 | 5 | Exact | yes |
| 21 | Processor `54533251` | Processor performance boost mode | `PERFBOOSTMODE` | yes | 2 | 1 | 2 | 1 | Exact | yes |
| 22 | Processor `54533251` | Processor idle time check | `IDLECHECK` | yes | 200000 | 200000 | 200000 | 200000 | Exact | yes |
| 23 | Processor `54533251` | …decrease time for … Class 1 *(second variant)* | `PERFDECTIME1` | yes | 2 | 1 | 2 | 1 | Exact | yes |
| 24 | Display `7516b95f` | Dim display after | `VIDEODIM` | yes | 885 | 60 | 885 | 60 | Exact | yes |
| 25 | Display `7516b95f` | Turn off display after | `VIDEOIDLE` | no | 2700 | 90 | 2700 | 90 | Exact | yes |
| 26 | Display `7516b95f` | Display brightness | `VIDEONORMALLEVEL` | no | 100 | 100 | 100 | 100 | Exact | no (= HP default) |
| 27 | Display `7516b95f` | Dimmed display brightness | — | no | 50 | 40 | 50 | 40 | Exact | yes |

Rows 19 and 23 render the **same friendly name** in this Windows build ("Processor performance decrease time for
Processor Power Efficiency Class 1") because the two resource IDs (`powrprof.dll,-1229` and `-1207`) collide. They are
distinct settings, distinguished by alias (`HETERODECREASETIME` vs `PERFDECTIME1`) and by GUID. This is a Microsoft
localisation defect, recorded here rather than "resolved" by guessing nicer names.

Full per-row descriptions, possible-value maps and provenance are in `Hone-Ultimate-Power-Plan-V5-Comparison.csv`.

### 2.4 Discrete-setting semantics (what the numbers actually mean)

| Setting | Value | Rendered meaning |
|---|---|---|
| `PERSONALITY` | AC 1 / DC 2 | AC = **High performance**, DC = **Balanced**. The plan is deliberately "high performance on mains, balanced on battery". |
| `PERFBOOSTMODE` | AC 2 / DC 1 | AC = **Aggressive**, DC = **Enabled** (HP default is Aggressive for both). |
| `SHORTSCHEDPOLICY` | AC 2 / DC 5 | AC = **Prefer performant processors** (P-cores preferred for short threads), DC = **Automatic** (HP default is Automatic for both). This is Hone's heterogeneous-scheduling change. |
| USB selective suspend | 0 / 0 | **Disabled** (HP default is Enabled). |
| `CPPERF` / `CPPERF1` | 1 / 2 | Parked cores are held at performance state 1 / 2 rather than a deep low-power state. |

Time-based values are in seconds (`DISKIDLE`, `VIDEOIDLE`, `VIDEODIM`, NVMe timeouts) except the "time check
interval" / "idle time check" settings, which are in 100 µs units (`PERFCHECK` 5000 = 500 ms; `IDLECHECK` 200000 = 20 s).

### 2.5 What is *not* in the plan — and why this matters

The plan deliberately stores **no** settings for these subgroups. Their effective values are inherited from Windows,
and they are **not** Hone's configuration:

| Subgroup | Stored by Hone? | Effective (inherited) value on the imported scheme |
|---|---|---|
| PCI Express (`501a4d13-…`) | **No** | Link state power management AC 0 (off) / DC 2 (moderate) — Windows default |
| Wireless Adapter Settings (`19cbb8fa-…`) | **No** | Power saving mode AC 0 (max performance) / DC 2 (medium) — Windows default |
| Sleep (`238c9fa8-…`) | **No** | Sleep after AC 600 s / DC 180 s; hibernate AC never / DC never; hybrid sleep on — Windows defaults |
| Battery / energy saver | **No** | not configured |
| Processor min/max state (`893dee8e` / `bc5038f7`) | **No** | min 100 % on AC comes from the *High performance personality*, not from a stored value |
| Buttons, Internet Explorer, Desktop background, Multimedia, Intel graphics | **No** | Windows defaults |

This is the single most important scope finding besides the 27 rows themselves:

* **Hone does not disable PCIe ASPM through the power plan.** `ASPMOptOut = 1` is written to
  `SYSTEM\CurrentControlSet\Services\pci\Parameters` — a driver-level registry switch, section 3, category 2.
  Anyone reconstructing this from the `.pow` alone would miss it entirely, and anyone folding it into the `.pow`
  would be inventing a setting that does not exist there.
* **Hone does not touch wireless power saving in the plan** — despite shipping a "network performance" tweak.
* **Sleep behaviour is not configured.** So a device that Hone puts to sleep does so on Windows' schedule.

---

## 3. Separating the power plan from the rest of the optimisation

Tweak `k6ad89bg` performs **71 granular operations**. The tracker recorded `operation_count = 48` for this tweak,
which is a *group-level* count; the discrepancy is logged as D-5 in section 7.

### Category 1 — actual power-scheme settings (the only things that belong in the `.pow`)

**27 settings** (section 2.3), plus 2 scheme-lifecycle operations — `PowerImportPowerScheme` and
`PowerSetActiveScheme` — which are reproduced by `powercfg /import` and (deliberately not performed) `/setactive`.

### Category 2 — separate Windows registry modifications (NOT in the `.pow`)

| # | Key / value | After | What it does |
|---|---|---|---|
| 1 | `Control\Power` → `PlatformAoAcOverride` | 0 | Forces classic S3 instead of Modern Standby (S0ix) |
| 2 | `Control\Power` → `PerfBoostAtGuaranteed` | 1 | Boost allowed at the guaranteed-frequency floor (created) |
| 3 | `Control\Storage` → `StorageD3InModernStandby` | 0 | Keeps storage out of D3 during standby |
| 4 | `Control\Power\EnergyEstimation\TaggedEnergy` → `DisableTaggedEnergyLogging` | 1 | was 0 |
| 5 | … → `TelemetryMaxApplication` | 0 | was 250 |
| 6 | … → `TelemetryMaxTagPerApplication` | 0 | was 50 |
| 7 | `Services\stornvme\Parameters\Device` → `IdlePowerMode` | 0 | NVMe APST / idle power states off |
| 8 | `Services\pci\Parameters` → `ASPMOptOut` | 1 | PCIe ASPM disabled (driver-level, **not** the PCIe power setting) |
| 9 | `HKLM\SOFTWARE\Policies\Microsoft\Windows\fssprov` → `EncryptProtocol` | 0 | key created |

### Category 3 — services and scheduled tasks

| # | Item | Change |
|---|---|---|
| 1–5 | `DcomLaunch`, `RpcEptMapper`, `RpcSs`, `Schedule`, `SystemEventsBroker` | forced Automatic + started (already Automatic on stock Windows — no-op safety net) |
| 6 | task `\DisablePowerSaving` (`0A4707B7-B8C2-42D6-9680-1F09EB227ED3`) | **created** (purpose unconfirmed — no task XML captured) |
| 7 | task `\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem` | **disabled** |

### Category 4 — device-specific configuration

| # | Item | Change |
|---|---|---|
| 1–14 | 7 PCI devices × `Device Parameters\WDF` → `WdfDirectedPowerTransitionEnable = 0`, `IdleInWorkingState = 0` | Intel PCH root ports/`DEV_51E0` family, `DEV_A74F`, `DEV_A71E`, NVIDIA `VEN_10DE DEV_22BE` (RTX GPU) |
| 15 | `Control\Class\{4d36e972-…}\0012` → `PnPCapabilities` | `0x10` → `0x18` (NIC power management off) |

### Category 5 — unrelated components of the wider optimisation

| # | Item | Change |
|---|---|---|
| 1–3 | `WINEVT\Channels\…\Diagnostic` (`Kernel-Processor-Power`, `SleepStudy`, `UserModePowerService`) | `Enabled` 1 → 0 |
| 4–6 | `Control\WMI\Autologger\EventLog-Microsoft-Windows-*-Diagnostic` | blocks deleted |
| 7–9 | `Control\WMI\Security` | 3 provider GUID ACL entries removed (`e01f9318…`, `e153e8ff…`, `2e0e91fe…`) |
| 10 | `%WinDir%\System32\Microsoft-windows-sleepstudy-events.dll` | **renamed** to `.old` (file mutation inside System32) |
| 11 | `Session Manager\Power` | `HiberbootEnabled`-area state blob rewritten as a side effect of activation |

**None of categories 2–5 is in the deliverable.** The `.pow` contains exactly the 27 scheme settings and nothing else —
which is what Windows' own export format can carry. Items 10 and 11 are not reproducible by any power-management
interface at all.

---

## 4. Reconstruction

### 4.1 Method

```
powercfg /import  "<Hone install>\resources\assets\hone-resources\HoneV5.pow"  44444444-4444-4444-4444-444444444452
powercfg /changename 44444444-4444-4444-4444-444444444452  "Hone Ultimate Power Plan V5"
powercfg /export  "...\Hone-Ultimate-Power-Plan-V5-Reconstructed.pow"  44444444-4444-4444-4444-444444444452
```

The deliverable is therefore a **genuine `powercfg /export` product**: a registry hive written by Windows' own
power-management tooling, importable with `powercfg /import`. It is not a renamed hive, not a `.reg`, not a
hand-written file, and not an export of some other plan (Balanced / Ultimate Performance were never involved).

### 4.2 GUID reuse

The GUID `44444444-4444-4444-4444-444444444452` did not exist on the host (`powercfg /list` showed only Balanced and
Paragon Perf V4). The scheme was imported **under its original GUID**, so no conflict arose and no "equivalent with a
different GUID" fallback was needed. Had it existed, the plan was to delete the pre-existing scheme only after
proving it was a leftover of ours, and otherwise to document the conflict — that path was not taken.

### 4.3 Name: V4 in the artifact, V5 on the system

The artifact stores `FriendlyName = "Hone Ultimate Power Plan V4"`. The scheme Hone actually registers is
`"Hone Ultimate Power Plan V5"` (runtime hive export, and `powercfg /LIST` at capture time). The deliverable carries
**V5**, because that is the plan's name as it exists on a real system; V4 is a stale string in a shipped asset.

This is the **only** metadata difference between the deliverable and the raw artifact, it was produced by
`powercfg /changename` (a Windows power-management command, not by editing bytes), and both names are recorded here
and in the manifest. Everything else — every subgroup, every setting GUID, every AC and DC value, the description
text — is bit-for-bit what the artifact stores.

### 4.4 What was deliberately *not* done

* No generic **Ultimate Performance** plan was duplicated. `e9a42b02…` / `ded574b5…` were never used.
* No Balanced or High performance scheme was modified and relabelled.
* No setting was added because it "should" be there. The plan's absence of PCIe/wireless/sleep settings is preserved.
* No activation on the host (section 0.3).

---

## 5. Validation

Full transcript: `Hone-Ultimate-Power-Plan-V5-Validation.txt` (raw command output, no editing).
Summary of what was executed:

| # | Check | Result |
|---|---|---|
| 1 | Pre-state captured (`/getactivescheme`, `/list`) | active `8c5e7fda…` Paragon Perf V4 |
| 2 | `powercfg /import HoneV5.pow 44444444-…` | **exit 0**, "Imported Power Scheme Successfully" |
| 3 | Active scheme after import | **unchanged** |
| 4 | `powercfg /query <guid>` (visible settings) | 288 lines, 28 visible settings enumerated |
| 5 | `powercfg /qh <guid>` (hidden included) | 1,732 lines, entire power-settings database for the scheme |
| 6 | Registry enumeration of the stored scheme | 33 keys / **27 AC-DC pairs** |
| 7 | `powercfg /changename` → "Hone Ultimate Power Plan V5" | exit 0 |
| 8 | `powercfg /export` → deliverable | exit 0, 12,288 bytes, sha256 `62AC…` (see manifest) |
| 9 | `powercfg /delete` | exit 0, key absent |
| 10 | `powercfg /import` deliverable under the same GUID | exit 0, name and description read back correctly |
| 11 | Registry comparison, original import vs re-import | **identical — every subgroup, setting GUID, AC and DC** |
| 12 | `powercfg /export` again → re-export | exit 0; 314 of 12,288 bytes differ |
| 13 | Binary diff of the 314 bytes | confined to hive header bookkeeping: log sequence (0x0C–0x0E), last-write timestamp/checksum (0x70, 0x80, 0x94), minor-version/sequence (0x1FC–0x1FE), per-cell write timestamps |
| 14 | Three-way value comparison (runtime / artifact / reconstruction) | **27 / 27 equal** |
| 15 | Cleanup: delete scheme, re-verify | scheme absent, active scheme `8c5e7fda…` restored |
| 16 | Control test: `duplicatescheme`, then `/list` | new scheme **is** listed immediately → the `/list` behaviour in item 17 is specific to the imported plan, not broken tooling |
| 17 | `powercfg /list` after import | **scheme not listed** (see 6.3) |
| 18 | Activation | **NOT TESTED** — safety override |

**Limitations that survived round-tripping:** none at the value level. All 27 settings and both metadata strings
survive export → delete → import → export unchanged.

**Limitation that did not survive:** byte-identical re-export is impossible for a `.pow`, because the format is a
registry hive carrying write timestamps, log sequence numbers and checksums. Semantic equality (item 11) is the
correct assertion and it holds exactly.

---

## 6. Fidelity and confidence

### 6.1 Directly recovered (highest confidence)

* All **27** stored settings, with **exact AC and DC** values, from the runtime-registered scheme.
* Both metadata strings (`FriendlyName`, `Description`) verbatim.
* The scheme GUID.
* The complete subgroup set — and, just as importantly, the complete list of subgroups the plan does *not* touch.

### 6.2 Inferred (stated, not assumed)

* That Hone rewrites `FriendlyName` at import rather than shipping a second hive — inferred from V4-in-artifact vs
  V5-on-system plus the absence of a `powercfg.exe` child process. Not observed directly.
* That the runtime scheme contains exactly these 27 and no others. The post-apply hive export contains 33 keys and
  27 pairs, which *is* the complete registered scheme — so this is close to direct, but the export is a point-in-time
  snapshot and HoneSvc could in principle write scheme values later. No evidence of such writes exists.
* That the Windows **High performance** personality default is the right comparison baseline for "modified vs default"
  (the plan's own `PERSONALITY` value is AC 1 = High performance, which is why that baseline was chosen).
* That the two `powrprof.dll` resource IDs that render identically are a localisation defect rather than two
  intentionally-identical settings.

### 6.3 Unknown / unresolved

| ID | Item | Why it is unresolved |
|---|---|---|
| U-1 | Whether the reconstructed plan can be **activated** | Not tested: activating would apply it to the primary machine (§7). Hone's own plan was observed active at runtime, but that proves the original can be activated, not the reconstruction. |
| U-2 | Why `powercfg /list` omits the imported scheme on this build | The scheme is present, queryable and exportable; a `duplicatescheme` scratch plan *is* listed. The shipped `HoneV5.pow` behaves identically, so the reconstruction reproduces the behaviour faithfully — but the cause is not established. The most likely explanation is that Hone also activates the scheme in the interactive session, and activation is what promotes it into `/list`. |
| U-3 | The plan's intended *hardware* targeting | The artifact stores NVMe timeouts and a P/E-core scheduling policy, which suggests a modern hybrid-CPU desktop, but nothing in the plan asserts a target. |
| U-4 | Whether Hone's UI ever writes scheme values after import (e.g. a "V5 refresh" path) | No runtime write was observed; not exhaustively ruled out. |

### 6.4 Confidence statement

> **27 of 27 settings recovered and verified; 27 of 27 reproduce exactly.**
> Confidence that the `.pow` contains the complete stored setting set of the original plan: **high** — three
> independent sources (shipped hive, runtime hive export, reconstruction round-trip) agree value-for-value.
> Confidence that no *additional* scheme setting exists anywhere in Hone: **medium** — that rests on the runtime state
> being fully explained by the artifact, which it is, but it is an inference over a snapshot rather than an
> exhaustive read of Hone's code paths.
>
> This is **not** a claim of 100 % reconstruction of "Hone's power optimisation". It is a 100 % match of the power
> *scheme*, and categories 2–5 of section 3 are explicitly outside the `.pow`.

---

## 7. Discrepancies

| ID | Discrepancy | Resolution |
|---|---|---|
| D-1 | Artifact `FriendlyName` = **V4**, registered scheme = **V5** | Deliverable carries V5 (the name on a real system); V4 preserved in the manifest. Rename performed with `powercfg /changename`. |
| D-2 | Earlier report captured **~12** setting rows, artifact stores **27** | The 12 came from a diff-based capture. The 15 missing rows are all hidden settings. Not a Hone version difference. |
| D-3 | Two processor settings render the **same friendly name** (`powrprof.dll,-1229` and `-1207`) | Reported verbatim; distinguished by GUID and alias. Microsoft resource collision. |
| D-4 | Earlier conclusion "`HoneV5.pow` is not a real `.pow`" | Wrong. `.pow` exports are registry hives; `powercfg /import` accepts it. |
| D-5 | Tracker records `operation_count = 48` for `k6ad89bg`; granular re-enumeration of the same evidence card yields **71** (2 lifecycle + 27 scheme + 9 category 2 + 7 category 3 + 15 category 4 + 11 category 5) | 48 is a group-level count. 71 is the granular count and supersedes it for scoping. |
| D-6 | `powercfg /list` omits the imported scheme on this build | Documented as U-2; behaves identically for the shipped artifact, so no fidelity loss. |

---

## 8. Reproducing this yourself

```powershell
# 1. inspect the shipped artifact
Get-FileHash "<Hone>\resources\assets\hone-resources\HoneV5.pow" -Algorithm SHA256
#    84BF2B26407801F4A8B9182177DCAB9C52FCC562E01283E427FA5C264D51C6EE

# 2. confirm the GUID is free (adjust if it is not)
powercfg /list

# 3. import under the original GUID
powercfg /import "<Hone>\resources\assets\hone-resources\HoneV5.pow" 44444444-4444-4444-4444-444444444452

# 4. enumerate, including hidden settings
powercfg /q  44444444-4444-4444-4444-444444444452
powercfg /qh 44444444-4444-4444-4444-444444444452
reg query "HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\44444444-4444-4444-4444-444444444452" /s

# 5. apply the runtime-observed name and export
powercfg /changename 44444444-4444-4444-4444-444444444452 "Hone Ultimate Power Plan V5"
powercfg /export "Hone-Ultimate-Power-Plan-V5-Reconstructed.pow" 44444444-4444-4444-4444-444444444452

# 6. clean up (do not activate on a machine you care about)
powercfg /delete 44444444-4444-4444-4444-444444444452
```

To verify the deliverable in a disposable VM (the isolation this investigation could not provide):

```powershell
powercfg /import "Hone-Ultimate-Power-Plan-V5-Reconstructed.pow" 44444444-4444-4444-4444-444444444452
powercfg /setactive 44444444-4444-4444-4444-444444444452     # item 6 of the task, untested here
powercfg /getactivescheme
powercfg /export  "reexport.pow" 44444444-4444-4444-4444-444444444452
```

---

## 9. Deliverables in this directory

| File | Contents |
|---|---|
| `Hone-Ultimate-Power-Plan-V5-Reconstructed.pow` | Genuine `powercfg /export` of the reconstructed scheme, GUID `44444444-…452`, name "Hone Ultimate Power Plan V5" |
| `Hone-Ultimate-Power-Plan-V5-Report.md` | This document |
| `Hone-Ultimate-Power-Plan-V5-Comparison.csv` | 27 rows × 25 columns: both values from three sources, match, defaults, aliases, hidden flags, descriptions, provenance |
| `Hone-Ultimate-Power-Plan-V5-Validation.txt` | Raw pre-state, import, enumeration, round-trip, diff and cleanup output |
| `Hone-Ultimate-Power-Plan-V5-Manifest.json` | GUID, name, counts, hashes, validation status, unresolved items |
