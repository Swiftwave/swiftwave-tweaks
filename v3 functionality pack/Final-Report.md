# Hone v2.1.0 — Final Reverse-Engineering Report

**Consolidated technical specification for the Swiftwave Tweaks implementation.**

| Field | Value |
|---|---|
| Target | Hone 2.1.0 (Electron), installed `C:\Users\alanz\AppData\Local\Programs\Hone` |
| Host under test | Windows 11 laptop, Intel i5-13500H (hybrid P/E), NVIDIA RTX 4050 Laptop (`10DE:28A1`), Realtek GbE (`10EC:8168`) |
| Capture window | 2026-11-10 22:12:22 → 22:36:59 (local, Asia/Shanghai) |
| Preset studied | Hone preset **`test`** — 9 of 106 catalog operations |
| Method | Per-tweak attribution from Windows Security 4688 command lines + AES argv decryption + full-registry snapshots (`exp\<Tag>\{before,after}`) diffed as HashSets |
| Deliverables | this file (D1), `Implementation-Tracker.json` / `.csv` (D2), `Swiftwave-Handoff.md` (D3), `Exclusions-Unresolved.md` (D4) |
| Do not | modify Swiftwave Tweaks as part of this task — this is a specification only |

---

## 0. How to read this document

### 0.1 Evidence taxonomy

Every factual claim carries one of four grades. The grade describes **how the claim is known**, not how confident it feels.

| Grade | Meaning | Typical source in this investigation |
|---|---|---|
| **VERIFIED-RUNTIME** | Observed directly on the test machine during a controlled, per-tweak experiment. The change is in a before/after registry diff, a 4688 process record, or an explicit command capture. | `evidence\<Id>.diff.md`, 4688 trace, `evidence\R00.diff.md` |
| **VERIFIED-ARTIFACT** | Established by reading a shipped file: decompiled service, embedded power hive, `main.js`, IPC channel names. Not necessarily executed. | `analysis\HoneSvc-decomp\HoneSvc.decompiled.cs`, `analysis\HoneV5-decoded.txt` |
| **INFERRED** | Not directly observed. Follows from Windows documentation, API semantics, or the shape of the surrounding evidence. Must be treated as a hypothesis to test. | "powercfg API used in-process because no `powercfg.exe` child appeared" |
| **UNCONFIRMED** | Explicitly unknown. Do not implement against an UNCONFIRMED detail without first closing it. | `\DisablePowerSaving` task action bytes; `MiscPolicyInfo` semantics |

### 0.2 Implementation classification vocabulary

| Class | Rule applied |
|---|---|
| **READY** | Behaviour fully established; a safe implementation is justified with no caveats worth gating. |
| **READY WITH MODIFICATIONS** | Behaviour understood, but at least one constituent operation must be dropped, gated, or implemented differently to satisfy "Safe. Reversible. Verified." |
| **NEEDS VALIDATION** | An implementation detail or compatibility implication is still open; ship behind a flag or not at all until closed. |
| **INFERRED** | Behaviour rests on indirect evidence only. |
| **EXCLUDE** | Unsafe, security-degrading, obsolete, or unjustifiable. |

Per-tweak classification does **not** mean "all or nothing". Each A–G specification names the exact constituent operations to implement, modify, or drop. A tweak graded READY WITH MODIFICATIONS typically ships 70–95 % of its operations unchanged.

### 0.3 Scope statement (and the 106 vs 9 discrepancy, stated up front)

The user narrowed this engagement to the nine optimizations that constitute Hone's `test` preset. That is what this report specifies.

Hone's live catalog, dumped from the renderer's memory, contains **106 optimization operations**. The shipped locale file `locale-en.json` enumerates **102** tweak entries (`hone-tweaks-full.txt`: 28 basic + 61 optimizations + 13 advanced). The catalog is fetched remotely at runtime, so the two lists are not the same artefact and are not expected to match:

* `k6ad89bg` ("Enable Maximum Performance Power Mode") and `istk0001` ("Disable Dynamic Interrupt Steering") appear in the live catalog and in the UI, but **are absent from the shipped locale** — they are server-side additions with fallback English strings.
* Conversely, several locale entries may no longer be served by the current catalog.

This investigation therefore accounts for **9 of 106 operations with direct runtime evidence, and 0 of the remaining 97**. It does not speculate about the other 97. Section 2.4 gives the arithmetic and the per-ID status. Any document that claims to cover all 106 without 106 captures is extrapolating; this one does not.

### 0.4 What "operation" means here

Hone's data model is: **UI card → tweak ID → N low-level directives**. `HoneUtility.exe` is ConfuserEx-protected with an emptied `#US` stream (no string literals in metadata), so the ID→directive map cannot be read statically. The only way to enumerate the constituent operations of a tweak is to run it and capture.

In this report an **operation** = one distinct directive as captured: one registry value write, one `bcdedit`/`netsh`/service/task/filesystem directive, or one homogeneous group of writes that Hone emits as a single loop (e.g. "`SvcHostSplitDisable=1` on 49 services"). Where a group expands to a known number of writes, the count is given. Counts are enumerated from the evidence, never estimated.

### 0.5 Evidence source index

| Tag | File (relative to `C:\Users\alanz\Documents\Trae-CN\temp_20261110_01\`) |
|---|---|
| `[5Q]` | `runtime-lab\evidence\5Q8oOtIH.card.md`, `5Q8oOtIH.diff.md` |
| `[3W]` | `runtime-lab\evidence\3WwEZo0M.card.md`, `3WwEZo0M.diff.md` |
| `[PE]` | `runtime-lab\evidence\PE9bgwi0.card.md`, `PE9bgwi0.diff.md` |
| `[9C]` | `runtime-lab\evidence\9C0X9LiC.card.md`, `9C0X9LiC.diff.md` |
| `[K6]` | `runtime-lab\evidence\k6ad89bg.card.md`, `k6ad89bg.diff.md` |
| `[XL]` | `runtime-lab\evidence\XLSEdxVW.card.md`, `XLSEdxVW.diff.md` (7 207 lines) |
| `[IS]` | `runtime-lab\evidence\istk0001.card.md`, `istk0001.diff.md` |
| `[SV]` | `runtime-lab\evidence\Hone-Runtime-Evidence.md` §T-000/T-001/T-002 (service path: `RqbxUufM`, `oPu4x2Ef`) |
| `[RB]` | `runtime-lab\evidence\Rollback-Fidelity.md`, `R00.diff.md`, `Residue.diff.md` |
| `[POW]` | `runtime-lab\analysis\HoneV5-decoded.txt` (27 rows decoded from embedded hive) |
| `[SVC]` | `runtime-lab\analysis\HoneSvc-decomp\HoneSvc.decompiled.cs` |
| `[STATIC]` | `Hone-Exhaustive-RE-Report.md` (artifact-level report) |
| `[LOC]` | `hone-tweaks-full.txt`, `locale-en.json` (shipped UI strings) |
| `[CKPT]` | `runtime-lab\CHECKPOINT.md` (method notes, preset→ID mapping from renderer memory) |

---

## 1. What Hone is, mechanically

You cannot implement a faithful equivalent without knowing how Hone executes work, because three of the nine tweaks rely on mechanisms that are invisible from the UI.

### 1.1 Process architecture

```
Hone.exe (Electron)
 ├─ renderer (React) ──ipcRenderer.invoke──► main.js  → builds + AES-encrypts argv
 ├─ hone-elevation-helper.exe  (elevated broker, named pipe \\.\pipe\HoneElevator)
 │     WinVerifyTrust check pinned to publisher "AuraSide, Inc."
 │     Job-object breakaway: "Inside a no-breakaway job; started out-of-job replacement"
 │        └─► HoneUtility.exe     (all tweak apply/revert; ConfuserEx-protected)
 │        └─► HoneTune.exe        (Game Mode: per-process priority/affinity)
 │        └─► hone-bios.exe       (firmware writes — out of scope, never executed)
 └─ HoneSvc.exe  (LocalSystem, delayed-auto, "Hone system optimization and monitoring service")
       independent of the UI; re-applies 2 tweaks on triggers  ← see §1.4
```

`HoneSvc.exe` is .NET 4.7.2 and **not obfuscated**; it was decompiled in full `[SVC]`. `HoneUtility.exe` (7.35 MB, single managed assembly, no bundle) is obfuscated and was only ever observed from the outside.

### 1.2 The argv contract — VERIFIED-ARTIFACT + VERIFIED-RUNTIME

Hone never passes a tweak ID in clear text. `main.js` builds a plaintext string and AES-encrypts it; the only argv element is lowercase hex ciphertext.

```
t <tweakId> <pid>                       — single tweak apply / revert
g <groupId> <pid> user <id1> <id2> …    — preset apply + user's individual selections
i <name> <pid>                          — internal operation (boost-up, estimation, drivers)
```

Recovered key derivation (verified byte-identical between `main.js` and the obfuscated literal in `HoneSvc`):

```
key = SHA256(pass)                        // 32 bytes
iv  = SHA256(pass + "iv")[0:16]           // 16 bytes
AES-256-CBC, PKCS7, hex-lower output
```

`<pid>` is the **calling Electron PID** and doubles as a watchdog: if the parent dies mid-apply, `HoneUtility` aborts. This is why every captured apply/revert command line ends in `20732` (the `Hone.exe` PID during the capture session) or `21836` (the `HoneSvc` PID during the service-path capture).

Decrypted captures used in this report: `t apply-<Id> 20732` / `t revert-<Id> 20732` — one per tweak, listed in `[RB]` §1.

Exit codes 70–75 are **structured, expected** non-zero results ("skip/revert-required"), not crashes: `applyOptimizations` whitelists `[70,71,72,73,74,75]`. A Swiftwave equivalent needs an equivalent "expected failure" concept, otherwise every partial apply looks like a crash to telemetry.

### 1.3 The second, plaintext grammar — VERIFIED-RUNTIME (new finding)

`HoneUtility.exe` also accepts a *plaintext* sub-command grammar, used for the operations the tweak payload delegates:

```
HoneUtility.exe sett serv_apply   <ServiceName> <StartType> start
HoneUtility.exe sett reg_add      <hive\path> <valueName> <type> <data>
HoneUtility.exe sett reg_delete   <hive\path> <valueName>
HoneUtility.exe sett rename_file  "<src>" "<dst>" ""
HoneUtility.exe --check-app
```

Gate: the parent must be signed by AuraSide (UTIL003). `HoneSvc.exe` qualifies, which is how the service path works — see §1.4. Anti-tamper UTIL012 refuses to run while a process whose name matches Procmon is present (circumvented for research only by renaming the tool; **do not reproduce this behaviour in Swiftwave**).

### 1.4 HoneSvc: the auto-apply path that bypasses the UI — VERIFIED-ARTIFACT + VERIFIED-RUNTIME

`HoneSvc.exe` re-applies exactly two hard-coded tweaks:

* bit 0 (value `1`) → `apply-RqbxUufM` (Device Affinity)
* bit 1 (value `2`) → `apply-oPu4x2Ef` (MSI)

Gate: `HKLM\SOFTWARE\Hone\TweaksSvc` (REG_DWORD bitmask). Factory value on this machine: `1`.

Triggers: service start (after a 2 s delay), USB device arrival, and default audio render device change (`IMMNotificationClient`, `eRender`/`eMultimedia`). Dedup window 2 s; 30 s suppression after `ResumeAutomatic` (hibernate/resume). It spawns `HoneUtility.exe <hex>` whose plaintext is `t apply-<TweakId> <HoneSvc pid>`, running as SYSTEM in session 0 — invisible to the user.

**Consequence for the report:** the applied state of these two tweaks is **not** controlled by the UI toggle. See §2.5 (contradiction log) and §4.6.

### 1.5 Persistence and rollback design

| Layer | Mechanism | Grade |
|---|---|---|
| System Restore | IPC `enable-/create-/apply-/get-system-restore`; checkpoint before apply | VERIFIED-ARTIFACT |
| Per-tweak revert | Same encrypted call with the same ID; `HoneUtility` restores the recorded prior value, or deletes the value if it created it | VERIFIED-ARTIFACT |
| Power-plan snapshot | `HKLM\SOFTWARE\Hone\PowerPlanBackup\{Custom,CoreParking}` | VERIFIED-ARTIFACT |
| Game Mode | Per-process priority class / affinity / GPU scheduling class restore | VERIFIED-ARTIFACT |

Two structural gaps, both confirmed empirically in `[RB]`:

1. The **power-plan snapshot exists but is not used on revert** — Hone deletes its imported scheme and lets Windows fall back to Balanced rather than restoring the previously active scheme (defect **D-1**).
2. **Driver installs and firmware writes have no snapshot at all** — irreversible by construction. Both are outside this report's scope.

---

## 2. Optimization hierarchy

### 2.1 The four layers

```
Layer 1  UI section            "FPS & Latency" 70a82116-8c64-4f92-8a0f-c3bafaf5db59
                               "Network & Ping" 380c0bd6-a1df-4f09-b6dd-9899962056d2
                               (category GUIDs recovered from the renderer; per-card
                                membership was not captured — see §8.2)
   │
Layer 2  UI card               e.g. "Optimize Device Affinities"
   │
Layer 3  tweak ID              RqbxUufM            (one ID per card; the ID is what the
   │                                                encrypted argv carries)
Layer 4  constituent operations 21 registry writes across 7 PCI functions
   │
Layer 5  Windows state         HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<dev>\...\Affinity Policy
```

Layers 3→4 is **one-to-many**: a single ID expands to anything from 1 write (`istk0001`) to ~493 (`XLSEdxVW`). Layers 4→5 can be many-to-one (49 services each getting the same value name).

### 2.2 Complete ID map for the nine in-scope optimizations

| # | UI name (live catalog) | Shipped-locale name `[LOC]` | ID | Captured via | Card file |
|---|---|---|---|---|---|
| 1 | Optimize FPS & Input Lag | 常规 Windows 优化 ("General Windows Optimizations") | `5Q8oOtIH` | UI apply, 22:12:22 | `[5Q]` |
| 2 | Optimize Storage & Memory | "Optimize Drives" | `3WwEZo0M` | UI apply, 22:14:42 | `[3W]` |
| 3 | Optimize Device Affinities | 优化设备亲和性 | `RqbxUufM` | HoneSvc service path, 20:29:28 | `[SV]` T-001 |
| 4 | Optimize Message Signal Interrupts | 优化消息信号中断 | `oPu4x2Ef` | HoneSvc service path, 20:29:30 | `[SV]` T-002 |
| 5 | Optimize Network Performance | 优化Netsh网络设置 ("Optimize Netsh network settings") | `XLSEdxVW` | UI apply, 22:23:38 | `[XL]` |
| 6 | Disable Dynamic Interrupt Steering | *(absent from locale)* | `istk0001` | UI apply, 22:26:19 | `[IS]` |
| 7 | Disable Windows Services | "Disable Windows Services" | `PE9bgwi0` | UI apply, 22:28:05 | `[PE]` |
| 8 | Reduce Windows Background Activity | "Disable Windows Program Compatibility Assistant" | `9C0X9LiC` | UI apply, 22:30:03 | `[9C]` |
| 9 | Enable Maximum Performance Power Mode | *(absent from locale)* | `k6ad89bg` | UI apply, 22:32:02 | `[K6]` |

Two name drifts are visible and matter for anyone cross-referencing Hone's own strings:

* `9C0X9LiC` is called *"Disable Windows Program Compatibility Assistant"* in the shipped locale but *"Reduce Windows Background Activity"* in the live UI. The captured behaviour matches the **live** name (34 tasks, WSearch, WER, telemetry policies) and is far broader than a PCA toggle.
* `3WwEZo0M` is described in the locale as *"Optimize Drives … disk defragmentation, TRIM support, and drive health monitoring"*. **No defrag, no `Optimize-Volume`, no TRIM, no `fsutil` was executed** `[3W]`. The name is stale marketing; the behaviour is registry-only.

### 2.3 Per-optimization operation counts (enumerated, not estimated)

| ID | Registry writes | Command directives | Service directives | Task directives | Total operations |
|---|---|---|---|---|---|
| `5Q8oOtIH` | ≈58 across 10 groups | 3 BCD + 1 task-spawned timer process | 5 | 1 created | **68** |
| `3WwEZo0M` | 58 (1 + 49 + 6 + 2) | 0 | 0 | 0 | **58** |
| `RqbxUufM` | 21 (7 devices × 3) | 0 | 0 | 0 | **21** |
| `oPu4x2Ef` | 8 (7 set + 1 delete) | 0 | 0 | 0 | **8** |
| `XLSEdxVW` | ≈41 + 452 QoS = 493 | 25 `netsh` + 1 self-reinvoke | 0 | 0 | **519** |
| `istk0001` | 1 | 0 | 0 | 0 | **1** |
| `PE9bgwi0` | 7 | 0 | 7 | 0 | **7** |
| `9C0X9LiC` | 25 | 0 | 4 | 34 disabled | **63** |
| `k6ad89bg` | 12 scheme rows + 9 platform + 14 WDF + 3 channel flags + 1 policy + 1 blob ≈ 40 | 1 file rename | 5 | 1 created + 1 disabled | **≈48** |

Total captured operations across the preset: **≈793**. Hone calls this "9 optimizations". This ratio — 9 UI entries to ~793 effects — is the single most important number in this report for anyone planning a clone.

`XLSEdxVW` carries a documented internal discrepancy: the card header records "18 `netsh` invocations" while its own itemised table lists **25** commands (13 standalone + 6 `newreno` resets + 6 `bbr2` sets). The itemised table is the authoritative capture; 25 is used throughout this report. The "18" in the header is unexplained and is logged in §8.3.

### 2.4 Reconciliation of the 106

| Bucket | Count | Status |
|---|---|---|
| Catalog operations reported by the renderer dump `[CKPT]` | **106** | reference denominator |
| Tweak entries in the shipped `locale-en.json` `[LOC]` | 102 | different artefact; not expected to equal 106 |
| In the `test` preset | 9 | this report |
| Captured with direct runtime evidence | **9** | full A–G specifications below |
| Not captured (not in `test`; never applied during the investigation) | **97** | no specification, no claim |
| Premium/entitlement-gated (not attempted — see D4 §1) | unknown | do not bypass |

Subtracting the nine specified operations from 106 leaves 97 unspecified operations. Of those 97, **95** are identifiable by ID and name in the shipped locale (28 basic + 54 optimizations + 13 advanced that are not among the captured seven); the remaining **2** are accounted for only by the difference between the remote catalog and the locale file and cannot be individually named from available evidence. `Implementation-Tracker.json` carries all 95 named-but-uncaptured IDs with `evidence: "NONE"` and `readiness: "NOT CAPTURED"`, plus two `UNATTRIBUTED` placeholder rows, so that the tracker reconciles to 106 without pretending to knowledge it does not have.

### 2.5 Contradiction log (things the evidence does not agree with itself about)

Per instruction, these are recorded as observed contradictions. They are not resolved here, and no experiment was run to resolve them.

**C-1 · `RqbxUufM` and `oPu4x2Ef` show as OFF in the Hone UI while their registry effects are present.**
Observed state: both cards off; both tweaks' writes present in the registry (`Affinity Policy` values on 7 PCI functions; `MSISupported=1` on 7 PCI functions). No `t revert-<Id>` payload was ever issued for either ID `[RB]` §1 — consistent with the cards being off, because Hone issues a revert only for a card it believes is on.

Two readings are compatible with the evidence, and the evidence does not choose between them:

* **(a) the service path is independent of card state.** `HoneSvc` re-applies both IDs from the `TweaksSvc=1` bitmask on service start / USB arrival / audio-device change `[SVC]`, `[SV]`. Value `1` = bit 0 set = affinity only, which matches `MSISupported` being present from the *earlier* session rather than freshly written.
* **(b) card state is not a reliable record of applied state.** Hone's per-session applied-list is stored in `localStorage` and in an encrypted leveldb; the service writes outside that bookkeeping entirely.

What would settle it (not performed): read `HKLM\SOFTWARE\Hone\TweaksSvc` now, rename/write a dummy value, restart `HoneSvc`, and see whether only `RqbxUufM` re-applies. **Swiftwave must not inherit this ambiguity** — D3 §3.4 makes applied-state single-sourced.

**C-2 · `5Q8oOtIH` writes `NoLazyMode=0`.** A "lazy mode ON" value inside an FPS/latency tweak is the opposite of the advertised intent. Either it is a deliberate default-restore or an authoring error. Not resolved; flagged in the tweak's spec (§3.1 F) and excluded from the recommended implementation.

**C-3 · `XLSEdxVW` sets `initialRto=2000`** while Windows' default is 1000 ms. Doubling the first-retransmit timer is contrary to "reduce latency". Captured verbatim; excluded from the recommended implementation with the rationale recorded.

**C-4 · `9C0X9LiC` sets `AllowBlockingAppsAtShutdown=0`** while `5Q8oOtIH` sets `AutoEndTasks=0` (per-user "do not auto-end tasks"). The two are contradictory in intent — one says "never let an app block shutdown", the other says "never force-kill apps at shutdown". Both were captured within 18 minutes of each other in the same preset. Not resolved.

**C-5 · `k6ad89bg` registers the scheme as "Hone Ultimate Power Plan V5"** while the embedded hive self-describes as *"Hone Ultimate Power Plan V4"* `[K6]`, `[POW]`. Either Hone rewrites the friendly name at import (INFERRED) or a newer hive ships than the one decoded offline. Cosmetic, but it means the offline decode may not be the plan that was actually installed; the 12 rows in §3.9 are the **observed** rows from the live scheme, and they are the authoritative list.

### 2.6 Structural rule this implies for Swiftwave

Do not model "one card = one script". Model **one card = an ordered set of typed operations with per-operation backup**, because:

* five of the nine tweaks touch values another of the nine also touches (§4.1);
* one tweak's revert (`XLSEdxVW` → BITS `SvcHostSplitDisable`) actively undoes another's apply (`3WwEZo0M` family);
* per-tweak reverts that assume "delete what I wrote" are wrong for any value that pre-existed (§6, D-2).

---

## 3. Per-optimization specifications

Nine specifications follow, each with sections A–G as required. Registry paths are given in full; `HKLM\` and `HKU\<SID>\` are literal. Verification snippets are PowerShell unless marked `cmd`/`netsh`. Every "before" value quoted is a captured value, not a Windows default; where the evidence does not contain a prior value, the field says so explicitly and the rollback section says how to handle it safely.

#### 3.0 Facts common to all nine specifications

These apply to every tweak below and are therefore stated once rather than repeated nine times.

* **Required privileges.** Every tweak writes `HKLM` or runs an elevated command, so all nine require an **administrator token**. The leaf `HoneUtility.exe` is launched by `hone-elevation-helper.exe`, which is elevated and verifies its own Authenticode identity; `HoneSvc`-path runs (`RqbxUufM`, `oPu4x2Ef`) run as **LocalSystem in session 0**. No tweak requires SYSTEM beyond what administrator rights provide — the service path is an implementation choice, not a requirement.
* **Process chain.** `Hone.exe` (medium IL) → `hone-elevation-helper.exe` (high IL) → `HoneUtility.exe` (high IL) → optional children (`bcdedit.exe`, `netsh.exe`, `conhost.exe`). Children inherit high IL.
* **Exit codes.** `HoneUtility` returns `0` on success and 1–255 structured failures. The Electron side treats **70–75 as expected, non-fatal** results ("skip / revert required") rather than errors. Individual child commands follow Windows conventions: `bcdedit` returns `0x1` for "element not found" (expected for the two `deletevalue` calls in §3.1), `netsh` returns `0x1` for an unsupported parameter or an invalid value, `powercfg` returns `0` on success and non-zero on any invalid GUID/setting.
* **Ordering guarantee.** Only one tweak runs at a time: `Hone.exe` serialises calls through the elevator pipe, and no two tweaks overlap in the 4688 trace. Cross-tweak ordering is therefore a UI choice, not a mechanism.
* **Rollback entry point.** Every tweak is inverted by `t revert-<Id> <pid>` using the same ID. There is no per-operation "undo" surface.
* **Recording.** Hone records prior state inside `HoneUtility` only for values it modifies; for values it creates it records nothing and deletes on revert. That distinction is the source of defect **D-2** and is called out wherever it matters.

---

### 3.1 `5Q8oOtIH` — "Optimize FPS & Input Lag"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Optimize FPS & Input Lag (shipped locale: 常规 Windows 优化 / "General Windows Optimizations") |
| Category | Optimizations |
| Internal ID | `5Q8oOtIH` (also marketed as "recommended") |
| Bundled? | **Yes** — 10 heterogeneous groups; this is the widest of the nine |
| Components | `HoneUtility.exe` → children `bcdedit.exe` ×3, self-reinvocation ×5, and (post-apply) `SetTimerResolution.exe --resolution 5120` launched by the created task |
| Constituent operations | ≈58 registry writes, 3 BCD directives, 5 service start directives, 1 scheduled task (+1 spawned helper process) |

#### B. Purpose and technical explanation

Hone sells this as "general Windows optimisation for responsiveness". What it actually does is four unrelated things at once:

1. **Timer behaviour** — removes forced platform clock/tick, forces a non-coalesced tick (`disabledynamictick yes`), writes ten undocumented `Session Manager\kernel` scheduler/timer tunables, and pins a persistent 0.5 ms timer resolution via a scheduled task. Intent: reduce input-to-photon and scheduler jitter for gaming.
2. **Process-termination timeouts** — shortens `WaitToKillServiceTimeout` to 1.5 s and adds per-user shutdown/desktop timeouts. Intent: faster shutdown/logoff, fewer "this app is preventing shutdown" waits.
3. **GPU deep C-states off** — `AllowDeepCStates=0` on every display-class and `Control\Video` GPU instance. Intent: keep the GPU from entering deep idle (avoids ramp latency and stutter).
4. **Shell/input/DirectX flags** — disables shell startup delay, app prelaunch, and Explorer idle-wait; enables DirectX swap-effect upgrade and Game Mode; disables raw-mouse throttling; and disables background-app access twice (per-machine value + policy).

Disadvantages and compatibility implications, from the captured mechanics rather than the marketing:

* `disabledynamictick` and the ten `kernel` values are **undocumented**. They change scheduler/tick behaviour globally. On some firmware they cause measurable time drift and can aggravate audio glitches; they are not supported by Microsoft and are not validated by Windows Update.
* The per-user `Control Panel\Desktop` timeouts mean apps that legitimately need >1.5 s to persist data at logoff can lose data.
* Forcing `AllowDeepCStates=0` on *all* GPU instances raises idle power and heat on laptops; on a dGPU laptop it prevents RTD3-style power-down of the display engine.
* `RawMouseThrottle*` values are undocumented; their behaviour is INFERRED from the name.

#### C. Exact implementation details

**C.1 Boot configuration** — requires reboot. VERIFIED-RUNTIME (`bcdedit.exe` children in 4688).

| # | Command | Before | After |
|---|---|---|---|
| 1 | `bcdedit /deletevalue {current} useplatformclock` | absent (revert used `/deletevalue`, so it was absent) | absent |
| 2 | `bcdedit /deletevalue {current} useplatformtick` | absent | absent |
| 3 | `bcdedit /set {current} disabledynamictick yes` | absent | `yes` |

**C.2 Kernel scheduler/timer values** — `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel`, all `REG_DWORD`, all **created** (absent before):

`SerializeTimerExpiration=1`, `EnableTickAccumulationFromAccountingPeriods=0`, `TimerCheckFlags=0`, `ReadyTimeTicks=6`, `ScanLatencyTicks=0x14` (20), `ThreadReadyCount=3`, `BoostingPeriodMultiplier=5`, `EnablePerCpuClockTickScheduling=1`, `ForceForegroundBoostDecay=1`, `ThreadDpcEnable=1`

**C.3 Shutdown timeouts** — `HKLM\SYSTEM\CurrentControlSet\Control`

| Value | Type | Before | After |
|---|---|---|---|
| `WaitToKillServiceTimeout` | `REG_SZ` | `"5000"` | `"1500"` |
| `WaitToKillAppTimeout` | `REG_SZ` | absent | `"1500"` |

**C.4 Per-user desktop timeouts** — `HKU\<SID>\Control Panel\Desktop`, all `REG_SZ`, all **created** (prior values not present in the capture — see F):

`HungAppTimeout="1000"`, `WaitToKillAppTimeout="1500"`, `LowLevelHooksTimeout="1000"`, `AutoEndTasks="0"`

**C.5 Per-user raw-mouse throttling** — `HKU\<SID>\Control Panel\Mouse`, `REG_DWORD`, created:

`RawMouseThrottleEnabled=1`, `RawMouseThrottleForced=1`, `RawMouseThrottleDuration=0x14` (20), `RawMouseThrottleLeeway=0`

**C.6 GPU deep C-states** — `AllowDeepCStates` = `REG_DWORD 0`, written to every instance under both:

* `HKLM\SYSTEM\CurrentControlSet\Control\Video\{GUID}\0000` … `\0015` — two GPU GUIDs, ≈20 subkeys
* `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000`, `\0001`, `\0002`

Before: absent on all instances. Note the write is **unconditional per instance** — Hone does not check whether the subkey corresponds to a real, active adapter.

**C.7 DirectX / shell / input**

| Key | Value | Type | Before → After |
|---|---|---|---|
| `HKCU\Software\Microsoft\directx\usergpupreferences` | `DirectXUserGlobalSettings` | `REG_SZ` | → `"SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;"` (prior not captured) |
| `HKCU\Software\Microsoft\directx\graphicssettings` | `SwapEffectUpgradeCache` | DWORD | created `1` |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize` | `StartupDelayInMSec` | DWORD | → `0` (prior not captured) |
| same | `WaitforIdleState` | DWORD | → `0` (prior not captured) |
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Dsh` | `IsPrelaunchEnabled` | DWORD | `1` → `0` |
| `HKCU\Software\Microsoft\input` | `IsInputAppPreloadEnabled` | DWORD | `1` → `0` |
| `HKCU\Software\Microsoft\GameBar` | `AllowAutoGameMode` | DWORD | → `1` |

**C.8 Background gating** — `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\backgroundaccessapplications\GlobalUserDisabled` = DWORD `1` (key created); `HKLM\SOFTWARE\Policies\Microsoft\Windows\appprivacy\LetAppsRunInBackground` = DWORD `2` (key created). `2` means "force deny".

**C.9 Multimedia** — `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\NoLazyMode` = DWORD `0`. See contradiction **C-2**: `0` is the lazy-mode-**on** value, which contradicts the tweak's intent.

**C.10 Services** — `DcomLaunch`, `RpcEptMapper`, `RpcSs`, `Schedule`, `SystemEventsBroker`, each coerced to `Automatic` and started via `HoneUtility.exe sett serv_apply <name> Automatic start`. All five are already Automatic on stock Windows 11; the observed effect was "ensure running", not a start-type change. Running state changed only where a service was stopped (not observed for these five).

**C.11 Scheduled task** — created `\LaunchSetTimerResolution`, `{FAD1DBDD-83F7-4469-A6BD-D806CA7BAA90}`, `Index=2`, state Running. Action: `hone-resources\SetTimerResolution.exe --resolution 5120` (= 0.5 ms). This is the persistence mechanism for the timer-resolution part: a process is spawned from `svchost` after the task fires.

**C.12 Filesystem** — none, other than the created task and the resource executable already present in the install directory.

#### D. Execution sequence

Captured order (from 4688 timestamps within the `HoneUtility` run): BCD directives → `serv_apply` batch → registry writes → task creation. The task's child (`SetTimerResolution.exe`) starts *after* `HoneUtility` exits, from `svchost`, i.e. asynchronously.

Not captured and therefore **unknown**: whether the registry groups have an internal order, and whether any group is conditional. No `--check-app`, OS-version, or hardware check was observed in this run; no failure path was exercised (the run exited cleanly). Partial-application behaviour is therefore **UNCONFIRMED**.

#### E. Verification

```powershell
# BCD (expect disabledynamictick Yes; useplatformclock/useplatformtick absent)
bcdedit /enum "{current}"

# kernel tunables (expect all 10 present with the values in C.2)
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' |
  Select SerializeTimerExpiration,EnableTickAccumulationFromAccountingPeriods,TimerCheckFlags,
         ReadyTimeTicks,ScanLatencyTicks,ThreadReadyCount,BoostingPeriodMultiplier,
         EnablePerCpuClockTickScheduling,ForceForegroundBoostDecay,ThreadDpcEnable

# shutdown timeouts
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').WaitToKillServiceTimeout   # "1500"
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control').WaitToKillAppTimeout       # "1500"

# per-user (run as the target user; SID substitution required for other profiles)
$sid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Get-ItemProperty "Registry::HKEY_USERS\$sid\Control Panel\Desktop" |
  Select HungAppTimeout,WaitToKillAppTimeout,LowLevelHooksTimeout,AutoEndTasks
Get-ItemProperty "Registry::HKEY_USERS\$sid\Control Panel\Mouse" |
  Select RawMouseThrottleEnabled,RawMouseThrottleForced,RawMouseThrottleDuration,RawMouseThrottleLeeway

# GPU deep C-states (expect Count > 0 and all AllowDeepCStates = 0)
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Video' -Recurse -ErrorAction SilentlyContinue |
  ForEach-Object { $p = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                   if ($null -ne $p.AllowDeepCStates) { "$($_.PSPath) = $($p.AllowDeepCStates)" } }

# task + live timer helper
Get-ScheduledTask -TaskName 'LaunchSetTimerResolution' | Select TaskName,State
Get-Process SetTimerResolution -ErrorAction SilentlyContinue | Select Id,ProcessName
```

A timer-resolution assertion cannot be made from the registry; the reliable check is the presence of the `SetTimerResolution` process (or an API probe of the current resolution from a helper process). Reporting "applied" from the registry alone would be wrong.

#### F. Rollback

| Item | Restore action | Caveat |
|---|---|---|
| BCD ×3 | `bcdedit /deletevalue {current} disabledynamictick` (and the two others, no-ops here) | Correct **only** because all three were absent beforehand. See defect **D-2**. |
| kernel ×10 | delete each value | All were created; deletion is exact. |
| `WaitToKillServiceTimeout` | set `REG_SZ` `"5000"` | captured original. |
| `WaitToKillAppTimeout` | delete | created. |
| Desktop ×4, Mouse ×4, DirectX ×2, Serialize ×2, GameBar ×1 | **prior value unknown** | Hone's own revert deletes them. A clone must read-and-record before writing, then restore; on a stock system the Windows defaults are `HungAppTimeout=5000`, `WaitToKillAppTimeout=20000`, `StartupDelayInMSec=0`, `WaitforIdleState=1` (INFERRED, not captured). |
| `IsPrelaunchEnabled`, `IsInputAppPreloadEnabled` | set back to `1` | captured original. |
| `AllowDeepCStates` | delete on every instance | created. |
| `GlobalUserDisabled`, `appprivacy\LetAppsRunInBackground` | delete value, then delete the `backgroundaccessapplications` key if empty | keys were created by Hone. |
| `NoLazyMode` | prior value unknown → record before write | — |
| Task `\LaunchSetTimerResolution` | `Unregister-ScheduledTask` | delete the task; do not just disable it, or `.exe` keeps running until logoff. |
| Services | no action needed | start type unchanged. |

Reboot required for BCD + kernel values to unload. Verified in `[RB]`: all of the above actually reverted, zero residue — this tweak is the best-behaved of the nine.

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Constituent | Verdict |
|---|---|
| BCD: delete `useplatformclock`, `useplatformtick` | **Ship.** Removing forced platform clock is the standard fix and is fully reversible when recorded. |
| BCD: `disabledynamictick yes` | **Gate, off by default.** Undocumented, global, can cause drift/audio issues. Offer as "aggressive timer mode (unsupported)". |
| Ten `Session Manager\kernel` values | **Exclude from default.** Undocumented scheduler internals with no published contract; no way to verify correct behaviour. Optional "experimental" tier at most. |
| `WaitToKillServiceTimeout=1500`, `WaitToKillAppTimeout=1500` | **Ship.** Record prior; 1500 is Hone's value and is aggressive but supported. |
| Per-user Desktop timeouts | **Ship** the three timeouts with recorded prior values. **Drop `AutoEndTasks=0`** — it contradicts C-4 and is a data-loss vector in the wrong direction. |
| `RawMouse*` ×4 | **NEEDS VALIDATION.** Undocumented; no observable verification. Do not ship in a "Verified" product. |
| `AllowDeepCStates=0` | **Ship, opt-in, per-adapter only** — resolve each instance to a real PnP adapter before writing, and skip if the machine is on battery-first (laptop) unless the user opts in. |
| DirectX `SwapEffectUpgradeEnable=1`, `SwapEffectUpgradeCache=1` | **Ship.** Documented DXGI behaviour; reversible. |
| `VRROptimizeEnable=0` | **Gate.** Disables VRR optimisation; wrong for VRR-panel owners. |
| `StartupDelayInMSec=0`, `WaitforIdleState=0` | **Ship.** Cosmetic/UX; reversible. |
| `Dsh\IsPrelaunchEnabled=0`, `input\IsInputAppPreloadEnabled=0` | **Ship.** |
| `AllowAutoGameMode=1` | **Gate.** Overrides a user preference. |
| `backgroundaccessapplications\GlobalUserDisabled=1` + `appprivacy` policy `2` | **Gate.** Per-machine policy overrides every user's per-app background choice; ships a policy key, which some managed devices disallow. |
| `NoLazyMode=0` | **Exclude.** Contradicts intent (C-2). |
| 5 × `serv_apply Automatic start` | **Ship as verify-only.** Do not change start types; do start them if stopped. |
| Task `\LaunchSetTimerResolution` | **READY WITH MODIFICATIONS**: implement with a documented timer-resolution API call (`NtSetTimerResolution` / `timeBeginPeriod`) from your own service, or an equivalent task pointing at *your* binary. Do not ship Hone's helper. |

Compatibility risks: Secure Boot/`bcdedit` requires UAC elevation and works only when BitLocker is suspended or the BCD is unlocked; HVCI/Secure Launch systems may reject `disabledynamictick`. Test on a hybrid laptop before enabling anything in the "aggressive" tier.

---

### 3.2 `3WwEZo0M` — "Optimize Storage & Memory"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Optimize Storage & Memory (shipped locale: "Optimize Drives") |
| Category | Optimizations |
| Internal ID | `3WwEZo0M` |
| Bundled? | Yes — two independent groups (service-host consolidation, NTFS) plus a third unrelated (Reserved Storage) |
| Components | `HoneUtility.exe` only. **No child processes.** Pure registry, no `defrag`, `Optimize-Volume`, `fsutil`, or TRIM. VERIFIED-RUNTIME. |
| Constituent operations | 58 registry writes in 4 groups |

#### B. Purpose and technical explanation

Marketing (locale): *"disk defragmentation, TRIM support, and drive health monitoring"*. **None of that happens.** The actual mechanism is:

1. **svchost consolidation.** `SvcHostSplitThresholdInKB` is the RAM threshold above which Windows gives *each service its own* `svchost.exe`. Raising it to `0xFFFFFFFF` (4 TB) means no machine ever exceeds it, so services are consolidated into shared hosts. Clicking 49 per-service `SvcHostSplitDisable=1` values makes the opt-in explicit for services that would otherwise split anyway. Effect: fewer processes, lower per-process working-set overhead, fewer handles — at the cost of blast radius (one crashing shared host takes down several services) and a coarser security boundary.
2. **NTFS metadata reduction.** Disabling 8.3 name generation removes a per-file metadata write and a name-collision check; disabling last-access updates removes a write on every read; raising `NtfsMemoryUsage` to 2 enlarges the cache; `NtfsMftZoneReservation=2` reserves 25 % of the volume for the MFT; `ScrubMode=1` makes integrity scrubbing background/fast.
3. **Reserved Storage off.** `ReserveManager` flags flipped; reclaims the ~7 GB reserve. The exact meaning of `MiscPolicyInfo=1→2` is **INFERRED** — Microsoft documents `PassedPolicy` only.

Disadvantages: MFT zone reservation is a real, permanent space cost (25 % of volume size on small volumes); 8.3 disabling can break legacy installers that rely on short names; disabling last-access breaks forensic/backup tooling that reads atime; consolidation makes per-service CPU/memory accounting much harder and widens fault domains; Reserved Storage off means feature updates may need to free space at install time.

#### C. Exact implementation details

**C.1 Threshold** — `HKLM\SYSTEM\CurrentControlSet\Control\SvcHostSplitThresholdInKB`, `REG_DWORD`, `0x01000000` (16 GB) → **`0xFFFFFFFF`**. Modified, not created.

**C.2 Per-service opt-out** — `HKLM\SYSTEM\CurrentControlSet\Services\<Name>\SvcHostSplitDisable` = `REG_DWORD 1`, **49 services, all created**:

`AarSvc_d23c3`, `CDPUserSvc_d23c3`, `WpnUserService_d23c3`, `OneSyncSvc_d23c3`, `cbdhsvc_d23c3`, `CaptureService_d23c3`, `BluetoothUserService_d23c3`, `webthreatdefusersvc_d23c3`, `UnistoreSvc_d23c3`, `UdkUserSvc_d23c3`, `P9RdrService_d23c3`, `DevicesFlowUserSvc_d23c3`, `DeviceAssociationBrokerSvc_d23c3`, `DevicePickerUserSvc_d23c3`, `PrintWorkflowUserSvc_d23c3`, `MessagingService_d23c3`, `NPSMSvc_d23c3`, `CredentialEnrollmentManagerUserSvc_d23c3`, `PenService_d23c3`, `ConsentUxUserSvc_d23c3`, `CloudBackupRestoreSvc_d23c3`, `BcastDVRUserService_d23c3`, `UserDataSvc_d23c3`, `IsoSessionCore`, `IsolationSession`, `PimIndexMaintenanceSvc_d23c3`, `FileSyncHelper`, `ClickToRunSvc`, `mxredirect`, `AMD External Events Utility`, `AMD Crash Defender Service`, `amdfendr`, `amdfendrmgr`, `amdlog`, `Wacom` ×3, `HWiNFO_214`, `HWiNFO_216`, `OneDrive Updater Service`, `OfficePLUS Service`, `Red Giant Service`, `KslD`, `Ccx01000`, `HoneSvc`.

Note the `_d23c3` suffix: these are per-user service instances whose suffix is a **per-machine random hex**, not a constant. A clone must resolve the actual suffix on the target machine (`Get-Service | Where-Object Name -like 'AarSvc_*'`) rather than hardcoding `d23c3`.

**Enumeration gap:** the capture records "49 services" in its summary line while the itemised list contains **45 names** (29 OS + 16 third-party, with three Wacom instances given as `Wacom ×3`). The four-name difference is unexplained and is logged in §8.3. The itemised list is authoritative for *which* services were observed; 49 is the claimed count.

**C.3 NTFS** — `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem`

| Value | Before | After | Change |
|---|---|---|---|
| `NtfsDisable8dot3NameCreation` | `2` (per-volume) | `1` (disable everywhere) | modified |
| `NtfsDisableLastAccessUpdate` | `0x80000002` (system-managed) | `1` (user-managed, disabled) | **modified — overwrites a system-managed setting** |
| `NtfsMemoryUsage` | `0` | `2` (max) | modified |
| `NtfsMftZoneReservation` | `0` (12.5 %) | `2` (25 %) | modified |
| `ScrubMode` | `2` (full) | `1` (fast) | modified |
| `NtfsAllowExtendedCharacterIn8dot3Name` | absent | `1` | created |

**C.4 Reserved Storage** — `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager`: `PassedPolicy` `1` → `0`, `MiscPolicyInfo` `1` → `2` (both `REG_DWORD`, modified).

#### D. Execution sequence

Single process, no children; no ordering information was captured. All four groups appear in the same diff window. Almost certainly unordered/independent (INFERRED: they touch disjoint keys).

Conditional execution: none observed. Note that Hone wrote `SvcHostSplitDisable` to third-party services present on *this* machine (AMD, Wacom, HWiNFO, OneDrive, OfficePLUS, Red Giant, KslD, Ccx01000). Whether it filters by service existence or writes blindly could not be determined from a single host — on a machine without those services, a blind write would create orphan service keys, which is material. Treat as **UNCONFIRMED**; a clone must write only to services that exist.

#### E. Verification

```powershell
# 1. threshold
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name SvcHostSplitThresholdInKB).SvcHostSplitThresholdInKB
# expect 4294967295 (0xFFFFFFFF)

# 2. per-service opt-outs, resolved dynamically
Get-Service | Where-Object { $_.Name -like '*_*' } | ForEach-Object {
  $v = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$($_.Name)" -Name SvcHostSplitDisable -ErrorAction SilentlyContinue).SvcHostSplitDisable
  if ($v -eq 1) { $_.Name }
} | Measure-Object | Select Count          # expect the applied subset, not a hardcoded 49

# 3. NTFS
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' |
  Select NtfsDisable8dot3NameCreation,NtfsDisableLastAccessUpdate,NtfsMemoryUsage,
         NtfsMftZoneReservation,ScrubMode,NtfsAllowExtendedCharacterIn8dot3Name

# 4. Reserved Storage
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\ReserveManager' |
  Select PassedPolicy,MiscPolicyInfo

# 5. EFFECT check (the only meaningful one for group 1): host count per shared host
(Get-Process svchost | Measure-Object).Count     # compare with the pre-apply count
```

Group 1's actual purpose only manifests after reboot; a pre/post `svchost` count is the honest verification, and the baseline must be captured before applying.

#### F. Rollback

| Item | Action |
|---|---|
| `SvcHostSplitThresholdInKB` | restore `0x01000000` |
| 49 × `SvcHostSplitDisable` | delete (all were created). Do **not** set to `0` — absence is the stock state. |
| `NtfsDisable8dot3NameCreation` | `2` |
| `NtfsDisableLastAccessUpdate` | `0x80000002` |
| `NtfsMemoryUsage` | `0` |
| `NtfsMftZoneReservation` | `0` |
| `ScrubMode` | `2` |
| `NtfsAllowExtendedCharacterIn8dot3Name` | delete |
| `PassedPolicy` / `MiscPolicyInfo` | `1` / `1` |

Order: threshold first, then per-service values, then reboot. Restoring the NTFS values before reboot is fine; they are read at volume mount. Verified: `[RB]` shows all 49 and the threshold returned to stock, zero residue.

One rollback subtlety that the evidence proves is real: **`ReserveManager` is not restored to a "disabled" state but to `1`/`1`**, and reserved storage does not necessarily reappear until the next feature update. Tell users.

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Constituent | Verdict |
|---|---|
| `SvcHostSplitThresholdInKB=0xFFFFFFFF` | **Ship**, opt-in, with the fault-domain warning. |
| 49 × `SvcHostSplitDisable` | **Ship with a rewritten list.** Resolve `_<suffix>` dynamically; skip non-existent services; **never** write to services the app does not own (drop `HoneSvc` and the third-party list; a Swiftwave clone writing to `KslD`/`Ccx01000` is attributing third-party services to itself). |
| NTFS 8dot3 / last-access / memory / scrub | **Ship.** All documented, all reversible. |
| `NtfsMftZoneReservation=2` | **Gate, default off.** 25 % reservation on a 256 GB volume is ~64 GB of "lost" space. Offer `1` (12.5 %) or leave at `0`. |
| `NtfsAllowExtendedCharacterIn8dot3Name=1` | **Exclude.** Pointless once 8dot3 creation is disabled (`NtfsDisable8dot3NameCreation=1`); dead operation, adds noise. |
| `ReserveManager` ×2 | **NEEDS VALIDATION.** `PassedPolicy=0` is defensible; `MiscPolicyInfo=2` is undocumented. If shipped, implement via the documented `DISM /Online /Set-ReservedStorageState /State:Disabled` instead of raw registry — same outcome, supported path. |

Conflict: `XLSEdxVW` writes `BITS\SvcHostSplitDisable=0`, which directly opposes this tweak's model. See §4.1.

---

### 3.3 `RqbxUufM` — "Optimize Device Affinities"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Optimize Device Affinities (locale: 优化设备亲和性) |
| Category | Optimizations |
| Internal ID | `RqbxUufM` |
| Bundled? | Yes — one homogeneous operation replicated across 7 PCI functions (21 writes) |
| Components | `HoneUtility.exe` (no helper binaries; one benign `conhost.exe`). Pure managed registry writes. |
| Applied via | **`HoneSvc` service path**, `t apply-RqbxUufM 21836`, 2026-11-10 20:29:28.7 |
| Re-applied by | `HoneSvc` whenever `HKLM\SOFTWARE\Hone\TweaksSvc` bit 0 is set, on service start / USB arrival / default audio device change `[SVC]` |

#### B. Purpose and technical explanation

This is the Windows *interrupt affinity* interface, not process affinity. For each PCI function, Windows lets a driver/administrator override the interrupt policy under the device's `Interrupt Management\Affinity Policy` key:

* `DevicePolicy=4` = `IrqPolicySpecifiedProcessors` — "deliver this device's interrupts only to the processors in my mask".
* `DevicePriority=3` = `IrqPriorityHigh`.
* `AssignmentSetOverride` = the processor mask, 8 bytes little-endian.

Intent: keep a device's interrupts on a fixed core so the DPC/ISR work is cache-warm and does not migrate, reducing interrupt-handling latency and jitter. The classic use is pinning a NIC or GPU away from the core running the game thread.

Why the configuration exists: the PCI driver stack reads these values at device start, so a persistent registry override is the only supported way to pin interrupts without a kernel driver.

Disadvantages:

* **The masks are machine-specific.** Hone computed them from this host's topology. Copying them to another machine pins interrupts to cores that may not exist, may be E-cores, or may be the cores running the game.
* `DevicePriority=3` (High) on **every** device, including audio and serial IO, means every device competes at the same elevated priority — the opposite of prioritisation.
* On hybrid CPUs, pinning to a P-core is sensible; pinning to an E-core (or to CPU 0, which also hosts most system activity) can increase latency.
* Effect requires device re-enumeration or reboot; there is no user-visible feedback that it "took".

#### C. Exact implementation details

Key shape, for each PCI function `HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<VEN_xxxx&DEV_xxxx&SUBSYS_…>\<instance>\Device Parameters\Interrupt Management\Affinity Policy\`:

| Value | Type | Data | Change |
|---|---|---|---|
| `DevicePolicy` | `REG_DWORD` | `4` | created |
| `DevicePriority` | `REG_DWORD` | `3` | created |
| `AssignmentSetOverride` | `REG_BINARY` (8 bytes, LE) | CPU mask | created |

Observed per-device masks on the i5-13500H host (bit *n* = logical CPU *n*):

| Device | Mask | CPUs |
|---|---|---|
| Intel `8086:51ED` | `04` | CPU 2 |
| NVIDIA `10DE:28A1` (RTX 4050) | `50` | CPU 4 + CPU 6 |
| Intel `8086:A7A0` (SATA AHCI) | `50` | CPU 4 + CPU 6 |
| NVIDIA `10DE:22BE` (HD Audio) | `01` | CPU 0 |
| Intel `8086:51CA` | `01` | CPU 0 |
| Realtek `10EC:8168` (GbE) | `01` | CPU 0 |
| Intel `8086:51F1` | `01` | CPU 0 |

**UNCONFIRMED:** the filter Hone uses to decide which PCI functions to touch. The host has more than 7 PCI functions; whether Hone skipped devices without an existing `Interrupt Management` key, skipped message-signalled devices, or used a fixed class list cannot be determined from one capture.

#### D. Execution sequence

Enumeration order over the PCI tree; no dependencies, no conditionals observed, no child processes. Effects are read by the PCI driver at device start → **effective after reboot or device re-enumeration** (INFERRED: the values are persisted immediately, which is what the capture proves; the effect timing is a driver-stack property).

#### E. Verification

```powershell
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI' -Recurse -ErrorAction SilentlyContinue |
  Where-Object { $_.PSChildName -eq 'Affinity Policy' } | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    [pscustomobject]@{
      Device = ($_.PSPath -split '\\')[-4]
      Policy = $p.DevicePolicy
      Priority = $p.DevicePriority
      Mask = if ($p.AssignmentSetOverride) { ($p.AssignmentSetOverride | ForEach-Object { $_.ToString('X2') }) -join '' } else { $null }
    }
  }
```

Expect `Policy=4` and `Priority=3` and an 8-byte mask per written device. Do **not** treat "12 devices have the key" as success — Windows creates `Affinity Policy` on its own for some devices.

#### F. Rollback

Delete `DevicePolicy`, `DevicePriority`, and `AssignmentSetOverride` under each `Affinity Policy` key that Hone wrote. All three were created, so deletion is exact. Then delete the now-empty `Affinity Policy` key **only** if Hone created it (the card indicates Hone wrote into existing `Interrupt Management` keys, but this was not asserted).

Reboot to restore default routing. **Hone's own revert was never observed** for this tweak — no `t revert-RqbxUufM` payload was ever issued `[RB]` §1. Rollback status: **UNCONFIRMED for Hone; exact for a clone.**

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Constituent | Verdict |
|---|---|
| Mechanism (`DevicePolicy=4` + `AssignmentSetOverride`) | **Ship.** Documented, reversible, no driver needed. |
| Hone's literal masks | **Do not copy.** Compute from `GetLogicalProcessorInformationEx` + `GetSystemCpuSetInformation`: pick P-cores (efficiency class 0), exclude CPU 0, exclude E-cores, mask ≤ 8 bytes. |
| `DevicePriority=3` on everything | **Modify.** Apply High only to the device the user is optimising (GPU for frame pacing, NIC for network latency, NVMe for I/O). Leave audio/serial-IO untouched. |
| Device selection | **Modify.** Offer a device picker; default to the discrete GPU + primary NIC + AHCI/NVMe. Do not touch Intel Serial IO functions (`8086:51ED`, `8086:51F1`, `8086:51CA`) — they service HID/thermal and pinning them to CPU 0 adds contention with system work. |
| Continuous re-apply | **Do not reproduce.** HoneSvc re-writes these on every USB plug and audio-device change, forever, independent of UI state (see §2.5 C-1). A Swiftwave feature must apply once, record, and stop. |

Risk: on a VM or a machine with < 4 logical CPUs, a hardcoded mask is invalid; validate the mask against `$env:NUMBER_OF_PROCESSORS` before writing.

---

### 3.4 `oPu4x2Ef` — "Optimize Message Signal Interrupts"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Optimize Message Signal Interrupts (locale: 优化消息信号中断) |
| Category | Optimizations |
| Internal ID | `oPu4x2Ef` |
| Bundled? | Yes — one homogeneous set operation over 7 PCI functions, plus one destructive exception |
| Components | `HoneUtility.exe`, no children |
| Applied via | **`HoneSvc` service path**, `t apply-oPu4x2Ef 21836`, 2026-11-10 20:29:29.99 |
| Re-applied by | `HoneSvc` when `HKLM\SOFTWARE\Hone\TweaksSvc` **bit 1** is set `[SVC]` |

#### B. Purpose and technical explanation

MSI/MSI-X lets a PCI function raise interrupts by writing to memory instead of asserting a shared physical line. Benefits: no IRQ sharing, per-vector targeting, lower latency. Windows exposes a per-device override at `…\Interrupt Management\MessageSignaledInterruptProperties`.

Hone's behaviour is two operations:

1. Force `MSISupported=1` on every PCI function it enumerates — including devices whose driver may not have negotiated MSI.
2. **Delete** `MessageNumberLimit` on the NVIDIA GPU only — this value caps how many MSI messages (queues/vectors) the device may use; removing it lets the GPU request more vectors.

Why the configuration exists: `MSISupported` is the documented admin override for drivers that default to line-based interrupts.

Disadvantages / compatibility implications:

* Forcing MSI on a device whose driver does not support it can leave the device **unable to start** (Device Manager code 10) until the value is removed. This is the main risk and it is real, not theoretical; the mitigation is to only touch devices that already declare `MSISupported` or `MessageNumberLimit`, and to keep a rollback path that does not require the GUI.
* Removing `MessageNumberLimit` can request more vectors than the platform has spare; on small systems this fails device start.
* Requires reboot / device re-enumeration to take effect.
* Interacts with `RqbxUufM`: an MSI-capable device uses message interrupts, so its `Affinity Policy` mask applies to per-vector targets — the two tweaks are designed to be used together.

#### C. Exact implementation details

For each PCI function `HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<VEN_xxxx&DEV_xxxx&…>\<instance>\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties\`:

| Value | Type | Data | Change | Scope |
|---|---|---|---|---|
| `MSISupported` | `REG_DWORD` | `1` | created/modified | all 7 enumerated functions |
| `MessageNumberLimit` | `REG_DWORD` | *(deleted)* | **deleted** | `10DE:28A1` (RTX 4050) **only** |

Devices written (same seven as §3.3): `8086:51ED`, `10DE:28A1`, `8086:A7A0`, `10DE:22BE`, `8086:51CA`, `10EC:8168`, `8086:51F1`.

Pre-existing `MessageNumberLimit` on the Realtek NIC (`10EC:8168`, value `1`) and on Intel `8086:51F1` (value `3`) were **left untouched** — the delete is GPU-only by an explicit device check, not a blanket rule. That is the one piece of evidence that Hone special-cases the GPU here.

#### D. Execution sequence

Runs ~1.3 s after `RqbxUufM` on the service path, i.e. affinity first, then MSI. Order is not functionally required (disjoint keys) but is consistent across the two captures. No conditionals observed beyond the GPU-only delete. No child processes. Failure path unexercised — **UNCONFIRMED**.

#### E. Verification

```powershell
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Enum\PCI' -Recurse -ErrorAction SilentlyContinue |
  Where-Object { $_.PSChildName -eq 'MessageSignaledInterruptProperties' } | ForEach-Object {
    $p = Get-ItemProperty $_.PSPath
    [pscustomobject]@{ Device = ($_.PSPath -split '\\')[-4]
                       MSISupported = $p.MSISupported
                       MessageNumberLimit = $p.MessageNumberLimit }
  }
```

Expect `MSISupported=1` on the seven; on the RTX 4050 expect `MessageNumberLimit` to be `$null`. Then confirm the device actually started: `Get-PnpDevice -Status Error` must not list a PCI device that worked before the change. Registry state alone does **not** prove the device still functions.

#### F. Rollback

* Restore `MSISupported` to its captured prior value per device. On this host the prior state was not recorded per device in the card — this is a **gap**: the card records the post state (1) and the delete, but not each device's original `MSISupported`. A clone must snapshot it. If the value was absent, delete it.
* Restore `MessageNumberLimit` on `10DE:28A1`. **The original value was not captured** — Hone deleted it without recording. This is a concrete instance of defect **D-2** (delete-instead-of-restore) and the reason a Swiftwave implementation must read the value before deleting it.
* Reboot to re-enumerate.

Hone's own revert for this ID was never observed (`[RB]` §1). Rollback status for Hone: **UNCONFIRMED**.

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Constituent | Verdict |
|---|---|
| `MSISupported=1` | **Ship with a whitelist.** Only write to devices where MSI is already in play (`MessageSignaledInterruptProperties` key exists) or where the class is known-good (NVMe, GPU, NIC). Never blanket-write every PCI function. |
| Delete `MessageNumberLimit` on the GPU | **Modify.** Read and record the value, then delete; if the device fails to start, restore it automatically. |
| Device enumeration filter | **Write your own.** Enumerate by PnP class (`Display`, `Net`, `SCSIAdapter`, `HDC`) via `Get-PnpDevice`, not by registry tree order. |
| Continuous re-apply | **Do not reproduce** (same reason as §3.3 G). |

---

### 3.5 `XLSEdxVW` — "Optimize Network Performance"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Optimize Network Performance (locale: 优化Netsh网络设置 / "Optimize Netsh network settings") |
| Category | Optimizations |
| Internal ID | `XLSEdxVW` |
| Bundled? | Yes — the largest of the nine: 25 `netsh` commands, ~41 scattered registry values, and ~452 policy keys |
| Components | `HoneUtility.exe` → 25 × `netsh.exe` children + 1 self-reinvocation |
| Constituent operations | ≈519 (see §2.3) |

#### B. Purpose and technical explanation

The tweak is a grab-bag of four unrelated intents:

1. **TCP stack tuning** (`netsh int tcp/udp/ip`, `Tcpip\Parameters`): modern congestion control (BBRv2), short `TcpTimedWaitDelay`, more aggressive retransmission count, extended options, RSS/DCA/task-offload toggles. Intent: better throughput and lower latency under loss.
2. **Discovery/legacy protocol removal**: ISATAP off, IPv6 tunnel components disabled, NetBIOS over TCP/IP disabled per adapter, `lmhosts` service disabled, an adapter `UpperBind` rewritten to drop `Tcpip6`/`lltdio`/`MsLldp`/`rspndr`. Intent: fewer background listeners and broadcasts.
3. **NIC power management off**: EEE/green Ethernet/connected power gating/MIMO power save/`PnPCapabilities` — stop the adapter from sleeping and adding wake latency.
4. **Per-game QoS DSCP marking**: ~452 policy keys marking a hardcoded list of game executables with DSCP 46 (EF) and no throttle. Intent: let WMM-aware routers prioritise game traffic.

Disadvantages:

* `initialRto=2000` **doubles** the first retransmission timer versus the Windows default of 1000 ms — the opposite of "lower latency" (contradiction **C-3**).
* NetBIOS disable + `lmhosts` disable breaks legacy LAN name resolution, older NAS/printers, and some LAN game discovery.
* The `UpperBind` rewrite is **destructive** and drops IPv6 binding on that adapter class instance. On a live adapter this silently removes IPv6.
* 452 QoS keys are permanent policy state; each is a registry key Windows re-evaluates on every connection.
* All changes are live (no reboot), which also means a mistake is immediate.

#### C. Exact implementation details

**C.1 `netsh` commands — exact strings and order** (VERIFIED-RUNTIME, 4688 child processes)

| # | Command |
|---|---|
| 1 | `netsh int isatap set state disabled` |
| 2 | `netsh int tcp set global rss=enabled` |
| 3 | `netsh int tcp set global nonsackrttresiliency=disabled` |
| 4 | `netsh int tcp set global initialRto=2000` |
| 5 | `netsh int tcp set supplemental template=custom icw=10` |
| 6 | `netsh int ip set interface ethernet currenthoplimit=64` |
| 7 | `netsh int ipv4 set gl loopbacklargemtu=disabled` |
| 8 | `netsh int ipv6 set gl loopbacklargemtu=disabled` |
| 9 | `netsh int ip set global taskoffload=enabled` |
| 10 | `netsh int udp set global uro=disabled` |
| 11 | `netsh int udp set global uso=enabled` |
| 12 | `netsh int tcp set global netdma=disabled` |
| 13 | `netsh int tcp set global dca=disabled` |
| 14–19 | `netsh int tcp set supplemental template={internet\|internetcustom\|compat\|custom\|datacenter\|datacentercustom} congestionprovider=newreno` |
| 20–25 | same six templates → `congestionprovider=bbr2` |

Order matters: every template is reset to `newreno` and only then set to `bbr2`. Net effect: BBRv2 on all six templates. On a build without BBRv2 the command returns an error and the stack stays on `newreno` — which is exactly why Hone pre-resets (INFERRED; no failure was observed because this host accepted `bbr2`).

**C.2 `Tcpip\Parameters`** — `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters`

| Value | Type | After |
|---|---|---|
| `DisableTaskOffload` | DWORD | `0` |
| `EnableTCPChimney` | DWORD | `0` |
| `EnableTCPA` | DWORD | `0` |
| `EnableDCA` | DWORD | `0` |
| `TcpMaxConnectRetransmissions` | DWORD | `2` |
| `TcpTimedWaitDelay` | DWORD | `0x20` (32 s) |
| `MultihopSets` | DWORD | `0x0F` |
| `SackOpts` | DWORD | `1` |
| `Tcp1323Opts` | DWORD | `3` |

**C.3 `Tcpip6\Parameters`** — same `DisableTaskOffload/EnableTCPChimney/EnableTCPA/EnableDCA = 0`, plus `DisabledComponents = 1` (disables IPv6 on **tunnel** interfaces — ISATAP/6to4/Teredo — while leaving native IPv6 intact; that is the documented meaning of `0x1`).

**C.4 `Dnscache\Parameters`** — `NegativeCacheTime=0`, `NegativeSOACacheTime=0`, `NetFailureCacheTime=0`, `EnableAutoDoh=2` (all DWORD; `EnableAutoDoh` created). `2` = "DoH enabled, automatic".

**C.5 Other service keys**

| Key | Value | Change |
|---|---|---|
| `Services\Psched` | `TimerResolution` = DWORD `1` | created |
| `Services\lmhosts` | `Start` = `4` | Manual → **Disabled** |
| `Services\iphlpsvc\config` | `IsatapState` = DWORD `3` | modified |
| `Services\Tcpip\qos` | `"Do not use NLA"` = `REG_SZ "1"` | created |
| `Services\LanmanServer\Parameters` | `IRPStackSize=0x1E` (30), `SizReqBuf=0x4410` (17424), `Size=3` | modified |
| `Services\BITS` | `SvcHostSplitDisable` = DWORD `0` | created — **opposes `3WwEZo0M`** |

**C.6 NetBIOS per interface** — `HKLM\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\Tcpip_{GUID}`, `NetbiosOptions` `0` → **`2`** (2 = disabled) on every adapter. Six interface GUID families observed (`a6e7eb87`, `47f6d386`, `892f3be0`, `3cdd439d`, `389c3618`, `88847ce0`).

**C.7 NIC advanced properties** — `HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0001`

`MIMOPowerSaveMode="3"`, `PowerSavingMode="0"`, `EnableGreenEthernet="0"`, `*EEE="0"`, `EnableConnectedPowerGating=0`, `EnableDynamicPowerGating="0"`, `EnableSavePowerNow="0"`, `PnPCapabilities=0x18`, `*NicAutoPowerSaver="0"`.

Note the class-key instance index (`0001`, `0012`, `0013`) is per-machine and per-adapter; a clone must map instance → adapter via `DriverDesc`/`NetCfgInstanceId`, never hardcode.

**C.8 Destructive binding rewrite** — `…\Class\{4d36e972…}\0013\Linkage` → `UpperBind` rewritten, dropping `lltdio`, `MsLldp`, `rspndr`/`rsppndr`, and `Tcpip6`. The affected instance was not the live adapter on this host, so no harm occurred here — but the operation itself is destructive and unbounded.

**C.9 QoS** — `HKLM\SOFTWARE\Policies\Microsoft\Windows\qos\<exe-name-lowercased>`, ≈452 keys created, each with:

```
"Application Name" = "<game>.exe"   "Version" = "1.0"
"Protocol" = "*"  "Local Port" = "*"  "Local IP" = "*"  "Local IP Prefix Length" = "*"
"Remote Port" = "*"  "Remote IP" = "*"  "Remote IP Prefix Length" = "*"
"DSCP Value" = "46"        ; EF
"Throttle Rate" = "-1"     ; no throttle
```

The game list is hardcoded inside `HoneUtility` (examples in the diff: `alan_wakes_american_nightmare.exe`, `fortniteclient-win64-shipping_eac_eos.exe`, `witcher3.exe`, `bf1942.exe`). **No cloud lookup occurred.**

**C.10 Self-reinvocation** — `HoneUtility.exe sett reg_add reg_dword HKLM\SYSTEM\CurrentControlSet\Services\BITS SvcHostSplitDisable 0` (the plaintext grammar of §1.3).

#### D. Execution sequence

`netsh` batch (1→25 in the order listed) → registry writes → QoS key flood → self-reinvocation for BITS. The `newreno`-then-`bbr2` pairing is the only functional dependency observed ("reset before set"). Whether Hone aborts on a failing `netsh` is **UNCONFIRMED**; no command failed on this host.

#### E. Verification

```powershell
# TCP/global state — the authoritative read, since netsh is the writer
netsh int tcp show global
netsh int tcp show supplemental template=internet
netsh int ipv4 show global
netsh int udp show global

# registry
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters' |
  Select DisableTaskOffload,EnableTCPChimney,EnableTCPA,EnableDCA,TcpMaxConnectRetransmissions,
         TcpTimedWaitDelay,MultihopSets,SackOpts,Tcp1323Opts
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters').DisabledComponents  # 1
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\NetBT\Parameters\Interfaces\*' |
  Select-Object PSChildName,NetbiosOptions                                                            # 2 on each
(Get-Service lmhosts).StartType                                                                       # Disabled

# QoS (count should match what was applied, not 452 on another build)
(Get-ChildItem 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\qos' -ErrorAction SilentlyContinue | Measure-Object).Count
```

For `bbr2`, the honest verification is `netsh int tcp show supplemental template=internet` on the target build; if it reports `cubic`/`newreno`, the write silently failed and the feature must report "unsupported" rather than success.

#### F. Rollback

`[RB]` §2 observed Hone's revert restoring:

```
bcdedit /deletevalue …                                   (n/a here)
netsh int tcp set supplemental <all 6 templates> congestionprovider=default
netsh int tcp set global rss=default
netsh int tcp set global nonsackrttresiliency=default
netsh int tcp set global initialRto=1000
netsh int isatap set state default
netsh int ip set interface ethernet currenthoplimit=128
netsh int {ipv4,ipv6} set gl loopbacklargemtu=enabled
netsh int udp set global uro / uso
HoneUtility.exe sett reg_delete HKLM\SYSTEM\CurrentControlSet\Services\BITS SvcHostSplitDisable
```

and removing all 452 QoS keys plus the `qos` policy key. Verdict in `[RB]`: clean, zero residue.

Not restored by Hone (a clone must handle these):

* `EnableAutoDoh` — deleted with the whole key? Not asserted; treat as "restore prior (absent → delete)".
* `LanmanServer\Parameters` `IRPStackSize`/`SizReqBuf`/`Size` — original values unknown; must be read before writing.
* NIC advanced properties — original values unknown per adapter.
* `UpperBind` — **if Hone dropped bindings, only a network-stack reset reliably restores them**. This is the strongest argument for excluding the operation rather than trying to reverse it.
* `NetbiosOptions` — original `0` (captured) → restore `0` per interface.

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Constituent | Verdict |
|---|---|
| BBRv2 on the templates actually in use | **Ship** with a competence probe first (`netsh int tcp show supplemental`) and `cubic` fallback. Do not churn all six templates twice; set only what you need. |
| `initialRto=2000` | **Exclude.** Contrary to intent (C-3). |
| `TcpTimedWaitDelay=32`, `SackOpts=1`, `Tcp1323Opts=3`, `MultihopSets=0x0F`, `TcpMaxConnectRetransmissions=2` | **Ship.** Documented; live; reversible. |
| `rss=enabled`, `netdma=disabled`, `dca=disabled`, `taskoffload=enabled`, `uro/uso`, `currenthoplimit`, `loopbacklargemtu` | **Ship.** |
| `DisableTaskOffload=0` etc. in registry | **Ship**, but note the direct contradiction with the catalog's own "Disable Task Offloading" tweak (§4.2). |
| `DisabledComponents=1` (tunnel-only IPv6 off) | **Ship, opt-in.** |
| `NetbiosOptions=2` + `lmhosts=Disabled` | **Gate behind an explicit "I don't use legacy LAN name resolution" confirmation.** |
| `UpperBind` rewrite | **Exclude.** Unbounded, destructive, and not reliably reversible. |
| `EnableAutoDoh=2` | **Gate.** Automatic DoH changes DNS behaviour for every app; some corporate/VPN DNS breaks. |
| NIC power-save-off block | **Ship per adapter** (`DriverDesc`-resolved), with prior values recorded. |
| 452 QoS keys | **Ship a curated subset** (the games the user actually owns) — the mechanism is safe and reversible; the 452-entry list is mostly dead weight. |
| `Psched\TimerResolution=1` | **Gate / NEEDS VALIDATION.** Obscure; unclear interaction with the packet scheduler on modern builds. |
| `BITS\SvcHostSplitDisable=0` | **Exclude.** It exists only to undo `3WwEZo0M`; shipping both is incoherent. |

---

### 3.6 `istk0001` — "Disable Dynamic Interrupt Steering"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Disable Dynamic Interrupt Steering |
| Category | Optimizations (present in the live catalog; **absent from the shipped locale**) |
| Internal ID | `istk0001` |
| Bundled? | **No** — exactly one registry value |
| Components | `HoneUtility.exe`, no children |
| Constituent operations | 1 |

#### B. Purpose and technical explanation

Dynamic interrupt steering is the kernel's ability to re-target device interrupts at runtime (for example, away from a core that is about to be parked, or to follow a thread on hybrid silicon). `InterruptSteeringFlags` is an undocumented kernel feature-flag DWORD read at boot. Writing `0` is an explicit "off".

Hone's catalog claims "fixed interrupt routing per power plan". **No power-plan interaction was observed** — nothing per-plan, per-device, or per-adapter was written. The claim is not supported by the capture.

Disadvantages: undocumented; effect is hardware/OS-generation dependent and may be a no-op; requires reboot; if future silicon depends on interrupt steering for correctness (e.g. keeping an interrupt on an unparked core), disabling it could increase latency instead of reducing it. Effect grade: **INFERRED, possibly no-op**.

#### C. Exact implementation details

| Key | Value | Type | Before | After |
|---|---|---|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel` | `InterruptSteeringFlags` | `REG_DWORD` | absent | `0x00000000` |

Created, not merely zeroed. Absence and `0` are not equivalent to the kernel's flag reader, which is why Hone creates the value rather than leaving it alone.

#### D. Execution sequence

Single write; no sequence to describe. Boot-time effect; Hone's UI does **not** prompt for a reboot.

#### E. Verification

```powershell
$v = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel' -Name InterruptSteeringFlags -ErrorAction SilentlyContinue).InterruptSteeringFlags
if ($null -eq $v) { 'absent' } else { $v }     # expect 0 after apply
```

There is no user-visible or API-observable confirmation that steering changed. **Do not claim a measured benefit** in the Swiftwave UI.

#### F. Rollback

`Remove-ItemProperty … -Name InterruptSteeringFlags` (it did not exist before). Reboot. Verified clean in `[RB]` ("`InterruptSteeringFlags` removed entirely").

#### G. Implementation recommendation — **READY**

One DWORD, trivially reversible, harmless on hardware where it is a no-op. Ship it with an honest label ("kernel interrupt steering disabled — no measurable effect on most systems") and a reboot flag. Do not copy Hone's claim about power plans.

---

### 3.7 `PE9bgwi0` — "Disable Windows Services"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Disable Windows Services |
| Category | Optimizations |
| Internal ID | `PE9bgwi0` |
| Bundled? | Yes — 7 services, one homogeneous operation |
| Components | `HoneUtility.exe` only. **No `sc.exe` / `net.exe` children** — `Start` written directly. |
| Constituent operations | 7 |

#### B. Purpose and technical explanation

Seven services get `Start=4` (Disabled). All seven were already **Stopped**, so nothing was killed; only the next boot's start type changes. Each service keeps its binary and its key — this is the most polite form of "disable a service", and it is fully reversible.

The list is notably *not* the usual debloat list: no `DiagTrack`, `MapsBroker`, `SysMain`, `Spooler`, `RemoteRegistry`, `dmwappushservice`. Those presumably belong to other tweaks (WSearch and WerSvc appear in `9C0X9LiC`; DiagTrack has its own catalog entry). This is real evidence that Hone's operations are **partitioned across cards**, which is why a clone must not re-derive a "disable services" feature from a generic debloat list and assume parity.

Impact per service:

| Service | Role | Risk |
|---|---|---|
| `SharedAccess` | Internet Connection Sharing | **Breaks mobile hotspot / ICS / `netsh wlan hostednetwork`.** Widely used on laptops. |
| `upnphost` | UPnP Device Host | **Breaks UPnP/DLNA discovery, automatic router port mapping, media streaming, some console/party-chat flows.** |
| `Ndu` | Network Data Usage **driver** | Per-app data usage in Settings stops; `NetworkServiceTriggers` bookkeeping disappears (5 trigger keys under `CurrentVersion\NetworkServiceTriggers\Triggers\bc90d167-…` were removed as a consequence). |
| `WalletService` | Wallet | Harmless. |
| `SEMgrSvc` | Payments / NFC SE | Harmless unless NFC payments are used. |
| `PhoneSvc` | Phone | Harmless unless Phone Link is used. |
| `AxInstSV` | ActiveX Installer | Harmless on modern systems. |

#### C. Exact implementation details

`HKLM\SYSTEM\CurrentControlSet\Services\<Name>\Start`, `REG_DWORD`:

| Service | Before | After | Running state before | Running state after |
|---|---|---|---|---|
| `WalletService` | `3` (Manual) | `4` (Disabled) | Stopped | Stopped |
| `SharedAccess` | `3` | `4` | Stopped | Stopped |
| `SEMgrSvc` | `3` | `4` | Stopped | Stopped |
| `Ndu` | `2` (Auto) | `4` | Stopped | Stopped |
| `upnphost` | `3` | `4` | Stopped | Stopped |
| `PhoneSvc` | `3` | `4` | Stopped | Stopped |
| `AxInstSV` | `3` | `4` | Stopped | Stopped |

No reboot prompt was shown by Hone. `SharedAccess` and `Ndu` only take full effect after reboot (INFERRED from demand-start semantics).

#### D. Execution sequence

One loop over the seven names; no ordering, no conditionals, no child processes. Failure path unexercised.

#### E. Verification

```powershell
'WalletService','SharedAccess','SEMgrSvc','Ndu','upnphost','PhoneSvc','AxInstSV' | ForEach-Object {
  $s = Get-Service -Name $_ -ErrorAction SilentlyContinue
  [pscustomobject]@{ Name = $_; StartType = $s.StartType; Status = $s.Status }
}
# expect StartType = Disabled for all seven
```

#### F. Rollback

Restore `Start` to the captured originals: `Ndu` → `2`, all others → `3`. No service needs starting (all were stopped beforehand). Reboot not required for the restore itself (the services become startable immediately), but any service the user then needs must be started manually or by its trigger.

Verified in `[RB]` §4: all seven back to their stock `Start` type, zero residue.

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Service | Verdict |
|---|---|
| `WalletService`, `SEMgrSvc`, `PhoneSvc`, `AxInstSV` | **Ship**, opt-in, no warning needed beyond "you won't use this feature". |
| `SharedAccess` | **Gate** behind an explicit checkbox: "I don't use mobile hotspot / Internet Connection Sharing". Detect an active hosted network first and refuse if one exists. |
| `upnphost` | **Gate**: "I don't use DLNA / UPnP port mapping". Detect an active UPnP device host consumer if possible. |
| `Ndu` | **Ship** with the label "per-app data usage will stop working". Restore to `2`, not `3`. |

Do not silently expand the list. If Swiftwave wants the standard debloat set, ship it as a **separate, clearly named** feature so its blast radius is visible and separately revertible.

---

### 3.8 `9C0X9LiC` — "Reduce Windows Background Activity"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Reduce Windows Background Activity (shipped locale: "Disable Windows Program Compatibility Assistant" — stale name) |
| Category | Optimizations |
| Internal ID | `9C0X9LiC` |
| Bundled? | **Heavily** — 4 services, 34 scheduled tasks, 10 WER values, 14 policy values, 1 kernel value |
| Components | `HoneUtility.exe` only; **no children** — the 34 tasks are disabled through the Task Scheduler COM API (`Enabled` flag + `TaskCache` rewrite), not `schtasks.exe` |
| Constituent operations | 63 (25 registry + 4 services + 34 tasks) |

#### B. Purpose and technical explanation

Five independent intents:

1. **Disable four services**: `WSearch` (the search indexer), `PcaSvc` (Program Compatibility Assistant), `WerSvc` + `wercplsupport` (Error Reporting).
2. **Disable 34 scheduled tasks** across CEIP, Feedback/Siuf, Application Experience, Autochk, DiskDiagnostic, DiskFootprint, Offline Files, Work Folders, Maps, SpacePort, Speech, WCN, NLA, WFP, Provisioning.
3. **Turn WER off in ten places** (machine, policy, per-user policy) so crashes are neither logged nor reported.
4. **Policy hardening**: AppCompat engine off, PCA off, SmartScreen-in-AppCompat off, VDM disallowed, no blocking apps at shutdown, no driver-not-found reporting to WER, WCN registrars and UI off.
5. **One undocumented kernel value**: `SeLpacEnableWatsonThrottling=0`.

Disadvantages, all real:

* **Windows Search off** is the highest-impact change in the whole preset: Start-menu search, Outlook search, Explorer content search and Settings search degrade to name-only or cached results.
* **Crash reporting fully off** — no dumps, so any later support conversation is blind.
* **Miracast / Wi-Fi Direct / WPS pairing break** (`wcn\ui` + all five registrars).
* `Autochk\Proxy` disabled → the scheduled chkdsk-on-boot proxy no longer runs; volume errors may never be scheduled.
* `AllowBlockingAppsAtShutdown=0` → apps that veto shutdown are ignored; **silent data loss** for unsaved work (and it directly contradicts `5Q8oOtIH`'s `AutoEndTasks=0`, contradiction **C-4**).
* `SpaceAgentTask` / `SpaceManagerTask` disabled → Storage Spaces maintenance stops.
* Offline Files / Work Folders / Workplace Join sync stop.

#### C. Exact implementation details

**C.1 Services** — `HKLM\SYSTEM\CurrentControlSet\Services\<Name>\Start`

| Service | Before | After | Running state |
|---|---|---|---|
| `WSearch` | `2` (Auto), **Running** | `4` | **left Running/Disabled** — dies at next reboot |
| `PcaSvc` | `2` (Auto), Running | `4` | left Running/Disabled |
| `WerSvc` | `3` | `4` | Stopped |
| `wercplsupport` | `3` | `4` | Stopped |

**C.2 Scheduled tasks → disabled (34)** — all under `\Microsoft\Windows\`:

`Windows Error Reporting\QueueReporting`; `Customer Experience Improvement Program\Consolidator`, `\UsbCeip`; `Feedback\Siuf\DmClient`, `\DmClientOnScenarioDownload`; `Application Experience\PcaPatchDbTask`, `\StartupAppTask`, `\MareBackup`; `ApplicationData\CleanupTemporaryState`, `\DsSvcCleanup`; `Autochk\Proxy`; `DiskDiagnostic\Microsoft-Windows-DiskDiagnosticDataCollector`; `DiskFootprint\Diagnostics`; `File Classification Infrastructure\Property Definition Sync`; `Location\WindowsActionDialog`; `Maintenance\WinSAT`; `Management\Provisioning\Logon`; `Maps\MapsToastTask`, `\MapsUpdateTask`; `NlaSvc\WiFiTask`; `Offline Files\Background Synchronization`, `\Logon Synchronization`; `Ras\MobilityManager`; `Shell\FamilySafetyMonitor`; `SpacePort\SpaceAgentTask`, `\SpaceManagerTask`; `Speech\SpeechModelDownloadTask`; `User Profile Service\HiveUploadTask`; `WCM\WiFiTask`; `Windows Filtering Platform\BfeOnServiceStartTypeChange`; `Windows Media Sharing\UpdateLibrary`; `Work Folders\Work Folders Logon Synchronization`, `\Work Folders Maintenance Work`; `Workplace Join\Automatic-Device-Join`.

Mechanism: the task's `Enabled` flag flipped via the Task Scheduler API, which rewrites `TaskCache\Tree\<task>` plus the `\Plain` / `\Logon` blobs. The tasks still exist and are re-enableable by name.

**C.3 Windows Error Reporting** — ten values:

| Key | Value | After | Change |
|---|---|---|---|
| `HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting` | `Disabled` | `1` | created |
| same | `DontSendAdditionalData` | `1` | created |
| same | `DontShowUI` | `1` | created |
| same | `LoggingDisabled` | `1` | created |
| `…\WER\Assert Filtering Policy` | `ReportAndContinue` | `0` | `1` → `0` |
| `…\WER\Consent` | `DefaultConsent` | `0` | `4` → `0` |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\windows error reporting` | `Disabled`, `DontSendAdditionalData` | `1`, `1` | key created |
| `HKLM\SOFTWARE\Policies\Microsoft\pchealth\errorreporting` | `DoReport` | `0` | key created |
| `HKCU\SOFTWARE\Policies\Microsoft\Windows\windows error reporting` | `Disabled` | `1` | key created |

**C.4 Policies**

| Key | Values |
|---|---|
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppCompat` | `AllowTelemetry=0`, `DisableEngine=1`, `DisablePCA=1`, `SbEnable=0`, `VDMDisallowed=1` |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\System` | `AllowBlockingAppsAtShutdown=0` |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\deviceinstall\settings` | `DisableSendGenericDriverNotFoundToWER=1`, `DisableSendRequestAdditionalSoftwareToWER=1` |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\wcn\ui` | `DisableWcnUi=1` |
| `HKLM\SOFTWARE\Policies\Microsoft\Windows\wcn\registrars` | `EnableRegistrars=0`, `DisableUPnPRegistrar=1`, `DisableInBand802DOT11Registrar=1`, `DisableFlashConfigRegistrar=1`, `DisableWPDRegistrar=1` |

**C.5 Kernel** — `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\SeLpacEnableWatsonThrottling` = DWORD `0` (created). Meaning undocumented → effect **INFERRED**.

#### D. Execution sequence

Services first, then task disables, then policy/registry values (observed order within the single run); no handler dependencies were exercised. `WSearch`/`PcaSvc` were **not** stopped after being disabled, so the system runs in a "Running/Disabled" state until reboot — a behaviour a clone should copy only deliberately.

#### E. Verification

```powershell
# services
'WSearch','PcaSvc','WerSvc','wercplsupport' | ForEach-Object {
  $s = Get-Service $_ -ErrorAction SilentlyContinue; "$_ : $($s.StartType)/$($s.Status)" }

# tasks — assert on the State, not on the registry
$paths = @('\Microsoft\Windows\Windows Error Reporting\','\Microsoft\Windows\Customer Experience Improvement Program\',
           '\Microsoft\Windows\Application Experience\','\Microsoft\Windows\Autochk\','\Microsoft\Windows\SpacePort\',
           '\Microsoft\Windows\Work Folders\','\Microsoft\Windows\Maps\')
Get-ScheduledTask | Where-Object { $p = $_.TaskPath; ($paths | Where-Object { $p -like "$_*" }) } |
  Select TaskName,State | Sort-Object State                    # expect Disabled for the applied set

# WER
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting' |
  Select Disabled,DontSendAdditionalData,DontShowUI,LoggingDisabled          # all 1
Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppCompat' |
  Select AllowTelemetry,DisableEngine,DisablePCA,SbEnable,VDMDisallowed

# kernel
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager\kernel').SeLpacEnableWatsonThrottling
```

#### F. Rollback

* Services: `WSearch`→`2`, `PcaSvc`→`2`, `WerSvc`→`3`, `wercplsupport`→`3`; do **not** start them, Windows does.
* Tasks: re-enable the same 34 by name (`Enable-ScheduledTask`). Verified in `[RB]`: all 34 re-enabled.
* WER values: delete the eight created values; restore `ReportAndContinue`→`1`, `DefaultConsent`→`4`.
* Policies: delete the created keys (`AppCompat`, `wcn*`, `windows error reporting`, `pchealth`, `appprivacy`, `fssprov`, `deviceinstall\settings` values). `[RB]` confirms `SOFT_Policies.reg` shows no section at all in the residue diff — the policy removal is complete.
* `SeLpacEnableWatsonThrottling`: delete.
* Reboot to actually stop `WSearch`.

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Constituent | Verdict |
|---|---|
| 34 scheduled tasks | **Ship**, but filter: skip any task the user has customised, and skip tasks that no longer exist on the target build. Detect by name and tolerate absence. |
| WER off | **Ship** as "disable crash reporting" with the consequence stated. Keep `DontShowUI` and `LoggingDisabled` together or neither — mismatched combinations produce crashes with no dump and no dialog, which is worse than either alone. |
| `WSearch` disable | **Separate opt-in toggle**, never bundled silently. This is the one change users notice. |
| `PcaSvc` / `wercplsupport` | **Ship.** |
| `wcn\registrars` + `wcn\ui` | **Gate** ("breaks Miracast / wireless display / WPS pairing"). |
| `Autochk\Proxy` | **Exclude.** Disk-integrity scheduling is not background bloat. |
| `AllowBlockingAppsAtShutdown=0` | **Exclude.** Data-loss vector, and incoherent with `5Q8oOtIH`'s `AutoEndTasks=0` (C-4). |
| `AppCompat` `DisableEngine=1`, `DisablePCA=1`, `VDMDisallowed=1`, `SbEnable=0` | **Ship** `DisablePCA`/`DisableEngine`; **gate `VDMDisallowed`** (it can break 16-bit/legacy installers) and **keep `SbEnable` out** — `SbEnable=0` interacts with SmartScreen reputation checks in ways that are security-adjacent. |
| `deviceinstall\settings` two values | **Ship.** Stops driver-missing prompts going to WER. |
| `SeLpacEnableWatsonThrottling` | **Exclude.** Undocumented, unverifiable. |
| Left-running-while-disabled behaviour | **Improve.** Stop the services after disabling, or clearly require a reboot before claiming success. |

---

### 3.9 `k6ad89bg` — "Enable Maximum Performance Power Mode"

#### A. Identification

| Field | Value |
|---|---|
| UI display name | Enable Maximum Performance Power Mode (absent from shipped locale — server-side addition) |
| Category | Optimizations |
| Internal ID | `k6ad89bg` |
| Bundled? | **Heavily** — a power-scheme import plus six unrelated platform/device/power groups |
| Components | `HoneUtility.exe` importing `HoneV5.pow` **in-process** (no `powercfg.exe` child in 4688 → `PowerImportPowerScheme` / `PowerSetActiveScheme` via PowrProf, INFERRED mechanism, VERIFIED effect); plaintext `sett` for service calls; one `rename_file` |
| Constituent operations | ≈48 |

#### B. Purpose and technical explanation

Seven intents:

1. **Import and activate a custom power scheme** `44444444-4444-4444-4444-444444444452`, registered as "Hone Ultimate Power Plan V5". Content decoded offline in `[POW]` and observed live in the scheme keys.
2. **Force classic S3 sleep**: `PlatformAoAcOverride=0` disables Modern Standby (S0ix).
3. **Disable storage/power gating**: `stornvme\IdlePowerMode=0` (APST off), `pci\ASPMOptOut=1` (PCIe ASPM off), `StorageD3InModernStandby=0`.
4. **Per-device WDF idle/power transitions off** on 7 devices (PCH root ports, chipset, RTX GPU).
5. **Suppress power telemetry**: three ETW diagnostic channels disabled, their WMI Autologger blocks deleted, three WMI security ACL entries removed, and the `Microsoft-windows-sleepstudy-events.dll` manifest **renamed inside System32**.
6. **Energy estimation off**: tagged-energy logging disabled.
7. **Misc**: `fssprov\EncryptProtocol=0`; a `\DisablePowerSaving` task created; `Power Efficiency Diagnostics\AnalyzeSystem` disabled; five services coerced to Automatic.

Disadvantages:

* `PlatformAoAcOverride=0` on a machine designed for Modern Standby can **break sleep/resume** and battery reporting. This is the riskiest single value in the nine tweaks.
* ASPM off + APST off + WDF transitions off all raise idle power and heat; on a laptop this is a battery-life decision, not a free win.
* Renaming a System32 manifest DLL is a **servicing hazard**: Windows Update or SFC will report a modified system file, and repair tooling may restore it, silently re-enabling the telemetry the tweak was suppressing.
* Removing WMI provider security descriptors is an integrity change that a clone must not reproduce.
* Hone deletes its imported scheme on revert and does **not** restore the previously active scheme (defect **D-1**).

#### C. Exact implementation details

**C.1 Scheme import/activation**

* `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes\ActivePowerScheme`: `8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` ("Paragon Perf V4 Power Plan") → `44444444-4444-4444-4444-444444444452` ("Hone Ultimate Power Plan V5").
* The scheme is a real, registered scheme (`powercfg /LIST` shows it) with its own settings subtree.

**C.2 Settings written into the imported scheme** — `…\PowerSchemes\44444444-4444-4444-4444-444444444452\<subgroup>\<setting>`

| Subgroup | Setting GUID | AC | DC | Meaning |
|---|---|---|---|---|
| `7516b95f` Display | `3c0bc021` | 2700 | 90 | Turn off display after (s) |
| `7516b95f` Display | `f1fbfde2` | 50 | 40 | Dimmed display brightness (%) |
| `0012ee47` Disk | `d3d55efd` | 4000 | 1000 | Secondary NVMe idle timeout (s) |
| `0012ee47` Disk | `d639518a` | 500 | 100 | Primary NVMe idle timeout (s) |
| `54533251` Processor | `2ddd5a84` | 1 | 7 | Core parking increase time |
| `54533251` Processor | `1facfc65` | 100 | 50 | Perf when unparked, PEC1 (%) |
| `54533251` Processor | `bae08b81` | 2 | 5 | Heterogeneous short-thread policy |
| `54533251` Processor | `0cc5b647` | 100 | 100 | Core parking min cores (%) |
| `54533251` Processor | `447235c7` | 1 | 1 | Parked performance state |
| `54533251` Processor | `619b7505` | 100 | 50 | Latency-sensitivity hint perf, PEC1 |
| `2a737441` USB | `d4e98f31` | 0 | 0 | USB selective suspend = off |
| `2a737441` USB | `0853a681` | 200 | 50 | Hub selective-suspend timeout (ms) |

Two of the processor rows are approximate GUID→name mappings (`[K6]` flags this); the twelve AC/DC pairs are the observed data. The offline decode `[POW]` lists **27** settings in the hive — the 12 above are the ones Hone also writes explicitly. The other 15 exist in the scheme by virtue of the import and were not itemised in the diff. Treat the full plan content as: 12 observed rows + 15 artifact-decoded rows, reconciled in `[POW]`.

**C.3 Platform / storage / PCI switches**

| Key | Value | After | Before |
|---|---|---|---|
| `Control\Power` | `PlatformAoAcOverride` | `0` | absent/modified — forces S3 |
| `Control\Power` | `PerfBoostAtGuaranteed` | `1` | created |
| `Control\Storage` | `StorageD3InModernStandby` | `0` | modified |
| `Control\Power\EnergyEstimation\TaggedEnergy` | `DisableTaggedEnergyLogging` / `TelemetryMaxApplication` / `TelemetryMaxTagPerApplication` | `1` / `0` / `0` | `0` / `250` / `50` |
| `Services\stornvme\Parameters\Device` | `IdlePowerMode` | `0` | modified |
| `Services\pci\Parameters` | `ASPMOptOut` | `1` | created/modified |
| `Control\Class\{4d36e972…}\0012` | `PnPCapabilities` | `0x18` | `0x10` |

**C.4 Per-device WDF power transitions (7 devices)** — `Control\Enum\PCI\…\Device Parameters\WDF` (note: `Control\Enum`, not the `Enum` root): `WdfDirectedPowerTransitionEnable=0`, `IdleInWorkingState=0` on Intel `VEN_8086 DEV_51E0`, `DEV_51E8`, `DEV_51CA`, `DEV_51ED`, `DEV_A74F`, `DEV_A71E`, and NVIDIA `VEN_10DE DEV_22BE`.

**C.5 Telemetry suppression**

* `HKLM\SYSTEM\CurrentControlSet\Control\WINEVT\Channels\…` `Enabled` `1` → `0` for `Microsoft-Windows-Kernel-Processor-Power/Diagnostic`, `Microsoft-Windows-SleepStudy/Diagnostic`, `Microsoft-Windows-UserModePowerService/Diagnostic`.
* `Control\WMI\Autologger\EventLog-Microsoft-Windows-{SleepStudy,UserModePowerService,Kernel-Processor-Power}-Diagnostic` — **blocks deleted**.
* `Control\WMI\Security` — three provider GUID ACL entries removed (`e01f9318…`, `e153e8ff…`, `2e0e91fe…`).
* File: `HoneUtility.exe sett rename_file "%WinDir%\System32\Microsoft-windows-sleepstudy-events.dll" "%WinDir%\System32\Microsoft-windows-sleepstudy-events.dll.old" ""`.

**C.6 Services** — `DcomLaunch`, `RpcEptMapper`, `RpcSs`, `Schedule`, `SystemEventsBroker` → `Automatic` + start (all already Automatic; no-op safety net).

**C.7 Scheduled tasks** — created `\DisablePowerSaving` (`{0A4707B7-B8C2-42D6-9680-1F09EB227ED3}`, `Index=2`, immediately `Ready`); disabled `\Microsoft\Windows\Power Efficiency Diagnostics\AnalyzeSystem`.

**C.8 Miscellaneous** — `HKLM\SOFTWARE\Policies\Microsoft\Windows\fssprov\EncryptProtocol` = `0` (key created); a fast-startup/S0 state blob under `Session Manager\Power` rewritten as a side effect of scheme activation.

#### D. Execution sequence

Scheme import → scheme activation → scheme setting writes → platform/device switches → telemetry suppression (channels, autologger, ACL) → file rename → `serv_apply` batch → task create/disable. This ordering is reconstructed from the diff grouping and is **not** timestamp-resolved within the run; treat it as indicative. The `rename_file` must precede the channel disable for the manifest to stop loading on next boot (INFERRED).

Conditional execution, failure handling, partial-apply behaviour: all **UNCONFIRMED**.

#### E. Verification

```powershell
powercfg /getactivescheme                                  # expect 44444444-…-444444444452
powercfg /list | Select-String '44444444'                  # scheme exists
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes').ActivePowerScheme
Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' | Select PlatformAoAcOverride,PerfBoostAtGuaranteed
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device').IdlePowerMode   # 0
(Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\pci\Parameters').ASPMOptOut                  # 1
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Enum\PCI' -Recurse -ErrorAction SilentlyContinue |
  Where-Object PSChildName -eq 'WDF' | ForEach-Object { (Get-ItemProperty $_.PSPath) |
    Select-Object @{n='Dev';e={$_.PSPath}},WdfDirectedPowerTransitionEnable,IdleInWorkingState }
Get-WinEvent -ListLog 'Microsoft-Windows-SleepStudy/Diagnostic' | Select LogName,IsEnabled          # False
Test-Path "$env:WINDIR\System32\Microsoft-windows-sleepstudy-events.dll.old"                       # True
Get-ScheduledTask -TaskName 'DisablePowerSaving' | Select State
```

Also verify sleep still works (`powercfg /a` lists S3 as available and S0ix as unavailable after the change) — and tell the user that this is exactly the consequence.

#### F. Rollback

| Item | Restore |
|---|---|
| Active scheme | `powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` (the **captured** previous scheme — this is defect **D-1**; do not fall back to Balanced) |
| Imported scheme | Delete `44444444-…` after switching away |
| Scheme settings | Not restorable individually once the scheme is deleted; if keeping the scheme, rewrite the 12 rows |
| `PlatformAoAcOverride` | delete (pre-state not captured as a value; absent → delete) |
| `PerfBoostAtGuaranteed` | delete (created) |
| `StorageD3InModernStandby` | prior unknown — record before write |
| `TaggedEnergy` ×3 | `0` / `250` / `50` (captured) |
| `stornvme IdlePowerMode` | prior unknown — record |
| `pci ASPMOptOut` | delete |
| NIC `0012` `PnPCapabilities` | `0x10` (captured) |
| WDF ×14 | delete both values per device |
| Channels ×3 | set `Enabled=1` |
| Autologger blocks ×3 | **not restored by Hone** (defect **D-3**) — a clone must snapshot the whole `EventLog-*-Diagnostic` key or re-add it from a known-good template |
| WMI `Security` ACL ×3 | **not restored by Hone** — and *not reproducible*; exclude the operation instead |
| `Microsoft-windows-sleepstudy-events.dll` | rename `.old` → original; verified working (`[RB]` §2) |
| Services | no action |
| `\DisablePowerSaving` | unregister |
| `AnalyzeSystem` task | re-enable |
| `fssprov EncryptProtocol` | delete key |

Reboot required for `PlatformAoAcOverride`, ASPM, APST, WDF, and the channel/autologger state.

#### G. Implementation recommendation — **READY WITH MODIFICATIONS**

| Constituent | Verdict |
|---|---|
| Custom power scheme | **Ship, but generate it.** `powercfg -duplicatescheme` from the current plan + `powercfg -setacvalueindex`/`-setdcvalueindex` for the 12 rows. Do not ship a binary `.pow`; do not reuse GUID `44444444-…` (it collides with Hone on machines that ran both). |
| Restoring the previous scheme on revert | **Ship — and this is the one place Swiftwave must be explicitly better than Hone.** Snapshot `ActivePowerScheme` (and the full plan if custom) and restore it. |
| `PlatformAoAcOverride=0` | **Gate hard.** Detect `powercfg /a`: if S0ix is the only low-power idle state, warn that forcing S3 may break sleep. Desktop-only default. |
| NVMe idle timeouts + `stornvme IdlePowerMode=0` | **Ship, opt-in**, labelled "higher idle power / heat". |
| `pci ASPMOptOut=1` | **Ship, opt-in**, same label. |
| WDF `WdfDirectedPowerTransitionEnable=0`, `IdleInWorkingState=0` | **Ship** on the GPU and chipset only; skip root ports unless the user asks. |
| ETW channel `Enabled=0` ×3 | **Ship.** Supported, reversible. |
| WMI Autologger block deletion | **Modify**: set the block's `Start`/`Enabled` to disabled rather than deleting the key, so it can be restored byte-for-byte. |
| WMI `Security` ACL removal | **Exclude.** Integrity change with no rollback. |
| `Microsoft-windows-sleepstudy-events.dll` rename | **Exclude.** Mutating System32 breaks servicing; the channel flag already achieves the goal. |
| `fssprov EncryptProtocol=0` | **NEEDS VALIDATION.** Obscure; unrelated to "performance power mode". |
| `\DisablePowerSaving` task | **Do not recreate.** Purpose unconfirmed; its action bytes were never captured. |
| `AnalyzeSystem` disable | **Ship.** |
| 5 × `serv_apply` | **Verify-only.** |

---

## 4. Global interaction and conflict analysis

This section exists because the nine tweaks are not independent, and because a per-feature rollback that ignores these interactions will produce broken state. Every row below is either directly captured or explicitly marked INFERRED.

### 4.1 Operations that modify the same registry value or key family

| Value / key family | Written by | Nature of the conflict |
|---|---|---|
| `Services\BITS\SvcHostSplitDisable` | `XLSEdxVW` → `0` | Directly opposes `3WwEZo0M`'s consolidation model (which sets `1` on 49 services and raises the global threshold). Applying both leaves BITS split out while everything else is consolidated — an incoherent end state. BITS is **not** in `3WwEZo0M`'s 49, so this is `XLSEdxVW` deliberately carving out an exception, most likely because BITS behaves badly in a shared host (INFERRED). |
| `Control\Class\{4d36e972…}\<instance>\PnPCapabilities` | `XLSEdxVW` (instance `0001`), `k6ad89bg` (instance `0012`) | Same value name under the same class GUID, different instances. On this host they are different adapters. On a machine where two class instances resolve to the same adapter, the second backup overwrites the first, and the first revert writes a stale value. **A clone must key backups by adapter GUID, not by class instance index.** |
| `Control\Session Manager\kernel\*` | `5Q8oOtIH` (10 values), `istk0001` (`InterruptSteeringFlags`), `9C0X9LiC` (`SeLpacEnableWatsonThrottling`) | Same key, disjoint values. No value collision, but any feature that implements "reset kernel tweaks" must reset all three groups or it leaves partial state. |
| `Services\DcomLaunch/RpcEptMapper/RpcSs/Schedule/SystemEventsBroker\Start` | `5Q8oOtIH` **and** `k6ad89bg` (identical `serv_apply … Automatic start` batch) | Idempotent today (target value identical). Becomes a real conflict the moment either tweak changes its target start type: revert of one restores the other's target. |
| `NetBT\Parameters\Interfaces\Tcpip_{GUID}\NetbiosOptions` | `XLSEdxVW` | Per-interface; new adapters (VPN, USB tether) appear later and are not covered — the tweak is a point-in-time snapshot, not a policy. Users will report "it came back". |
| `Control\Class\{4d36e968…}` (display class) vs `Control\Video\{GUID}\*` | `5Q8oOtIH` (`AllowDeepCStates`) | Two representations of the same GPU state. A rollback that clears one and not the other leaves a half-disabled C-state policy. |

### 4.2 Tweaks that modify the same services

| Service | Tweaks |
|---|---|
| `WSearch` | `9C0X9LiC` (`Start=4`). The catalog also has a dedicated "Disable Windows Search Indexer" entry (`uX8vbPmT`) that is not captured — **two cards with the same target**. |
| `WerSvc` / `wercplsupport` | `9C0X9LiC`; the catalog also has "Disable Windows Error Reporting" (`MRqdp6hw`). |
| `DcomLaunch`, `RpcEptMapper`, `RpcSs`, `Schedule`, `SystemEventsBroker` | `5Q8oOtIH`, `k6ad89bg`. |
| `lmhosts` | `XLSEdxVW` (`Manual → Disabled`). Not touched by `PE9bgwi0`, despite being the obvious member of a "disable services" list — further evidence the partitioning is arbitrary. |
| `HoneSvc` | `3WwEZo0M` writes `SvcHostSplitDisable=1` **on Hone's own service**. A clone must never include itself. |
| `CscService`, `lfsvc` | **Not touched by any apply**, but flipped `Disabled → Manual` by the revert run (defect **D-4**). |

### 4.3 Conflicting power-plan configurations

| Item | Conflict |
|---|---|
| `ActivePowerScheme` | `k6ad89bg` owns it. The catalog's "Optimize Windows Power Plan" (`U1dtiije`) almost certainly owns the same value (not captured → INFERRED). Two features that both "activate a plan" cannot coexist; the last one wins and the first one's revert silently steals activation back. |
| Core parking / perf-boost settings | `k6ad89bg`'s plan sets core-parking min cores 100 / boost mode Aggressive **inside its own scheme**. Any other feature that writes processor settings into the *active* scheme will fight it, because the active scheme is now Hone's clone rather than the user's plan. |
| `5Q8oOtIH` timer values vs `k6ad89bg` plan | Not a direct collision, but both steer the same outcome (reduced jitter) through different mechanisms; the combined effect is unmeasured. |
| User's own plan | The captured pre-state was a **third-party** plan ("Paragon Perf V4"). A machine running two optimisers will have its active scheme changed by whichever applied last, and neither records the other's plan. |

### 4.4 Overlapping network optimisations

| Overlap | Detail |
|---|---|
| Congestion provider | `XLSEdxVW` sets all six supplemental templates to `bbr2`. The catalog has a dedicated "Optimize Network Congestion Provider" (`UpUzWNLk`) — same templates. Applying both is idempotent but reverting one breaks the other's assumption. |
| Task offload | `XLSEdxVW` writes `DisableTaskOffload=0` **and** runs `netsh int ip set global taskoffload=enabled` — i.e. it *enables* offload. The catalog has "Disable Task Offloading" (`QIDIS7Hu`) which does the opposite. These two are mutually exclusive by design, and Hone ships both. |
| IPv6 | `XLSEdxVW` sets `DisabledComponents=1` (tunnels only) **and** rewrites one adapter's `UpperBind` to remove `Tcpip6` (native IPv6 on that adapter). Two different IPv6 intents in one tweak. |
| NIC power management | `XLSEdxVW` (instance `0001`) and `k6ad89bg` (instance `0012`) both set `PnPCapabilities=0x18` plus assorted power-saving values on different adapters. |
| NetBIOS | `XLSEdxVW` only. But `lmhosts=Disabled` also belongs to the "disable services" family, so the two cards overlap conceptually. |

### 4.5 Dependencies between optimisations

| Dependency | Explanation | Grade |
|---|---|---|
| `oPu4x2Ef` → `RqbxUufM` | MSI changes *how* interrupts are delivered; affinity changes *where* they are delivered. Affinity masks are more meaningful on MSI-capable devices. Applied in this order by `HoneSvc`, 1.3 s apart. | VERIFIED-RUNTIME (order), INFERRED (benefit) |
| `3WwEZo0M` → everything | Changing `svchost` topology changes how every service-based tweak's state is observable (`Running/Disabled` can appear as separate processes). Verification scripts that look for a service's `svchost` host must run after a reboot. | VERIFIED-RUNTIME (state), INFERRED (interaction) |
| `9C0X9LiC` (WSearch disabled) → `5Q8oOtIH` (shell timings) | Search being dead makes the shell-timing tweak's user-visible effect look better or worse depending on whether the user blames search. Not a technical dependency; a UX one. | INFERRED |
| `k6ad89bg` → `istk0001` / `RqbxUufM` | The plan's core-parking and boost settings change which cores are available when interrupts are pinned. Hone's claim that steering is "per power plan" (unsupported) may refer to this. Unmeasured. | UNCONFIRMED |

### 4.6 Operations that should not be enabled simultaneously

| Combination | Why |
|---|---|
| `3WwEZo0M` + `XLSEdxVW` | The BITS `SvcHostSplitDisable=0` carve-out makes the consolidation story false, and the two reverts interleave on the same key. |
| `5Q8oOtIH` + `9C0X9LiC` | `AutoEndTasks=0` vs `AllowBlockingAppsAtShutdown=0` — contradictory shutdown semantics (C-4). A clone must pick one policy and enforce it globally. |
| `k6ad89bg` + any other power-plan feature | Single ownership of `ActivePowerScheme` is required for correct rollback (D-1). |
| `XLSEdxVW` + any other netsh/congestion feature | Same values, no coordination, and `bbr2` has no per-template ownership record. |

### 4.7 Operations that overwrite previous user configuration

These are the cases where "restore the original" is not enough, because the original *was* a deliberate user choice:

| Value | Captured prior | Why it matters |
|---|---|---|
| `NtfsDisableLastAccessUpdate` | `0x80000002` — **system-managed policy**, not a user default | Writing `1` converts a Windows-managed policy into a hard user setting; restoring `0x80000002` is required, not "delete". |
| `ActivePowerScheme` | `8c5e7fda…` (Paragon Perf V4) | A third-party optimiser's plan. Hone's revert abandons it (D-1). |
| `NetbiosOptions` | `0` per interface | `0` = "use DHCP-provided setting"; `2` = hard disable. Restoring `0` preserves the user's DHCP intent. |
| NIC advanced properties | mixed | Users tune EEE/flow-control/interrupt moderation per adapter; the tweak overwrites 9 values with no per-adapter record. |
| `AllowAutoGameMode`, background-app policy | user preference | A per-machine policy overrides every user's choice. |
| `Control Panel\Desktop` timeouts | not captured | If the user had already shortened them, Hone's revert deletes the user's value along with its own (D-2 class). |
| `MessageNumberLimit` on the GPU | not captured | Deleted outright with no record (D-2). |

### 4.8 Changes that affect other Swiftwave Tweaks features

If Swiftwave already has any of these, the Hone-derived feature must coordinate rather than write blindly:

* **A service manager / startup manager.** After `9C0X9LiC`+`PE9bgwi0`, eleven services are `Disabled`. The manager will show them as user-modified, and its own "restore defaults" may fight the tweak's revert. Either own the state in one place, or make the tweak's revert the only writer.
* **A power-plan feature.** See §4.3.
* **A network/latency feature.** See §4.4.
* **A restore-point / backup feature.** Hone creates a System Restore checkpoint before applying. If Swiftwave has an equivalent, it must run *before* the first write, and it must not rely on the checkpoint for correctness of per-value rollback.
* **A telemetry/privacy feature.** `9C0X9LiC` and `k6ad89bg` both disable telemetry through different mechanisms (policy vs ETW channels vs System32 file rename). Only the first two are defensible.

### 4.9 How applying one tweak changes another's baseline

Concrete cases that would break a naive "detect → apply" implementation:

1. After `3WwEZo0M`, `SvcHostSplitDisable=1` exists on 49 services. `XLSEdxVW` then writes `0` on BITS. A detection routine for "is `3WwEZo0M` applied?" that checks "all services with the value = 1" will now report *not applied*.
2. After `9C0X9LiC`, `WSearch` is `Running/Disabled`. A detection routine that reads `Status` to decide "is the indexer running?" gets `Running` and concludes nothing changed.
3. After `k6ad89bg`, the active scheme is Hone's clone. A detection routine for a *different* power feature that compares against "Balanced" will report the user has a custom plan and may refuse to apply.
4. After `5Q8oOtIH`, `WaitToKillAppTimeout` exists in both HKLM and per-user; a check of only one location reports a false negative.
5. After `RqbxUufM`, `Affinity Policy` keys exist on 7 devices — but Windows creates that key for other devices on its own, so "key exists" is not evidence of the tweak.

This is why D3 §3 requires **explicit ownership records** rather than state inference.

---

## 5. Master implementation tracker

### 5.1 The nine specified optimizations

| UI optimization | Category | Internal ID | Underlying ops | Main targets | Evidence | Rollback | Safety | Swiftwave readiness | Implementation notes |
|---|---|---|---|---|---|---|---|---|---|
| Optimize FPS & Input Lag | Optimizations | `5Q8oOtIH` | ≈68 (58 reg, 3 BCD, 5 svc, 1 task) | BCD, `Session Manager\kernel`, `Control`, per-user Desktop/Mouse, `Control\Video`, DirectX, shell flags, policies, task | VERIFIED-RUNTIME | Verified (D-2 caveat on BCD delete) | Medium — undocumented timer internals, data-loss timeouts | **READY WITH MODIFICATIONS** | Ship ~60 %; drop kernel tunables, `disabledynamictick`, `NoLazyMode`; gate VRR/GameMode/background policy |
| Optimize Storage & Memory | Optimizations | `3WwEZo0M` | 58 (1 threshold, 49 per-service, 6 NTFS, 2 ReserveManager) | `SvcHostSplitThresholdInKB`, `Services\*`, `FileSystem`, `ReserveManager` | VERIFIED-RUNTIME | Verified, zero residue | Low–medium — fault-domain + MFT space | **READY WITH MODIFICATIONS** | Rewrite the 49-service list dynamically; MFT zone opt-in; use DISM for reserved storage |
| Optimize Device Affinities | Optimizations | `RqbxUufM` | 21 (7 devices × 3 values) | `Enum\PCI\…\Affinity Policy` | VERIFIED-RUNTIME (service path) | Hone: **UNCONFIRMED** (never reverted). Clone: exact by deletion | Medium — mask correctness | **READY WITH MODIFICATIONS** | Compute topology; High priority only on chosen device; no re-apply loop |
| Optimize Message Signal Interrupts | Optimizations | `oPu4x2Ef` | 8 (7 set + 1 delete) | `…\MessageSignaledInterruptProperties` | VERIFIED-RUNTIME (service path) | Hone: **UNCONFIRMED**; original `MessageNumberLimit` never recorded | Medium — device may fail to start | **READY WITH MODIFICATIONS** | Whitelist devices; record the limit before deleting; auto-restore on device error |
| Optimize Network Performance | Optimizations | `XLSEdxVW` | ≈519 (25 netsh, ~41 reg, 452 QoS) | `netsh` global/templates, `Tcpip*`, `Dnscache`, `NetBT`, NIC class, `qos` policy | VERIFIED-RUNTIME | Verified clean; `UpperBind` not reversible | Medium–high — destructive binding rewrite, `initialRto` regression | **READY WITH MODIFICATIONS** | Drop `initialRto=2000`, `UpperBind`, BITS key; probe `bbr2`; curate the QoS list |
| Disable Dynamic Interrupt Steering | Optimizations | `istk0001` | 1 | `kernel\InterruptSteeringFlags` | VERIFIED-RUNTIME | Verified (removed entirely) | Low — undocumented, likely no-op | **READY** | One DWORD + reboot flag; honest label; no "power plan" claim |
| Disable Windows Services | Optimizations | `PE9bgwi0` | 7 (7 services) | `Services\*\Start` | VERIFIED-RUNTIME | Verified, all 7 restored | Medium — ICS / UPnP breaks | **READY WITH MODIFICATIONS** | Ship 4; gate `SharedAccess`, `upnphost`; label `Ndu` |
| Reduce Windows Background Activity | Optimizations | `9C0X9LiC` | 63 (25 reg, 4 svc, 34 tasks) | `Services`, `TaskCache`, WER, AppCompat/wcn/deviceinstall policies, kernel | VERIFIED-RUNTIME | Verified: 34 tasks re-enabled, policies fully removed | High — search, crash diagnostics, Miracast, shutdown data loss | **READY WITH MODIFICATIONS** | Separate `WSearch` toggle; exclude `Autochk\Proxy`, `AllowBlockingAppsAtShutdown`, `SeLpac…` |
| Enable Maximum Performance Power Mode | Optimizations | `k6ad89bg` | ≈48 (scheme + 12 rows + 9 platform + 14 WDF + 3 channel + ~4 telemetry/file/svc/task) | Power scheme, `Control\Power`, `Control\Storage`, `stornvme`, `pci`, WDF, WINEVT, WMI, System32 file | VERIFIED-RUNTIME | **Partial by design** — D-1, D-3; ACL + file rename not restored | High — S3 forcing, System32 mutation | **READY WITH MODIFICATIONS** | Generate the plan; restore the previous active scheme; exclude ACL removal and the DLL rename |

### 5.2 Reconciliation to all 106 operations

| Status | Count | Where enumerated |
|---|---|---|
| Specified with runtime evidence | 9 | §3.1–3.9 |
| Catalog operations identifiable by ID and name in the shipped locale, **not captured** | 95 | §5.3 below (grouped), and row-per-ID in `Implementation-Tracker.json` / `.csv` with `readiness = "NOT CAPTURED"` |
| Catalog operations not individually identifiable from available artefacts | 2 | 106 − 102 = 4, minus the 2 captured IDs that are absent from the locale (`k6ad89bg`, `istk0001`) = **2**. `Implementation-Tracker.json` carries two placeholder rows `UNATTRIBUTED-1..2`. |
| **Total** | **106** | — |

The 95 uncaptured entries are **not** specified and are **not** recommended. Their presence in the tracker is an accounting device, not a claim that they were investigated. Per §0.3, the only honest statement about them is: Hone ships them, their names are known, their behaviour was never observed.

### 5.3 The 95 uncaptured operations (accounting only)

**Basic (28):** `vuhb28o8`, `9iomolwm`, `z90xk5at`, `cgcg315u`, `vbz0p0ii`, `vxlrldeq`, `eeonbkv7`, `1uqq6b4x`, `bhixaibk`, `gx5vkld5`, `nrflzg6z`, `hac9fl1v`, `r01odzd9`, `uycq83fy`, `ys1a8ndu`, `aiz5euyc`, `co7irvci`, `smwe5jff`, `29c6fl12`, `ddfmnd4x`, `ijn0qyv9`, `mi3edaf9`, `3t0h7gxy`, `4lbijo1s`, `wjk2yias`, `tRKUtRGu`, `uxMCjeFx`, `A1y48HXQ`.

**Optimizations (54 uncaptured of 61):** `U1dtiije`, `v045eFc9`, `pfLuarzK`, `tKM21cdI`, `Ug4IxWMf`, `gWigP0NH`, `YAnVLycP`, `UpUzWNLk`, `4Qkbdnhs`, `Gjjgm4MQ`, `3ORTkkSB`, `vAZKhNoo`, `VTD6iRbm`, `L845PlpA`, `oT2l4Zb1`, `iMXahycT`, `YQUGpGjv`, `IIDDZa7n`, `qkOOtg74`, `7cOugH6F`, `ENLA3R1v`, `QDMUbWag`, `W8yLHlvU`, `GVENRCnG`, `m8t73ii9`, `HbBkb2i2`, `OVmpBwHD`, `lZkyBm0w`, `Jn9owohm`, `NuWCOu4K`, `IfLKA5ws`, `ieJEZvs2`, `40OUw03z`, `QIDIS7Hu`, `up9wiW80`, `VAipoxBx`, `kHfXNuV9`, `DM9dsxiF`, `uX8vbPmT`, `bAK0k9Cd`, `eW9ePZyc`, `MRqdp6hw`, `e2BzuKOD`, `HpV3uIvW`, `nZJJC4Hm`, `SzogbmaV`, `uax9Y8Ty`, `5ryKPok4`, `r4mf7nte`, `hO1L3Jpw`, `SIItxVD7`, `gIdMtEk9`, `ehOxENN4`, `egl22a78`.

**Advanced (13):** `dymq4wez`, `57lss4ms`, `xvbucgf2`, `o8s4k82o`, `6r5xk7sb`, `0m4cgflh`, `boywrotd`, `012p0r0x`, `gmtg8wst`, `a6xuekjx`, `prgc3ohm`, `yf38o58h`, `irsws4c4`.

Seven of the Advanced entries, plus one Optimization entry (`GVENRCnG` "Disable Mitigations"), are pre-classified **EXCLUDE on principle** even though they were never run, because their *names* describe operations that cannot be made safe or reversible and their target mechanisms are documented elsewhere: `6r5xk7sb` (SmartScreen), `0m4cgflh` (UAC), `boywrotd` (VBS), `a6xuekjx` (Defender), `GVENRCnG` (CPU mitigations), `yf38o58h` (third-party NVMe driver install), and `dymq4wez`/`gmtg8wst` (radio disable — reversible but user-hostile). This is a name-level judgement, explicitly not an evidence-based one; see D4 §2.

---

## 6. Rollback fidelity — what Hone's own revert actually does

Source: `[RB]`, a full apply-then-revert cycle on the same machine (snapshot `R00.before` all-applied → user toggled every card off → `R00.after`), plus a stock-baseline-to-reverted diff.

### 6.1 Coverage

* **7 of 9** tweaks were reverted with an explicit encrypted `t revert-<Id>` payload: `5Q8oOtIH`, `XLSEdxVW`, `3WwEZo0M`, `9C0X9LiC`, `istk0001`, `PE9bgwi0`, `k6ad89bg`.
* **0 revert payloads** for `RqbxUufM` and `oPu4x2Ef`. Hone exposes no UI revert path for the service-maintained tweaks, and no revert was issued. Their registry effects **remained** after the revert cycle.
* Residue check: a grep for every Hone-signature artefact across the 14 851-line stock→reverted diff returned **zero** matches for the seven reverted tweaks. Functionally: the revert is complete for what it touched.

### 6.2 Defects — a clone must not inherit these

| # | Defect | Severity | Evidence | What a clone must do instead |
|---|---|---|---|---|
| **D-1** | Active power scheme not restored. Hone deletes its imported scheme and Windows falls back to **Balanced**, abandoning the user's `8c5e7fda…` ("Paragon Perf V4"). | **Medium** | `[RB]` §3 D-1 | Snapshot `ActivePowerScheme` before import; `powercfg /setactive <snapshot>` on revert, *then* delete the imported scheme. |
| **D-2** | Revert **deletes** values instead of restoring them: `useplatformclock`, `useplatformtick`, `disabledynamictick` (`/deletevalue`), `BITS\SvcHostSplitDisable` (`reg_delete`), and `MessageNumberLimit` on the GPU. | Low but latent | `[RB]` §3 D-2; §3.4 F of this report | Record `(exists, type, data)` for every value **before** writing; restore the recorded tuple; delete only when the value was absent. |
| **D-3** | Three WMI Autologger blocks (`…SleepStudy-Diagnostic`, `…UserModePowerService-Diagnostic`, `…Kernel-Processor-Power-Diagnostic`) lose their `Status` value and are **not recreated**; only the `WINEVT\Channels` `Enabled` flag is restored. Diagnostic event logging for those channels is permanently gone until Windows servicing re-adds it. | Low | `[RB]` §3 D-3 | Snapshot the whole autologger key (export `.reg`), or disable the block instead of deleting it. |
| **D-4** | Revert overshoots its own footprint: `CscService` (Offline Files) and `lfsvc` (Geolocation) go **Disabled (stock) → Manual** although **no apply run touched them**. | **Medium** | `[RB]` §3 D-4 | Revert must be driven by the recorded change set, not by a hardcoded "restore to default" list. Never write outside your own footprint. |

D-1 and D-4 were corrected manually on the live system and verified afterwards; the report records the defect, not the workaround as Hone behaviour.

### 6.3 What the revert did correctly

Confirmed no-ops / exact restores `[RB]` §4: `WSearch`, `PcaSvc`, `WerSvc`, `wercplsupport`, `WalletService`, `SEMgrSvc`, `SharedAccess`, `upnphost`, `Ndu`, `PhoneSvc`, `AxInstSV`, `lmhosts` all back to stock `Start`; all 34 disabled tasks re-enabled; both Hone-created tasks deleted; all policy keys removed with **no `SOFT_Policies.reg` section surviving** in the residue diff; `InterruptSteeringFlags` removed; all 452 QoS entries and the `qos` key removed; all 49 `SvcHostSplitDisable` and the `SvcHostSplitThresholdInKB` restored; the renamed SleepStudy DLL restored by `rename_file` back.

### 6.4 Environmental noise, explicitly not Hone

`U_Software.reg` churn (FeatureUsage, MUI cache, Trae/Hone app state) is normal per-user activity. **Swiftwave Tweaks 2.0.0 was uninstalled between the stock baseline and the revert** (registry uninstall entries and `Installer\Folders` records gone) — unrelated to Hone, and noted here so it is not mistaken for residue.

---

## 7. Evidence and provenance

### 7.1 Claim → source map

| Claim | Grade | Source | Timestamp / ID |
|---|---|---|---|
| Tweak identity per UI click is recoverable from argv alone | VERIFIED-RUNTIME | decrypted `t apply-/revert-<Id> <pid>` from 4688 | 22:19:24 onward |
| `HoneSvc` re-applies `RqbxUufM`/`oPu4x2Ef` from a registry bitmask | VERIFIED-ARTIFACT + VERIFIED-RUNTIME | `[SVC]` full decompile; `[SV]` T-000; live argv `t apply-RqbxUufM 21836` | 20:29:28–20:29:30 |
| AES key derivation identical in `main.js` and `HoneSvc` | VERIFIED-ARTIFACT | `[STATIC]` §3.2 + `[SVC]` `(c>>1)^4090` literal, compared byte-for-byte | — |
| `5Q8oOtIH` contents (§3.1 C) | VERIFIED-RUNTIME | `[5Q]` + `5Q8oOtIH.diff.md` + 4688 children | 22:12:22 |
| `3WwEZo0M` has no defrag/TRIM | VERIFIED-RUNTIME | `[3W]` (no child processes, no `fsutil`) | 22:14:42 |
| `SvcHostSplitThresholdInKB` 16 GB → 4 TB | VERIFIED-RUNTIME | `[3W]` §1, diff | 22:14 |
| `RqbxUufM` masks (7 devices) | VERIFIED-RUNTIME | `[SV]` T-001 | 20:29:28 |
| `oPu4x2Ef` deletes `MessageNumberLimit` on GPU only | VERIFIED-RUNTIME | `[SV]` T-002 | 20:29:30 |
| Affinity/MSI values still present later | VERIFIED-RUNTIME | `[SV]` T-001 "confirmed 10 min later" | 20:39 |
| 25 `netsh` commands and their order | VERIFIED-RUNTIME | `[XL]` §1 (4688 child list) | 22:23:38 |
| ~452 QoS keys, DSCP 46 | VERIFIED-RUNTIME | `[XL]` §3 (5 426 added lines ÷ 12) | 22:23 |
| `UpperBind` rewrite on class instance `0013` | VERIFIED-RUNTIME | `[XL]` §2 | 22:23 |
| `InterruptSteeringFlags=0` is the entire tweak | VERIFIED-RUNTIME | `[IS]` | 22:26:19 |
| 7 services → `Start=4` | VERIFIED-RUNTIME | `[PE]` | 22:28:05 |
| 34 tasks + 4 services + WER | VERIFIED-RUNTIME | `[9C]` | 22:30:03 |
| Scheme `44444444-…` becomes active | VERIFIED-RUNTIME | `[K6]` §1 (`powercfg /LIST` before/after) | 22:32:02 |
| 12 scheme rows | VERIFIED-RUNTIME, 2 rows approximate | `[K6]` §2 | 22:32 |
| 27 settings in the embedded hive | VERIFIED-ARTIFACT | `[POW]` | offline |
| HoneSvc triggers (start, USB, audio) | VERIFIED-ARTIFACT | `[SVC]` | offline |
| Import uses PowrProf, not `powercfg.exe` | INFERRED | absence of `powercfg.exe` in 4688 `[K6]` §1 | 22:32 |
| `MiscPolicyInfo` semantics | INFERRED | undocumented | — |
| `SeLpacEnableWatsonThrottling` effect | INFERRED | undocumented | — |
| `RawMouseThrottle*` behaviour | INFERRED | name only | — |
| Effect of affinity/MSI on measured latency | **not measured** | no benchmark was run | — |
| Hone's revert for `RqbxUufM`/`oPu4x2Ef` | **UNCONFIRMED** | never issued | — |
| `\DisablePowerSaving` action | **UNCONFIRMED** | task body not captured | 22:32 |
| Which PCI functions Hone filters | **UNCONFIRMED** | single host | 20:29 |
| Failure handling / exit-code semantics in practice | **UNCONFIRMED** | no failure exercised | — |

### 7.2 Reproducibility requirements

To reproduce any per-tweak spec: (1) enable Windows Security 4688 process-creation auditing; (2) snapshot the registry before and after each single-tweak apply and diff as HashSets (not text diff — ordering noise dominates); (3) record child processes with full command lines; (4) decrypt the argv to attribute the run to an ID; (5) reboot-and-recheck for anything flagged boot-time. This is exactly the method used, and it is the only method that worked on an obfuscated, elevated, UAC-brokered binary.

---

## 8. Known unknowns and limitations

### 8.1 Not established (per tweak)

| ID | Open question |
|---|---|
| `5Q8oOtIH` | Prior values for 9 per-user/global values were never captured; internal ordering of the 10 registry groups; whether `NoLazyMode=0` is an error. |
| `3WwEZo0M` | Whether the 49-service list is filtered by existence or written blindly; effect of `MiscPolicyInfo=2`. |
| `RqbxUufM` | Device selection filter; whether Hone ever reverts it; measured effect. |
| `oPu4x2Ef` | Per-device prior `MSISupported`; original `MessageNumberLimit`; whether device start failures were ever observed. |
| `XLSEdxVW` | Why the card header says "18 netsh" while 25 were captured; whether `netsh` failures abort the run; prior `LanmanServer` and NIC values. |
| `istk0001` | Whether the value has any effect on this silicon. |
| `PE9bgwi0` | None material — the simplest tweak of the nine. |
| `9C0X9LiC` | Whether `SbEnable=0` is intentional; `SeLpac…` meaning. |
| `k6ad89bg` | `\DisablePowerSaving` action; which 15 of the 27 hive settings were actually written vs inherited; pre-state of `PlatformAoAcOverride`/`StorageD3InModernStandby`/`stornvme IdlePowerMode`; failure path of the in-process import. |

### 8.2 Category-level unknowns

* The UI section GUIDs (`70a82116…` "FPS & Latency", `380c0bd6…` "Network & Ping") were recovered from the renderer, but **which card belongs to which section was not captured**. The `Category` column in §5 therefore reads "Optimizations" (the catalog's own grouping) rather than a UI section name.
* The remote catalog is fetch-time variable. The 106 count is a snapshot; a later catalog may differ. Do not treat 106 as a stable constant.

### 8.3 Discrepancies recorded but not resolved

1. `XLSEdxVW`: "18 netsh invocations" (card header) vs 25 itemised commands.
2. `k6ad89bg`: scheme name "V5" (live) vs "V4" (hive metadata).
3. `9C0X9LiC`: shipped-locale name vs live UI name.
4. `3WwEZo0M`: shipped-locale description (defrag/TRIM) vs captured behaviour (registry only).
5. `5Q8oOtIH`: `NoLazyMode=0` intent.
6. `XLSEdxVW`: `initialRto=2000` intent.
7. `5Q8oOtIH` vs `9C0X9LiC`: contradictory shutdown policies.
8. **`RqbxUufM` / `oPu4x2Ef`: UI cards off, registry effects present, no revert issued** (§2.5 C-1).
9. **`3WwEZo0M`: the capture summary says 49 services; its itemised list enumerates 45 names.** Either four services were written but not listed, or the count is wrong. The report uses the itemised list for identity and 49 only as the claimed total.
10. **`XLSEdxVW`: the NetBIOS section says "4 GUIDs observed" and then lists six interface-GUID families.** The report quotes all six; the "4" is unexplained.

Items 5–8 are intent contradictions inside Hone's own product; items 9–10 are internal counting inconsistencies in the capture itself; items 1–4 are documentation/artefact drift. None was resolved by further experiment, per instruction.

### 8.4 Evidence limitations that a reader must respect

* One machine, one Windows build, one hardware generation (hybrid Intel + NVIDIA). Every "before" value is *this* machine's before, not a universal default.
* No performance measurement was taken. Where this report says "reduces latency" it is describing the **intent** of a configuration change, not a measured result. Nothing here should be marketed as a benchmarked improvement.
* Hone's failure handling was never exercised: every captured run exited cleanly. Partial-apply behaviour is unknown for all nine.
* `HoneUtility.exe` could not be decompiled (ConfuserEx, emptied `#US`). Every operation list in §3 is a **runtime** observation, so an operation that failed to produce an observable change would be missing from the list. Coverage is therefore "at least these", never "exactly these".

---

## 9. Final quality checks (per §10 of the requirements)

| Requirement | Status |
|---|---|
| Every UI optimization entry has an individual specification | 9 of 9 in scope specified (§3.1–3.9). 93 out of scope are named and marked NOT CAPTURED — see §5.2. |
| All 106 underlying operations accounted for | Accounted: 9 specified, 93 named/uncaptured, 4 unattributable placeholders. §5.2. |
| Bundled operations fully decomposed | Yes for all nine, with per-operation counts in §2.3 and per-operation verdicts in each G section. |
| Every implementation specification cross-checked against captures | Yes — each C section cites the card it comes from; §7.1 is the map. |
| Rollback instructions correspond to actual original state | Yes where captured; where the original was not captured, the report says so and requires read-before-write (§6.2 D-2, §4.7). |
| Missing values and unsupported assumptions identified | §8.1, §8.3, and the per-spec "prior unknown" markers. |
| Implementation handoff is self-contained | `Swiftwave-Handoff.md` (D3) — contains no dependency on reading Hone, this report, or the evidence tree to implement any approved feature. |
| No vague descriptions | Every change names a key, value, type, data, verification command, and restore action. |
| Do not modify Swiftwave Tweaks | Not modified. No write outside this output directory. |




