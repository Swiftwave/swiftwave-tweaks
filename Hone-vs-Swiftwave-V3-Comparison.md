# Hone vs Swiftwave Tweaks V3 — Operation-Level Technical Comparison

Date: 2026-11-11
Scope: the nine original Hone optimizations and their Swiftwave Tweaks V3 counterparts, compared by
the **system operations actually performed**, not by feature names.

Evidence legend used in every "Verification status" cell:

| Code | Meaning |
|---|---|
| `V-SYN` | Exercised by the headless self-test with a synthetic topology / fixture. |
| `V-UNIT` | Exercised by the registry apply→restore fixture and journal tests (real OS calls). |
| `V-INV` | Operation inventory cross-checked against the spec/report; the code path was not run on this host. |
| `V-BLK` | Could not be exercised on this host (unelevated harness / hardware absent). |
| `V-N/A` | Operation not present in Swiftwave. |

---

## 0. Feature mapping

| # | Original Hone optimization | Swiftwave V3 optimization | Relationship |
|---|---|---|---|
| F1 | `5Q8oOtIH` — Optimize FPS & Input Lag | `v3-fps-input-lag` — FPS & input lag optimization | Replacement of a prior Swiftwave feature, reduced to the safe subset |
| F2 | `3WwEZo0M` — Optimize Storage & Memory | `v3-storage-memory` — Consolidate service hosts | Replacement, made hardware-gated |
| F3 | `RqbxUufM` — PCI Device Affinities | `v3-device-affinity` — Device affinity | Replacement; hardcoded CPUs removed |
| F4 | `oPu4x2Ef` — Enable MSI Mode | `v3-msi` — Message-signalled interrupts | Replacement; split from the combined PCI class |
| F5 | `XLSEdxVW` — Optimize Network Performance | `v3-network-performance` — Network performance optimization | Replacement, reduced to the safe subset |
| F6 | `istk0001` — Disable Dynamic Interrupt Steering | `v3-dynamic-interrupt-steering` — Disable interrupt steering | Reproduced |
| F7 | `PE9bgwi0` — Disable Windows Services | **merged into** `debloat` | Merged feature |
| F8 | `9C0X9LiC` — Reduce Windows Background Activity | **merged into** `debloat` | Merged feature |
| F9 | `k6ad89bg` — Maximum Performance Power Mode | `v3-max-performance-power` — Swiftwave power plan | Replacement; desktop/laptop split |

The merged card is **"Debloat Windows & Reduce Background Activity"** (`debloat`). The legacy
`windows-suggestions` card is retained with `Category = "Legacy"` only so pre-merge history records
still resolve.

---

## 1. F1 — FPS & input lag

### A. Identification
- Hone: `5Q8oOtIH`, "Optimize FPS & Input Lag".
- Swiftwave: `v3-fps-input-lag`, "FPS & input lag optimization".
- Relationship: replacement of the prior Swiftwave feature; Hone's undocumented half is removed.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| BCD `useplatformclock` | Delete unconditionally | Delete only when present | An unconditional delete returns exit code 1 on machines where the value is already absent and aborts the whole apply | `V-INV` |
| BCD `useplatformtick` | Delete unconditionally | Delete only when present | Same as above | `V-INV` |
| BCD `disabledynamictick` | Set `yes` (opt-in) | Not applied | Timing-source change with no measurable benefit for the target workload; kept out of the default profile | `V-N/A` |
| `HKLM\SYSTEM\CurrentControlSet\Control\WaitToKillServiceTimeout` | REG_SZ `"1500"` | REG_SZ `"1500"` | Same operation; prior type captured | `V-INV` |
| `…\Control\WaitToKillAppTimeout` | REG_SZ `"1500"` | REG_SZ `"1500"` | Same | `V-INV` |
| `…\Control\Session Manager\kernel` ten undocumented values (`DisablePagingExecutive`, `LargeSystemCache`, `DistributedTimersEnabled`, `DpcWatchdogProfile*`, `IoPageLockLimit`, …) | Written | Not written | Undocumented kernel internals; different Windows builds have different safety envelopes | `V-N/A` |
| `RawMouse*` (4 values) | Written | Not written | Input-stack internals with contradictory guidance; no verified user-visible benefit | `V-N/A` |
| `NoLazyMode`, `AllowDeepCStates` | Written | Not written | Power/scheduler internals; conflicts with the power-plan feature | `V-N/A` |
| `HKCU\Software\Microsoft\directx\graphicssettings` `SwapEffectUpgradeEnable` | `1` (inside a `DirectXUserGlobalSettings` string) | REG_DWORD `1` | Equivalent effective setting; DWORD avoids rewriting the whole string and losing sibling values | `V-INV` |
| same key `SwapEffectUpgradeCache` | `1` | REG_DWORD `1` | Same | `V-INV` |
| same key VRR optimisation | `0` | Not written | Disabling VRR optimisation degrades variable-refresh displays | `V-N/A` |
| `HKCU\…\Explorer\Serialize\StartupDelayInMSec` | `0` | `0` | Same | `V-INV` |
| `HKCU\…\Explorer\Serialize\WaitforIdleState` | `0` | `0` | Same | `V-INV` |
| `HKCU\…\Dsh\IsPrelaunchEnabled` | `0` | `0` | Same | `V-INV` |
| `HKCU\Software\Microsoft\input\IsInputAppPreloadEnabled` | `0` | `0` | Same | `V-INV` |
| `HKCU\Control Panel\Desktop\HungAppTimeout` | `"1000"` | `"1000"` | Same | `V-INV` |
| `…\Desktop\WaitToKillAppTimeout` | `"1500"` | `"1500"` | Same | `V-INV` |
| `…\Desktop\LowLevelHooksTimeout` | `"1000"` | `"1000"` | Same | `V-INV` |
| `…\Desktop\AutoEndTasks` | `1` | Not written | Forcing app termination silently discards unsaved work; data-loss vector | `V-N/A` |
| Services `DcomLaunch`, `RpcEptMapper`, `RpcSs`, `Schedule`, `SystemEventsBroker` | Forced to Automatic | Not written; treated as verify-only | They are already Automatic on stock Windows; writing them adds risk with no state change | `V-INV` |
| Task `\LaunchSetTimerResolution` | Created, runs a helper for 0.5 ms timer | Not created | Swiftwave does not ship Hone's helper binary; creating a task pointing at a missing binary is worse than omitting it | `V-N/A` |
| `GlobalUserDisabled` + `appprivacy` policy | Written | Not written | Privacy-policy surface, not an FPS/input operation | `V-N/A` |
| `AllowAutoGameMode` | Written | Not written | Game Mode is already on by default; the policy form is unsupported on several builds | `V-N/A` |

### C. Classification
- **Reproduced:** registry timeouts, serialisation pair, Dsh/input prelaunch, desktop timeouts.
- **Modified:** BCD deletes made conditional; DirectX values written as DWORDs.
- **Excluded intentionally:** `disabledynamictick`, the ten kernel tunables, `RawMouse*`,
  `NoLazyMode`/`AllowDeepCStates`, VRR=0, `AutoEndTasks`, the timer task, privacy/Game Mode policy.
  Reasons: undocumented internals, data loss, no verified benefit, or a missing helper binary.
- **Partially implemented:** the five services are acknowledged but not written (already default).

---

## 2. F2 — Storage & memory (service-host consolidation)

### A. Identification
- Hone: `3WwEZo0M`, "Optimize Storage & Memory".
- Swiftwave: `v3-storage-memory`, "Consolidate service hosts".
- Relationship: replacement, now gated on hardware.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Control\SvcHostSplitThresholdInKB` | `0xFFFFFFFF` | `0xFFFFFFFF` when gate open; L1 also writes it | Same value; gate decides whether the rest follows | `V-INV` |
| Per-service `Services\<name>\SvcHostSplitDisable` | `1` for a fixed 49-service list | `1` for services discovered at run time that (a) match a user-service base name and (b) actually have `ImagePath = …svchost.exe…` | Device-adaptive: never touches third-party or absent services | `V-INV` |
| Hardware gate | None | Requires ≥ 4 logical processors **and** ≥ 8 GiB RAM; otherwise L1 (threshold only) | Consolidation increases fault domains; small machines should not join many services | `V-INV` |
| `…\Control\FileSystem\NtfsDisable8dot3NameCreation` | `1` | `1` | Same | `V-INV` |
| `…\FileSystem\NtfsDisableLastAccessUpdate` | `1` | `1` | Same | `V-INV` |
| `…\FileSystem\NtfsMemoryUsage` | `2` | `2` | Same | `V-INV` |
| `…\FileSystem\ScrubMode` | `1` | `1` | Same | `V-INV` |
| `…\FileSystem\NtfsMftZoneReservation` | `1` | Not written | Reserves ~25 % of the volume for the MFT; opt-in only, default off | `V-N/A` |
| `…\FileSystem\NtfsAllowExtendedCharacterIn8dot3Name` | `0` | Not written | Dead once 8.3 name generation is off | `V-N/A` |
| `ReserveManager` registry values | Written directly | Not written | Reserved storage is owned by the servicing stack; the supported path is `DISM /Set-ReservedStorageState` | `V-N/A` |

### C. Classification
- **Reproduced:** threshold, four NTFS values.
- **Device-adaptive:** the `SvcHostSplitDisable` set and the L1/L2 gate.
- **Excluded intentionally:** MFT zone, extended-character 8.3, raw ReserveManager writes.

---

## 3. F3 — PCI device affinities

### A. Identification
- Hone: `RqbxUufM`, "PCI Device Affinities".
- Swiftwave: `v3-device-affinity`, "Device affinity".
- Relationship: replacement. The hardcoded CPU 2+3 assignment is gone.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| Device enumeration | Fixed list of PCI instances | `Win32_PnPEntity` query per run, class-filtered (Display, SCSIAdapter, HDC, Net, System) | Device discovery instead of assuming instance paths | `V-INV` |
| Eligibility | Any subkey with an `Affinity Policy` key | Pre-existing `Affinity Policy` key **and** class allowed **and** not a boot display **and** not a hard-denied device | Boot display GPUs must keep group-wide affinity or the panel can go dark | `V-INV` |
| `DevicePolicy` | `4` | `4` | Same | `V-INV` |
| `DevicePriority` | `3` | `3`, primary device only | Lower-priority devices do not need a priority boost | `V-INV` |
| `AssignmentSetOverride` | Fixed mask for CPU `2+3` | Topology-derived 2-bit mask, 8-byte LE, never CPU 0, prefers distinct LLCs | E-1/E-2/E-3 and E-GRP-GT64 enforced before any write | `V-SYN` (mask), `V-INV` (write) |
| Processor groups | Ignored | `E-GRP-GT64` when `Groups != 1` or LP > 64 | Prevents a group-0-only mask for a target in another group | `V-SYN` |
| > 64 logical processors | Ignored | Refused with `E-GRP-GT64` | Same | `V-SYN` |
| Post-write health | None | `QueryOne` health assertion per device | A device that enters a config-error state is rolled back | `V-INV` |
| Per-device failure | All-or-nothing | Per-device rollback + `E-PARTIAL-FAILURE` | One bad device cannot leave the rest half-configured | `V-UNIT` (engine) |
| Ownership | None | Journal target per device value | Rebuildable ownership prevents two features writing the same value | `V-UNIT` |

### C. Classification
- **Reproduced:** `DevicePolicy`, `DevicePriority`, `AssignmentSetOverride`.
- **Device-adaptive:** device discovery, class eligibility, mask derivation, group handling.
- **Modified:** primary-only priority; boot-display exclusion; health assertion.

---

## 4. F4 — MSI mode

### A. Identification
- Hone: `oPu4x2Ef`, "Enable MSI Mode".
- Swiftwave: `v3-msi`, "Message-signalled interrupts".
- Relationship: replacement; split out of the former combined PCI class.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| Device selection | Every device with an MSI key | GPU, storage and NIC classes only | Enabling MSI on unrelated devices is unverifiable and occasionally harmful | `V-INV` |
| `MessageSignaledInterruptProperties\MSISupported` | `1` everywhere | `1` only where the MSI key already exists | Never fabricates an MSI key on a device that has none | `V-INV` |
| `MessageNumberLimit` | Deleted for all devices | Deleted for the primary GPU only | Other devices keep their vendor-configured limit | `V-INV` |
| Boot display | Not checked | Skipped with `E-DEV-BOOT-DISPLAY` | Prevents losing the display path | `V-INV` |
| Post-write health | None | Health assertion + per-device rollback | Same rationale as F3 | `V-INV` |
| Registry type | Not preserved | Prior kind + data captured | `REG_SZ` vs `REG_DWORD` is restored exactly | `V-UNIT` |

### C. Classification
- **Reproduced:** `MSISupported=1`.
- **Modified:** scoped device classes; primary-only `MessageNumberLimit`; health assertions.
- **Device-adaptive:** MSI-key existence check.

---

## 5. F5 — Network performance

### A. Identification
- Hone: `XLSEdxVW`, "Optimize Network Performance".
- Swiftwave: `v3-network-performance`, "Network performance optimization".
- Relationship: replacement; reduced to the safe subset.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| `netsh int tcp set global rss=enabled` | Yes | Yes | Same | `V-INV` |
| `netsh int tcp set global nonsackrttresiliency=disabled` | Yes | Yes | Same | `V-INV` |
| `netsh int ip set global taskoffload=enabled` | Yes | Yes | Same | `V-INV` |
| `netsh int tcp set global netdma=disabled` | Yes | Yes | Same | `V-INV` |
| `netsh int tcp set global dca=disabled` | Yes | Yes | Same | `V-INV` |
| `netsh int udp set global uro=disabled` | Yes | Yes | Same | `V-INV` |
| `netsh int udp set global uso=enabled` | Yes | Yes | Same | `V-INV` |
| `netsh int ipv4/ipv6 set global loopbacklargemtu=disabled` | Yes | Yes | Same | `V-INV` |
| `netsh int isatap set state disabled` | Yes | Yes | Same | `V-INV` |
| Congestion provider → `bbr2` | Set unconditionally | Set only if `netsh int tcp show supplemental` reports `bbr2`; otherwise left unchanged and annotated `E-BBR2-UNSUPPORTED` | Device-adaptive; older builds reject the command and abort the apply | `V-INV` |
| `…\Services\Tcpip\Parameters` nine values (`DisableTaskOffload=0`, `EnableTCPChimney=0`, `EnableTCPA=0`, `EnableDCA=0`, `TcpMaxConnectRetransmissions=2`, `TcpTimedWaitDelay=32`, `MultihopSets=0x0F`, `SackOpts=1`, `Tcp1323Opts=3`) | Written | Written | Same values, typed capture | `V-INV` |
| `…\Services\Tcpip6\Parameters` four offload values + `DisabledComponents=1` | Written | Written | Same. `DisabledComponents=1` disables tunnel interfaces only, native IPv6 stays up | `V-INV` |
| `…\Services\Dnscache\Parameters` `NegativeCacheTime`, `NegativeSOACacheTime`, `NetFailureCacheTime` | `0` | `0` | Same | `V-INV` |
| `…\Dnscache\Parameters\EnableAutoDoh` | `2` | Not written | Forces DoH for every app and can break split-horizon DNS | `V-N/A` |
| `…\Services\Psched\TimerResolution` | `10000` | Not written | Needs validation against the power-plan timer settings | `V-N/A` |
| `…\Services\lmhosts\Start` | `4` | Not written | Legacy-LAN opt-in only | `V-N/A` |
| `…\Services\iphlpsvc\config\IsatapState` | `3` | `3` | Same | `V-INV` |
| `…\Services\Tcpip\qos\Do not use NLA` | `"1"` | `"1"` | Same | `V-INV` |
| `UpperBind` rewrite | Yes | Not written | Destructive to the bind stack and not reliably reversible | `V-N/A` |
| `…\Tcpip\Parameters\initialRto` | `2000` | Not written | Doubles the first retransmit; contradicts the latency goal | `V-N/A` |
| NIC `NetbiosOptions` | `2` | Not written | Breaks NetBIOS-dependent legacy apps and name discovery | `V-N/A` |
| `BITS\SvcHostSplitDisable` | `0` | Not written | Directly conflicts with F2, which sets `1` on eligible services (C2-1) | `V-N/A` |
| QoS DSCP policies (~452 entries) | Created | Not written | No measurable consumer benefit; large registry footprint | `V-N/A` |
| NIC power-save block (`*EEE`, `EnableGreenEthernet`, `MIMOPowerSaveMode`, `PnPCapabilities`) | Applied to fixed instance `0001` | Applied per physical adapter resolved by `DriverDesc` + `NetCfgInstanceId`; virtual/Hyper-V/loopback/WAN-miniport skipped | Device-adaptive; never assumes the numeric instance index | `V-INV` |
| `netsh int ipv4 set interface <name> currenthoplimit=64` | Yes | Not written | Interface name is machine-specific; a wrong name fails the apply | `V-N/A` |

### C. Classification
- **Reproduced:** the ten documented netsh globals, the nine TCP values, the five IPv6 values,
  DNS negative cache, `IsatapState`, QoS NLA string, NIC power-save block.
- **Device-adaptive:** bbr2 probe; per-adapter NIC resolution.
- **Excluded intentionally:** `EnableAutoDoh`, `Psched\TimerResolution`, `lmhosts`,
  `UpperBind`, `initialRto`, `NetbiosOptions`, BITS conflict, DSCP flood, per-interface hop limit.
- **Modified:** NIC block applied per adapter instead of a fixed instance.

---

## 6. F6 — Dynamic interrupt steering

### A. Identification
- Hone: `istk0001`, "Disable Dynamic Interrupt Steering".
- Swiftwave: `v3-dynamic-interrupt-steering`, "Disable interrupt steering".
- Relationship: reproduced.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\InterruptSteeringFlags` | `0` | `0` | Same | `V-BLK` (host unelevated; engine verified by fixture) |
| Build gate | None | Requires build ≥ 19041, else `E-OS-UNSUPPORTED` | The value is not honoured on older kernels | `V-INV` |
| VM gate | None | `E-VM` on virtual machines | No physical interrupt steering to disable | `V-INV` |
| State detection | Read live value | Journal-only (C3/C4) | Matching value without a transaction is reported Unapplied | `V-UNIT` |

### C. Classification
- **Reproduced:** the single registry value.
- **Modified:** journal-only state; explicit build/VM gates.

---

## 7. F7 — Disable Windows services (merged)

### A. Identification
- Hone: `PE9bgwi0`, "Disable Windows Services".
- Swiftwave: merged into `debloat` — "Debloat Windows & Reduce Background Activity".
- Relationship: merged feature (not a standalone card).

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| `Services\WalletService\Start` | `4` | `4` | Same | `V-INV` |
| `Services\SEMgrSvc\Start` | `4` | `4` | Same | `V-INV` |
| `Services\PhoneSvc\Start` | `4` | `4` | Same | `V-INV` |
| `Services\AxInstSV\Start` | `4` | `4` | Same | `V-INV` |
| `Services\Ndu\Start` | `4` | `4` | Same | `V-INV` |
| `Services\PcaSvc\Start` | Not in F7 (came from F8) | `4` | Merged list adds it once | `V-INV` |
| `Services\WerSvc\Start`, `Services\wercplsupport\Start` | Not in F7 (came from F8) | `4` | Merged list | `V-INV` |
| Absent service | Would create the key | Skipped; no journal target | Prevents phantom `Services\<name>` keys | `V-UNIT` |
| Ownership | None | Journal; legacy `windows-suggestions` record retired on apply | One owner per target | `V-UNIT` |

### C. Classification
- **Reproduced:** the five F7 services (now part of the merged list).
- **Modified:** service writes are skipped when the service key is absent.
- **Excluded intentionally:** WSearch, SharedAccess, upnphost, RemoteRegistry, TrkWks,
  WMPNetworkSvc, icssvc — each has dependent functionality that breaks silently.

---

## 8. F8 — Reduce Windows background activity (merged)

### A. Identification
- Hone: `9C0X9LiC`, "Reduce Windows Background Activity".
- Swiftwave: merged into `debloat`.
- Relationship: merged feature.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| Scheduled tasks (Hone's background-activity set) | Disabled individually by full path | 33 full task paths disabled in one batched pass | Same effective operation; batched so Detect does not spawn 33 processes | `V-INV` |
| WER / AppCompat / device-install registry values | Written | 13 values written | Same set, typed capture | `V-INV` |
| `WSearch` | Disabled | Not disabled | Breaks Start-menu search and Outlook indexing | `V-N/A` |
| `SharedAccess`, `upnphost` | Disabled | Not disabled | Breaks Internet Connection Sharing and UPnP discovery | `V-N/A` |
| `RemoteRegistry`, `TrkWks` | Disabled | Not disabled | Breaks remote management and distributed link tracking | `V-N/A` |
| `WMPNetworkSvc`, `icssvc` | Disabled | Not disabled | Breaks media sharing and mobile hotspot | `V-N/A` |
| Existing promotional-content card (9 HKCU CDM values) | Separate Swiftwave feature | Folded into the same card; legacy prior adopted | Avoids duplicated operations and preserves the true original values | `V-UNIT` (ownership) |

### C. Classification
- **Reproduced:** task disables, WER/AppCompat values, the nine CDM values.
- **Modified:** single card, batched detection, legacy-prior adoption.
- **Excluded intentionally:** the seven dependent services listed above.

---

## 9. F9 — Maximum performance power mode

### A. Identification
- Hone: `k6ad89bg`, "Maximum Performance Power Mode".
- Swiftwave: `v3-max-performance-power`, "Swiftwave power plan".
- Relationship: replacement. Reconstructed settings are discarded in favour of importing the
  supplied plan.

### B. Operation-by-operation

| System operation | Original Hone behaviour | Swiftwave V3 behaviour | Difference / justification | Verification status |
|---|---|---|---|---|
| Plan creation (desktop) | Registry reconstruction from the Hone findings, then per-setting writes | `powercfg /import Assets\Swiftwave-Ultimate-Performance.pow` (no GUID) | Uses the supplied artifact verbatim; hidden settings survive because nothing is reconstructed | `V-INV` |
| New plan GUID discovery | Assumed | `/list` diff before/after import | Never guesses a GUID | `V-INV` |
| Rename | Registry `FriendlyName` write | `powercfg /changename` → `Swiftwave Ultimate Performance` | Supported API; no locale parsing | `V-INV` |
| Per-setting writes (~90 `setacvalueindex`/`setdcvalueindex`) | Written one by one | Not written; configuration comes from the imported blob | The blob is the source of truth; drift is impossible | `V-INV` |
| Content verification | None | 27 AC/DC rows from the supplied comparison CSV are read back after import | Proves the import actually landed | `V-INV` |
| Activation | `powercfg /setactive` | `powercfg /setactive` after verification | Same, but only after verification | `V-INV` |
| Laptop plan | Same plan as desktop | `/duplicatescheme 8c5e7fda-…` → rename `Swiftwave High Performance` | Laptop thermals do not tolerate the desktop plan; the canonical Windows scheme is untouched | `V-INV` |
| Form-factor detection | None | Battery query + chassis types; `E-CLASS-AMBIGUOUS` when neither is conclusive | Fails closed instead of guessing | `V-INV` |
| Rollback — active scheme (D-1) | Not captured | Previously active GUID captured; re-activated only if our scheme is still active | Fixes D-1; an external scheme change is respected | `V-INV` |
| Rollback — delete (D-2) | Deletes the created plan | Deletes only the Swiftwave-owned GUID | Fixes D-2; fails to restore and then deletes is no longer possible | `V-INV` |
| Hidden settings | Reconstructed subset | Preserved by import | No loss of vendor/hidden settings | `V-INV` |

### C. Classification
- **Reproduced:** plan activation, rename intent.
- **Modified:** import instead of reconstruction; verification before activation.
- **Device-adaptive:** desktop vs laptop; form-factor ambiguity.
- **Not implemented:** none of the recovered settings are dropped — they are carried inside the
  imported plan.

---

## D. Hardware and environmental differences

| Area | Hone approach | Swiftwave V3 | Is it genuinely adaptive? |
|---|---|---|---|
| CPU topology | Assumed; affinity fixed to CPU 2+3 | `GetLogicalProcessorInformationEx` + CPU sets; cores, SMT siblings, groups, LLC domains | Yes — exercised with synthetic and live topologies |
| Hybrid classification | Not attempted | CPUID leaf `0x1A` sampled per LP, cross-checked against EfficiencyClass; no `== 0` assumption | Yes — tested for calibrated, inverted and ambiguous cases |
| Processor groups | Ignored | Single group and ≤ 64 LP required; `E-GRP-GT64` otherwise | Yes — tested |
| PCI device discovery | Fixed instance list | WMI PnP query with class allow-list and hard-deny IDs | Yes in principle; not exercised against real devices |
| GPU / NIC eligibility | Every device | Boot display excluded; virtual/Hyper-V/loopback/WAN miniports excluded | Yes in principle; not exercised against real devices |
| Service discovery | Fixed 49-service list | Run-time enumeration requiring a real `svchost.exe` `ImagePath` | Yes — not yet run against a non-stock image |
| Laptop vs desktop power | Same plan | Desktop import vs laptop duplication, with ambiguity fail-closed | Yes in design; not exercised on a laptop here |
| Windows compatibility | Value writes only | Build gates on F6, class gates on F3/F4, `bbr2` probe on F5 | Partly — the probe is real, the others are version gates |

Distinguishing tested from assumed: **tested** = topology, hybrid calibration, group gates, journal,
registry fidelity, partial-failure recovery. **Assumed** = real PCI health assertions, real NIC
instance resolution, real laptop plan duplication, desktop `.pow` import/activation.

---

## E. Verification and rollback comparison

| Capability | Hone | Swiftwave V3 |
|---|---|---|
| Original-state capture | Partial; some values captured, some assumed default | Every target captured with kind + data; absent targets recorded as absent |
| Applied-state detection | Live value comparison only | Journal-only state (C3/C4) plus live read-back |
| Ownership tracking | None | Append-only journal with per-target ownership; legacy-record transfer |
| Verification | Minimal | Independent `DetectAfterApply`; failure triggers automatic revert |
| Partial failure | Left half-applied | Applied operations undone in reverse; `E-PARTIAL-FAILURE` reported |
| Restoration accuracy | Approximate | Typed restore (DWord/QWord/String/ExpandString/Binary/MultiString), or delete when absent |
| Cross-feature conflicts | Not detected | Target ownership + conflict rules (C1/C2) |
| Reboot handling | None | `RequiresRestart` surfaced for BCD/power changes; no state is claimed as verified before reboot where a reboot is required |

### D-1 through D-4

| Defect | Hone behaviour | Swiftwave mitigation |
|---|---|---|
| **D-1** | Revert did not restore the previously active power scheme | `CapturePrevious` records the active GUID; rollback re-activates it, and only if the Swiftwave plan is still active |
| **D-2** | Revert deleted objects instead of restoring prior values (services, BITS, task states) | Every operation stores `PriorKind` + `PriorData`; restore writes the exact prior value, or deletes only if it was absent |
| **D-3** | Revert left WMI Autologger sessions created during apply | Swiftwave never creates WMI Autologger sessions; nothing of that kind is in any operation list |
| **D-4** | Revert overshot its footprint, changing values it never set | Journal targets are exactly the operations applied; skipped/absent targets are never journaled and never touched on revert |

---

## F. Performance and functional evidence

### F.1 Implemented
All nine Hone optimizations have a Swiftwave V3 counterpart (F3/F4 split, F7/F8 merged into
`debloat`, F1/F2/F5/F6/F9 standalone). Every code path builds in Debug and Release with
0 warnings / 0 errors.

### F.2 Verified by automated test (45/45 passing)
- Topology gates: supported desktop, supported hybrid, unreadable topology, 1-core degenerate,
  2-LP degenerate, ambiguous hybrid, two groups, 128 LP.
- Live host: 1 group, 16 LP, 12 cores, hybrid, perfClass 1 → mask `0x14` (LPs 2 and 4).
- Journal semantics: Optimized, `E-FOREIGN-EDIT`, Unapplied-without-transaction (C4), rolled back,
  empty transaction.
- Registry engine: DWORD, `REG_EXPAND_SZ`, REG_BINARY, absent value; two full apply/restore cycles;
  exact kinds and data restored.
- Absent-service skip; no phantom service key.
- Partial failure: reverse undo, `E-PARTIAL-FAILURE`, no rollback record.
- Conflict C1: second apply returns `AlreadyOptimized`.

### F.3 Implemented but not runtime-exercised here
- F1, F2, F5, F6, F9: HKLM/power writes require an elevated packaged run.
- F3, F4: real device affinity/MSI writes and post-write health assertions.
- F5: real NIC instance resolution and inverse-netsh restore.
- F9: `.pow` import, 27-row verification, activation and D-1 restore.
- Debloat: service/task writes and legacy-prior adoption against a real `windows-suggestions`
  rollback record.

### F.4 Not tested across
- A real laptop (power-plan duplication path).
- A real multi-group or > 64-LP machine (gates are synthetic-tested only).
- A non-Intel hybrid CPU (expected to fail closed with `E-HYBRID-AMBIGUOUS`).
- External configuration changes during apply (the read-back and `E-FOREIGN-EDIT` path is tested at
  the engine level, not as a race).

### F.5 Readiness statement
The backend corrections required before UI integration are complete and the rollback engine is
demonstrably faithful for the operation kinds it supports. F3/F4 rollback safeguards remain in
place, and no UI integration has been started. The remaining gap is a packaged elevated
apply/restore run across all nine features, which is listed in section 4 of
`V3-Correction-Report.md`.
