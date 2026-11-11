# Swiftwave Tweaks — Comprehensive Engineering Audit Report

**Audit date:** 2026-11-10
**Audited version:** 2.0.0 (csproj `GameReadyOptimizer.csproj`, assembly `SwiftwaveTweaks`)
**Audit type:** Static inspection-only. No code modified, no optimizations executed, no Windows settings changed, nothing published.
**Method:** Every C#/XAML source file, every PowerShell script, and all build/packaging/CI files were read in full. Build artifacts under `bin/` and `obj/` (991 files) and prebuilt binaries under `release/`, `release-assets/`, `store-artifacts/`, `store/screenshots/` were inventoried but not decompiled — see exclusions in §2. Runtime testing was **not possible** (no .NET SDK on the audit machine) — see §11.

---

## 1. Executive Summary

Swiftwave Tweaks is a **.NET 8 WPF desktop utility for Windows 11 (x64)** that presents a curated set of gaming/performance configuration changes and applies them through a disciplined **Detect → Capture previous state → Apply → Independently verify → Record rollback** lifecycle. It is the v2 rename of an earlier "GameReady Optimizer" project (the csproj file keeps the old name; assembly, namespaces, and all user-visible branding are `SwiftwaveTweaks`/`Swiftwave Tweaks`).

**Architecture.** It is a compact single-project WPF app (~30 source files, ~120 KB of C#). There is **no dependency-injection container, no MVVM framework, and no external UI framework**. The "architecture" is deliberate and consistent: one `MainWindow` code-behind builds every page programmatically (no per-page XAML), a static `OptimizationCatalog` instantiates `Optimization` subclasses, and a shared abstract base class (`Core/Optimization.cs`) enforces the apply/verify/rollback protocol for all 15+ optimizations. Persistence is three JSON files plus registry under `%LOCALAPPDATA%\SwiftwaveTweaks\`. The app runs `asInvoker` and elevates *per-operation* by spawning UAC-elevated PowerShell child processes with base64-encoded scripts, then verifies outcomes by re-reading the system.

**Engineering strengths (verified in code, not just claimed):**

- The base `Optimization.Apply()` is genuinely disciplined: it refuses to apply when detection says Manual/Unsupported, captures previous state first, applies, **independently re-detects**, and on failed verification attempts an automatic revert before reporting failure ([Core/Optimization.cs](Core/Optimization.cs) lines 42–93).
- Rollback is **absence-aware** for registry toggles: if a value did not exist before, revert deletes it rather than writing a guessed default ([GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) `HkcuDwordOptimization.Revert`, lines 49–64).
- The NVIDIA optimization captures *both* the previous value and whether it was a driver-predefined default, and revert restores the exact prior condition ([NvidiaPowerManagementOptimization.cs](Optimizations/NvidiaPowerManagementOptimization.cs), [NvidiaDrsService.cs](Services/NvidiaDrsService.cs)).
- Network revert restores each keyword to its exact captured value and re-reads to confirm ([NetworkPowerSavingOptimization.cs](Optimizations/NetworkPowerSavingOptimization.cs) lines 88–130).
- Page loads run detection off the UI thread with a navigation token that discards stale results ([MainWindow.xaml.cs](MainWindow.xaml.cs) lines 288–344).
- Dangerous categories are deliberately *not* implemented: no service disabling, no telemetry killing, no undocumented registry patches, no BIOS/voltage changes, no offload/RSS tampering. Manual states are honest placeholders, and the diagnostics engine routes XMP/EXPO, ReBAR, and refresh-rate issues to "Manual (BIOS)" guidance.
- The Store release pipeline hard-refuses to publish unsigned or validation-failing packages ([release-store.ps1](release-store.ps1) lines 245–247), and the Pages workflow enforces immutable versioned URLs.

**Overall condition.** For its stated philosophy — *Supported. Reversible. Verifiable. Reasonably safe.* — the implementation is **materially better than typical "optimizer" utilities** and largely honors its claims. However, the audit found **2 HIGH and 8 MEDIUM confirmed defects**, most importantly: (1) the Visual Effects revert never restores a string-typed `MinAnimate` value (a field-name mismatch in the rollback JSON), (2) a system restore point is *never* created despite `RestorePointService` existing as dead code, (3) the user-temp cleanup reports success when everything failed, (4) failed machine-scope startup disables leave phantom "Disabled" entries, and (5) there is no concurrency guard preventing the same optimization from being applied twice simultaneously (rollback record can be lost). There are **no tests of any kind**, and compilation could not be validated in this environment. Detail in §12.

---

## 2. Repository Map

```
swiftwave-tweaks-tweaks/
├─ GameReadyOptimizer.csproj        # The only project. net8.0-windows10.0.19041.0, WPF, single-file self-contained win-x64, v2.0.0
├─ app.manifest                     # asInvoker, supportedOS = Windows 10/11 GUID only
├─ App.xaml / App.xaml.cs           # Global control styles (Button/ListBoxItem/ScrollBar) + single-instance guard & pipe listener startup
├─ MainWindow.xaml / .xaml.cs       # Shell window: custom chrome, sidebar nav (12 pages), welcome overlay, page builder + OptimizationCard (~1,030 lines)
├─ CaptionButton.cs                 # Vector-drawn min/max/close caption buttons (WindowChrome hit-test exempt)
├─ ToggleSwitch.cs                  # Custom animated toggle (ToggleButton subclass) used on all optimization cards
├─ Motion.cs                        # Shared animation timings + Windows "show animations" accessibility fallback
├─ Ui.cs                            # Frozen dark-theme brush palette + control factories (cards, tags, status pills)
├─ Core/
│  ├─ Optimization.cs               # THE central contract: enums, Detection/ApplyResult records, apply/verify/revert base class
│  └─ DiagnosticsEngine.cs          # Read-only diagnostic rules over the snapshot
├─ Optimizations/                   # All 8 optimization implementation files (15+ instances)
│  ├─ GamingOptimizations.cs        # HKCU DWORD base class + GameDVR/GameBar/GameMode/HAGS + manual-NVIDIA placeholder
│  ├─ PowerOptimizations.cs         # Power plan (powercfg), processor frequency policy (Advanced)
│  ├─ StorageOptimization.cs        # Optimize-Volume per drive + OptimizationCatalog + Presets
│  ├─ VisualEffectsOptimization.cs  # 3 visual-effect presets over HKCU keys + WM_SETTINGCHANGE broadcast
│  ├─ NvidiaPowerManagementOptimization.cs  # NVAPI DRS PREFERRED_PSTATE lifecycle
│  ├─ NetworkPowerSavingOptimization.cs     # EEE/Green Ethernet/PowerSavingMode via NetAdapter cmdlets
│  ├─ FortniteAffinityOptimization.cs       # Startup .lnk + hidden PS watcher excluding CPU 0 from Fortnite affinity
│  └─ WindowsDebloatOptimization.cs         # ContentDeliveryManager suggestion-surface toggles
├─ Services/
│  ├─ RegHelper.cs                  # Typed registry read/write helpers + RegistryRead.CurrentVersionValue
│  ├─ ProcessRunner.cs              # Process/PowerShell execution incl. elevated-with-output capture
│  ├─ SafeLog.cs                    # Append-only activity log (%LOCALAPPDATA%\SwiftwaveTweaks\logs\activity.log)
│  ├─ RollbackStore.cs              # rollback.json read/add/remove
│  ├─ UserPreferences.cs            # preferences.json (OnboardingCompleted, MinimizeOnStartup, CloseToTray)
│  ├─ SingleInstance.cs             # Session mutex + named-pipe activation (per user/session)
│  ├─ SystemDetector.cs             # WMI/registry snapshot + PowerPlanReader (powercfg)
│  ├─ MachineServices.cs            # NvidiaService (nvidia-smi), DisplayService (EnumDisplaySettings), RestorePointService (DEAD)
│  ├─ NetworkAdapterService.cs      # NetAdapter PowerShell read/apply/restore
│  ├─ NvidiaDrsService.cs           # NVAPI DRS wrapper (read/apply/restore-to-default/restore-to-value)
│  ├─ StartupService.cs             # Run-key enumeration, classification, reversible disable via HKCU backup
│  ├─ CleanupService.cs             # user-temp / windows-temp / recycle-bin measure + clean
│  ├─ TrayIcon.cs                   # Raw Shell_NotifyIcon tray icon (manual .ico parse)
│  └─ WindowForeground.cs           # AttachThreadInput foreground activation for second-instance
├─ installer/SwiftwaveTweaks.wxs    # WiX v4-schema per-machine x64 MSI (silent /qn for Store)
├─ .github/workflows/deploy-msi-pages.yml   # Release MSI → immutable versioned GitHub Pages URL (magic-byte validated)
├─ build-release.ps1                # Icon gen → restore → Debug build → Release publish → copy metadata
├─ release-store.ps1                # Full Microsoft Store MSI/EXE pipeline (sign → MSI → verify → Pages → Partner Center)
├─ Properties/PublishProfiles/win-x64.pubxml
├─ store/                           # listing.json (Store metadata) + screenshots + capture script (UIA automation)
├─ release/ release-assets/ store-artifacts/   # Prebuilt deliverables (188 MB EXE, 58 MB MSI, README/CHANGELOG/VERSION)
├─ New-Icon.ps1                     # Generates Assets/GameReady.ico programmatically
├─ Assets/GameReady.ico
├─ README.md                        # Stale in places (see §10)
├─ click.ps1, click-toggle.ps1, click-toggle2.ps1, find-toggle.ps1, hover.ps1, uia-select.ps1, uia-toggle.ps1,
│  shot.ps1, crop.ps1, sample.ps1   # Developer-only UIA/screenshot/pixel helpers used to script the running app during development
├─ bin/ obj/                        # Build artifacts (991 files) — excluded from line-level inspection; standard MSBuild outputs
└─ (no .git directory in this snapshot; no LICENSE file; no test project anywhere)
```

**Exclusions explained:** `bin/` and `obj/` are MSBuild intermediate/final outputs of this same source tree and were not inspected file-by-file. `release/SwiftwaveTweaks.exe` (188 MB) and `store-artifacts/*.msi` are publish outputs of the current source; they were not executed or decompiled (audit rule: do not execute optimization software; binaries match the csproj version metadata as recorded in release/VERSION.txt). `store/screenshots/*.png` are static images. Everything else — 100% of source, scripts, config, CI, and installer definitions — was read in full.

---

## 3. Architecture Documentation

### 3.1 Startup sequence

1. `App.OnStartup` ([App.xaml.cs](App.xaml.cs)) runs **before** any window: `SingleInstance.TryAcquire()` takes a session-local mutex (`Local\SwiftwaveTweaks.SingleInstance`). A second launch signals the first over a per-user/per-session named pipe (`SignalExisting` sends 1 byte, receives the owner PID, calls `AllowSetForegroundWindow`) and shuts down.
2. `StartupUri` creates `MainWindow`. Constructor: reads `preferences.json`, sets footer version (`v2.0` derived from `InformationalVersion`), builds caption buttons, checks admin status via `WindowsPrincipal`.
3. `OnSourceInitialized` creates the `TrayIcon` (raw `Shell_NotifyIcon`, no extra framework).
4. `OnLoaded` (async): kicks off `SystemDetector.CollectAsync()` and `NvidiaService.Query()` **immediately** (prefetch), plays the welcome overlay (~2.4 s) unless Windows reduced-motion is on, marks `OnboardingCompleted`, optionally minimizes, then performs the initial page navigation explicitly (the XAML's initial `SelectedIndex=0` fires `SelectionChanged` before the visual tree exists and is deliberately skipped).
5. `SingleInstance.StartListener` accepts activation requests until exit; `OnExit` releases the mutex and cancels the listener.

### 3.2 No DI container — explicit static service graph

There is no service locator, no IoC, no ViewModel layer. Dependencies are direct static calls:

- **Pages** → `MainWindow` builder methods → `SystemDetector`, `NvidiaService`, `StartupService`, `CleanupService`, `NetworkAdapterService`, `OptimizationCatalog`, `RollbackStore`, `DiagnosticsEngine`.
- **Optimizations** → `RegHelper`, `ProcessRunner`, domain services (`NvidiaDrsService`, `NetworkAdapterService`, `PowerPlanReader`), and `SafeLog`/`RollbackStore` (only via the base class).
- **State** is carried in `MainWindow` fields: `_snapshotTask`, `_nvidiaTask` (memoized tasks = per-process caches), `_catalog` (drive-bound), `_prefs`, `_navToken`.

### 3.3 Navigation and rendering

Twelve fixed pages (`PageDefs`). Navigation uses a moving highlight border, a title crossfade, and `CrossfadeTo` → `LoadPage(key, token)`. `LoadPage` runs each page's data collection inside `Task.Run`/`await` and swaps content only if `token == _navToken` — the documented fix for the old "Network freeze" race. Pages are rebuilt from scratch on every visit; there is no page caching (detection is intentionally live every time).

### 3.4 The Optimization protocol (the core pattern)

Every feature that changes the system subclasses `Optimization` and implements:

| Member | Role |
|---|---|
| `Detect()` | Read-only live state → `OptState` (Optimized / NeedsAttention / Manual / Unsupported / Unknown) + `Selectable` |
| `CapturePrevious()` | Serialized JSON of the exact previous state (values *and* absence) |
| `ApplyChange(out detail)` | The actual mutation |
| `DetectAfterApply()` | Independent re-read confirming the change |
| `Revert(previousState, out detail)` | Restore captured state, return true only when verified |

`Apply()` orchestrates: skip when already optimized; fail honestly when Manual/Unsupported; auto-revert on failed verification; append to `RollbackStore` only after verified success; log everything to `SafeLog`. `Rollback(previousState)` is the History-page entry point.

### 3.5 Async model

- Detection and apply always run via `Task.Run`; UI updates resume on the dispatcher (`await` continuation).
- Busy feedback is a global status bar + pulse dots (`SetBusy`); the per-card toggle disables itself while its own operation runs.
- The **only** fire-and-forget is the single-instance pipe listener loop (designed to swallow faults).

### 3.6 Error propagation and logging

- All execution paths funnel failures into `ApplyResult`/`(bool, string)` tuples with human-readable messages; exceptions inside `Apply()`/`Rollback()` are caught by the base class.
- `SafeLog` appends one line per event (`ISO timestamp message :: exception.Message`) to `%LOCALAPPDATA%\SwiftwaveTweaks\logs\activity.log`. Page-load failures are additionally shown in the page body.
- Registry/WMI reads swallow exceptions and return null/empty by design ("detection must never crash the UI"); the trade-off is silent degradation to fallback strings.

### 3.7 Privileges / UAC model

`app.manifest` declares `asInvoker`. Elevation is per-operation:

- HKCU-only operations (Game DVR, Game Bar, Game Mode, Visual Effects, Windows suggestions, Fortnite watcher) require **no elevation**.
- HKLM registry, powercfg, Optimize-Volume, NetAdapter writes, machine-scope Run keys: elevated child `powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand <base64>` with `Verb=runas`. Output capture writes to a temp file because elevated children cannot pipe stdout to a non-elevated parent ([ProcessRunner.cs](Services/ProcessRunner.cs) lines 60–90).
- NVAPI DRS needs no elevation (per-user driver profile database).
- The header shows "· administrator" only when the whole app was launched elevated.

### 3.8 Shutdown

`OnClosing`: if `CloseToTray` is set, hide + show tray; else dispose tray icon and close. `App.OnExit` stops the pipe listener and releases the mutex. No long-running work is cancelled on exit — an in-flight `Task.Run` apply would be abandoned mid-operation (partially-applied risk is bounded by each optimization's own verify/auto-revert, but an abandoned *elevated* child could still be running; see finding SWT-010/§12).

### 3.9 Dependency map (high level)

```
App → SingleInstance ─► MainWindow
MainWindow → SystemDetector (WMI, registry, powercfg, EnumDisplay*, PS Get-Partition fallback)
           → NvidiaService (nvidia-smi)
           → StartupService (registry + elevated PS) → RegHelper / ProcessRunner
           → CleanupService (file IO + PS)
           → NetworkAdapterService (PS NetAdapter)
           → OptimizationCatalog → [15+ Optimization instances]
                    ├→ RegHelper (HKCU writes)
                    ├→ ProcessRunner (elevated PS: HAGS, powercfg, Optimize-Volume, HKLM Run)
                    ├→ NvidiaDrsService → NvAPIWrapper.Net → nvapi64.dll
                    ├→ NetworkAdapterService
                    └→ FortniteAffinityOptimization (file system + WScript.Shell COM .lnk)
           → RollbackStore (rollback.json) / PreferencesStore (preferences.json) / SafeLog (activity.log)
           → DiagnosticsEngine (reads snapshot + StartupService)
```

---

## 4. Complete Feature Inventory

All 12 navigation pages and every actionable item, with the full UI → VM → service → system chain. "Persist" = survives reboot by mechanism; "Rollback restores prior state" is assessed against actual code.

### 4.1 Dashboard
- **Where:** [MainWindow.xaml.cs](MainWindow.xaml.cs) `BuildDashboard`/`BuildPcSpecs` (lines 348–438). **Data:** one `SystemSnapshot` per process (`CollectAsync` memoized).
- **Behavior:** Windows/CPU/GPU/RAM-modules-with-rated-vs-configured-speeds/displays/power plan/drives/process count; "Gaming-relevant state" grid (Game Mode, Game Bar, DVR, HAGS).
- **No system changes.** Stale after in-app changes until restart (SWT-009).

### 4.2 Recommended (presets)
- **Where:** `BuildRecommended` + `RunPreset` (lines 440–503); preset defs in [StorageOptimization.cs](Optimizations/StorageOptimization.cs) `Presets` (lines 137–169).
- **Presets:** *Recommended safe* (power-plan, game-dvr, game-bar, game-mode, visual-default, all storage-*), *Gaming* (+ processor-policy advanced, visual-balanced), *Balanced* (power-plan, game-mode, visual-default).
- **Behavior:** applies members sequentially through each one's full lifecycle; already-optimal members are skipped; per-member UAC prompts appear as needed.
- **Rollback:** each member individually recorded; **no preset-level undo** (revert members one by one from History).

### 4.3 Gaming page
| Optimization | Id | Mechanism | Admin | Persist | Rollback quality |
|---|---|---|---|---|---|
| Disable Game DVR background recording | `game-dvr` | HKCU `...\GameDVR\AppCaptureEnabled=0`; reads HKLM policy for state label | No | Yes (HKCU) | Absence-aware restore; policy state correctly detected ([GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) 68–99) |
| Disable Xbox Game Bar overlay | `game-bar` | HKCU `...\GameBar\UseNexusForGameBarEnabled=0` | No | Yes | Absence-aware |
| Ensure Game Mode enabled | `game-mode` | HKCU `AutoGameModeEnabled=1` | No | Yes | Absence-aware (delete on revert) |
| HAGS | `hags` | HKLM `GraphicsDrivers\HwSchMode=2` via elevated PS | Yes | Yes (HKLM) | Absence-aware incl. delete-when-was-absent; restart required |
| Fortnite CPU-0 affinity watcher | `fortnite-affinity` | Per-user `shell:startup` .lnk → hidden PowerShell script `%LOCALAPPDATA%\SwiftwaveTweaks\FortniteAffinity.ps1`; sets `ProcessorAffinity` excluding logical CPU 0 once when Fortnite appears | No | Yes (file artifacts) | Removes only Swiftwave-owned artifacts; artifact-state detection (valid/modified/missing/outdated) |
| Reduce Windows suggestions | `windows-suggestions` | 9 HKCU ContentDeliveryManager DWORDs → 0 | No | Yes | Per-key exact restore incl. absence; independent re-read verification |

### 4.4 NVIDIA page
- **Facts card:** name/driver/VRAM/temp/power via `nvidia-smi --query-gpu=...`; ReBAR via `nvidia-smi -q` regex ([MachineServices.cs](Services/MachineServices.cs) `NvidiaService.Query`).
- **Power management mode** (`nvidia-power-mgmt`): NVAPI DRS setting `0x1057EB71` (PREFERRED_PSTATE) on the base profile → value 1 ("Prefer maximum performance"). Detect reads live (incl. the valid "not explicitly set = driver default" state); apply verifies by fresh-session re-read; revert distinguishes predefined-default vs user-custom prior value. No elevation. ([NvidiaDrsService.cs](Services/NvidiaDrsService.cs), [NvidiaPowerManagementOptimization.cs](Optimizations/NvidiaPowerManagementOptimization.cs).)
- All other NVIDIA profile settings are intentionally not exposed (documented rationale in [StorageOptimization.cs](Optimizations/StorageOptimization.cs) lines 101–109). `NvidiaProfileManualState` class exists but is unused (dead code kept as infrastructure).

### 4.5 Power page
| Optimization | Id | Mechanism | Admin | Rollback |
|---|---|---|---|---|
| Performance power plan | `power-plan` | `powercfg -duplicatescheme` (Ultimate template) + `powercfg /setactive` | Yes | Restores previous plan **by GUID**, verified by re-read ([PowerOptimizations.cs](Optimizations/PowerOptimizations.cs) 8–85) |
| Processor frequency policy (max 100%) — *Advanced* | `processor-policy` | `powercfg /setacvalueindex|/setdcvalueindex` PROCTHROTTLEMAX=100 on current scheme | Yes | Restores captured AC/DC indices — **DC −1 sentinel bug** (SWT-006) |

### 4.6 Storage page
- One card per fixed drive (`storage-<letter>`): `Optimize-Volume -DriveLetter X -Verbose` elevated; TRIM/retrim on SSD/NVMe, defrag on HDD; success requires explicit output phrases ("successfully" / "did not need" / "completed without error"). One-shot Apply button (no toggle). No settings changed → revert is a documented no-op. ([StorageOptimization.cs](Optimizations/StorageOptimization.cs) 11–78.)

### 4.7 Network page
- Active wired adapter facts via `Get-NetAdapter` filter; **hidden entirely** when the driver exposes none of `*EEE`, `AdvancedEEE`, `EnableGreenEthernet`, `PowerSavingMode`.
- `network-power-saving`: elevated `Set-NetAdapterAdvancedProperty` to 0 for enabled keywords only; revert restores exact captured values and re-reads. Explicitly never touches RSS/interrupt moderation/offloads/flow control ([NetworkAdapterService.cs](Services/NetworkAdapterService.cs), [NetworkPowerSavingOptimization.cs](Optimizations/NetworkPowerSavingOptimization.cs)).

### 4.8 Startup page
- Enumerates HKCU Run (user scope) + HKLM Run 64-bit (machine scope) + this tool's backup key; keyword classification (Important / Probably unnecessary / User decision) from two static name lists.
- **Disable:** backup value to `HKCU\...\SwiftwaveTweaks\StartupBackup\<scope>`, then delete (HKCU direct; HKLM via elevated `Remove-ItemProperty`), then verify absence + backup presence. Important-classified entries are refused.
- **Enable:** restore from backup (HKCU direct; HKLM elevated), verify presence, delete backup.
- Defects: phantom entries after cancelled elevation (SWT-004), REG_EXPAND_SZ → REG_SZ type loss (SWT-005), 32-bit HKLM Run and Wow64 invisible, per-user backup for machine entries ([StartupService.cs](Services/StartupService.cs)).

### 4.9 Cleanup page
- `user-temp` (direct file IO, in-use files skipped and counted), `windows-temp` (elevated PS delete + before/after size), `recycle-bin` (`Clear-RecycleBin -Force`; measured via Shell COM). Success criteria defect in user-temp (SWT-003). ([CleanupService.cs](Services/CleanupService.cs).)

### 4.10 Diagnostics page
- Read-only rules ([DiagnosticsEngine.cs](Core/DiagnosticsEngine.cs)): XMP/EXPO below rated speed (Manual/BIOS), >10 enabled startup apps, >180 processes, drives <10% free, mechanical-drive detection, refresh-rate below max, Game DVR enabled, HAGS not explicitly enabled, conservative power plan, ReBAR disabled (Manual/BIOS), driver-update suggestion. No actions are taken automatically.

### 4.11 History & Restore
- Lists `rollback.json` newest-first; Restore resolves the optimization by Id from the current build's catalog and calls `Rollback(previousState)`; on verified success removes the entry. Ids unknown to the current build are reported as unrestorable. ([MainWindow.xaml.cs](MainWindow.xaml.cs) 736–779.)

### 4.12 Settings
- "Start minimized", "Close to tray" toggles (preferences.json); About card with version; data-locations disclosure. No "run at Windows startup" preference exists (the Store listing's wording doesn't claim one; the README's "persisted startup preference" refers to the welcome sequence — itself now inaccurate, §10).

### 4.13 Shell/integration features (not pages)
- Welcome overlay animation (skipped under reduced motion), tray icon with Open/Exit, close-to-tray, single-instance activation (pipe + `AttachThreadInput` foregrounding), custom caption buttons + WindowChrome drag/snap, per-monitor-safe reduced-motion fallbacks.

---

## 5. System Interaction Map

**WMI/CIM (reads only):** `Win32_Processor`, `Win32_VideoController` (virtual/basic/indirect filtered), `Win32_PhysicalMemory` (Speed vs ConfiguredClockSpeed → XMP detection), `Win32_ComputerSystem`, `Win32_LogicalDisk (DriveType=3)`, `Win32_DiskDrive` + `ASSOCIATORS OF` chains (drive→partition→volume mapping).

**Registry reads:** `HKLM\...\CurrentVersion` (ProductName/DisplayVersion/ReleaseId/CurrentBuildNumber/UBR); `HKLM\...\GraphicsDrivers\HwSchMode`; `HKCU\...\GameDVR\AppCaptureEnabled`; `HKLM\...\Policies\...\GameDVR\AllowGameDVR`; `HKCU\...\GameBar` (UseNexusForGameBarEnabled, AutoGameModeEnabled/AllowAutoGameModeEnabled); VisualFX/Advanced/Desktop WindowMetrics keys; HKCU+HKLM Run keys; ContentDeliveryManager keys; startup backup key.

**Registry writes:** HKCU direct via `RegHelper` (GameDVR, GameBar, VisualEffects set, Advanced, Desktop, ContentDeliveryManager, Run, StartupBackup) — absence-aware delete supported. HKLM only through elevated PowerShell (`HwSchMode`, HKLM Run).

**Subprocesses (all `ProcessRunner`, base64-encoded, no shell interpolation of user input):**
- `powershell.exe` (5.1, `-NoProfile -ExecutionPolicy Bypass -EncodedCommand`): drive mapping fallback; NetAdapter read/apply/restore; HAGS write; HKLM Run write; `Optimize-Volume`; `Clear-RecycleBin`; windows-temp cleanup; `Checkpoint-Computer` (dead code).
- `powercfg`: `/getactivescheme`, `/list`, `/q`, `-duplicatescheme`, `/setactive`, `/setacvalueindex`, `/setdcvalueindex`.
- `nvidia-smi`: `--query-gpu=...` and `-q`.
- Elevation always `Verb=runas` + output captured to `%TEMP%\swt-elev-<guid>.txt` then read back and deleted.

**Native P/Invoke:** `EnumDisplayDevices`/`EnumDisplaySettings` (monitors + max refresh at current res); `Shell_NotifyIcon`/`CreateIconFromResourceEx`/`DestroyIcon` (tray); `AllowSetForegroundWindow` (single-instance); `ShowWindow/SetForegroundWindow/SetActiveWindow/BringWindowToTop/GetForegroundWindow/GetWindowThreadProcessId/AttachThreadInput` (activation); `SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE)` (visual effects take effect immediately).

**Third-party native:** NvAPIWrapper.Net 0.8.1.101 → vendor `nvapi64.dll`; DRS session get/set/restore of `PREFERRED_PSTATE` (0x1057EB71) on base profile.

**COM:** `WScript.Shell` (create/read Startup .lnk); `Shell.Application` Namespace(0xA) (recycle-bin size).

**OS primitives:** session mutex + named pipe (single instance); WPF WindowChrome (drag/snap/caption hit-testing).

**File system:** `%LOCALAPPDATA%\SwiftwaveTweaks\{logs\activity.log, rollback.json, preferences.json, FortniteAffinity.ps1}`; `%TEMP%\swt-elev-*.txt` (transient); `shell:startup\Swiftwave Fortnite Affinity.lnk`; user/system temp deletion; recycle bin.

**Network:** the app itself makes **no network requests** (only local subprocesses). Update/distribution flows use GitHub APIs via the separate CI/pipeline scripts, not the app.

---

## 6. Safety & Reversibility Assessment

Evaluated against the stated priority order: **Supported → Reversible → Verifiable → Reasonably safe → Technically defensible.**

### What genuinely holds up
1. **Supported interfaces only (confirmed).** Every mutation goes through: HKCU/HKLM registry values that are documented Windows settings, `powercfg`, `Optimize-Volume`, `Set-NetAdapterAdvancedProperty`, NVAPI DRS, or the documented Run-key model. No service SCM changes, no bcdedit, no driver hooking, no undocumented keys were found anywhere in the tree.
2. **Revert restores the *actual* previous state in most paths (confirmed).** Absence-aware rollback for every HKCU DWORD toggle; exact-value NVAPI revert with predefined/custom distinction; per-key network values with re-read; power plan by GUID. This is better than "apply presumed defaults," which the assignment specifically warns about — the codebase largely avoids that trap.
3. **Verification is real (confirmed).** `DetectAfterApply` re-reads; the base class auto-reverts when verification fails; elevated writes are confirmed by parsing explicit `WRITE:True` / `SETACTIVE:True` / `OPT:True` markers plus independent re-reads where possible.
4. **Honesty mechanisms (confirmed).** Manual/Unsupported states refuse to apply; partially-installed Fortnite watcher reports `Unknown`; policy-disabled Game DVR is labeled; cleanup reports skipped in-use items; the app never pretends an unsupported feature is automated.

### Where the philosophy is not fully met (confirmed defects; details in §12)
- **No restore point is ever created** — `RestorePointService.TryCreate` exists, is fully implemented, and is called by *nothing*. For a tool that writes HKLM values and power policies, the absence of this safety net is the largest gap between claim and implementation (SWT-002).
- **Visual Effects revert has a real data-loss bug** for string-typed `MinAnimate` (SWT-001) — a rollback that cannot fully reconstruct prior state.
- **Processor DC restore can write a −1 sentinel** when battery-side state was unreadable at capture (SWT-006).
- **Partial-failure states are not always cleaned up:** cancelled elevation during machine-scope startup disable leaves a phantom backup that misrepresents system state (SWT-004).
- **Concurrent operations can defeat rollback bookkeeping** (SWT-007): nothing prevents a card toggle and a preset from applying the same optimization simultaneously; `RollbackStore.Add` is last-writer-wins.
- **Success detection is wrong in user-temp cleanup** (SWT-003): `reclaimed > 0 || failed > 0` marks a 100%-failed cleanup as success.

### Risk-classification of individual features
- **Low risk, well-guarded:** game-dvr, game-bar, game-mode, visual-default/balanced, windows-suggestions, power-plan, network-power-saving, nvidia-power-mgmt.
- **Moderate:** HAGS (HKLM + restart, honestly labeled Moderate), processor-policy (Advanced label; DC sentinel bug), visual-performance (Moderate; changes many UX values at once).
- **Operationally irreversible by design (documented in UI):** cleanup of user temp / recycle bin (deletion), windows-temp cleanup. These are not registry state; "rollback" does not apply and the UI says so.
- **Disclosure-sensitive:** the Fortnite watcher is a *hidden, silent, auto-starting background process* created in the user's Startup folder. It is per-user, removable, and narrowly scoped, but it is persistence — the card says "Startup watcher installed" only in its state line; the description does not spell out that a permanent PowerShell process polls every 500 ms until Fortnite appears, or that enabling mid-session does nothing until next logon (SWT-011).

**Verdict:** the safety architecture is real and unusually rigorous for this software category, but four concrete defects (SWT-001/002/004/006) mean "reversible" is currently ~95% true rather than 100%, and two process-level gaps (SWT-003/007) undermine "verified."

---

## 7. Technical Debt & Code Quality

**Strengths.** Consistent naming; `Nullable` enabled and clean usage; frozen brushes; static UI factories keep page code compact; the optimization protocol is genuinely uniform; comments explain *why* (including spec references and verification rationale); UI responsiveness is protected by Task.Run everywhere with a navigation token race guard; accessibility basics exist (AutomationProperties on caption buttons and title, tooltips, reduced-motion honored through `SystemParameters.ClientAreaAnimation`).

**Confirmed problems (locations in §12):**

1. **Dead code:** `SystemSnapshot.ToDisplayText()` (superseded by `BuildPcSpecs`, referenced only in a comment); `RestorePointService` (never called — see SWT-002); `NvidiaProfileManualState` (intentionally kept, documented); unused fields `HkcuDwordOptimization._previous/_previousJson`; unused `ENUM_REGISTRY_SETTINGS` const.
2. **Instance state mutated inside `Detect()`:** `PowerPlanOptimization._target`, `StorageOptimization._appliedSuccessfully` — safe only because the apply path is currently sequential; becomes a bug under concurrency (ties into SWT-007).
3. **No operation-level mutual exclusion:** `SetBusy` is cosmetic; toggles of *other* cards and preset runs remain clickable during an operation.
4. **Memoized-but-immutable caches:** snapshot/catalog never invalidated → stale UI data (SWT-009).
5. **Logging:** single append-only file, no rotation, no levels, no structure; every registry read failure is silently swallowed (deliberate, but means diagnostic blind spots).
6. **RollbackStore/PreferencesStore:** whole-file JSON rewrite, no atomic write (temp+move), no lock — corruption possible on concurrent access or crash mid-write.
7. **Performance:** every page navigation re-detects *all* optimizations (each Network/Power detection spawns PowerShell); `BuildNetwork` reads the adapter twice; `ResolveDiskModel` does O(disks×partitions) ASSOCIATORS queries; self-contained single-file EXE is 188 MB and self-extracts native libraries at first run.
8. **UI construction:** ~1,000-line code-behind with all 12 pages built imperatively — readable but monolithic; no MVVM separation (acceptable at this size, but it concentrates risk).
9. **Hardcoded assumptions:** Windows PowerShell 5.1 path (`powershell.exe`) — fine on Windows 11 but excludes PS7-only environments (none required); `nvidia-smi` must be on PATH for the NVIDIA facts card; `ReBAR` regex depends on exact nvidia-smi wording (needs runtime validation).
10. **Localization readiness:** zero — every string is hardcoded English (including localized-looking registry descriptions).
11. **Accessibility gaps:** caption buttons are mouse-only (no keyboard focus/Invoke pattern); several interactive `Border`/`Grid` composites lack automation peers.
12. **Versioning:** version hardcoded in csproj *and* in `release-store.ps1` (`$version`, `$msiName`, regex check) — three edit sites per release.
13. **Repository hygiene:** 188 MB EXE + 58 MB MSI + icon/PDB committed; `store/capture-screenshots.ps1` hardcodes `c:\Users\Administrator\Desktop\...`; ~10 one-off UIA dev scripts at root; README titled for the old project name.

**Dependency review:** `System.Management 8.0.0` (matches TFM; standard). `NvAPIWrapper.Net 0.8.1.101` — pinned, community wrapper around NVIDIA's proprietary NVAPI; upstream is low-activity; it is the only dependency that loads vendor-native code. No transitive red flags identified statically. Vulnerability scanning was not possible (no SDK; see §11).

---

## 8. Security Assessment

**Process execution.** All subprocess arguments are built from string constants, base64-encoded scripts, or system-derived values. Startup entry names/commands passed to elevated PowerShell are single-quote-escaped ([StartupService.cs](Services/StartupService.cs) lines 103, 133). **One gap:** `NetworkAdapterService` interpolates the adapter `InterfaceDescription` and driver-enumerated keywords into scripts without escaping ([NetworkAdapterService.cs](Services/NetworkAdapterService.cs) lines 135, 164–171) — an apostrophe in an adapter description breaks the script (correctness), and a malicious driver name is a theoretical injection vector, but exploiting it already requires local driver-level control (LOW; confirmed as SWT-017).

**Registry/privileged operations.** Writes are confined to the enumerated key set in §5. HKLM writes occur only inside elevated PowerShell child processes with explicit success markers. `RequestedExecutionLevel asInvoker` prevents the app itself from silently running elevated.

**File system.** Elevated output files are GUID-named in `%TEMP%` and deleted in a `finally`; contents are script marker strings, not secrets. Cleanup deletes only the three documented categories; user-temp deletion is aggressive-but-standard (in-use files skipped).

**Network communication.** None in the app. The pipeline scripts talk to GitHub/Partner Center APIs only, using env-var credentials.

**Secrets.** No secrets committed (grepped). `release-store.ps1` reads signing material and MSStore credentials exclusively from environment variables and explicitly warns against committing them; the workflow uses the ephemeral `GITHUB_TOKEN`.

**Deserialization.** All JSON is System.Text.Json against known record types; no polymorphic deserialization; rollback JSON is schema-validated by property access with try/catch.

**Update integrity / supply chain.** The app has no self-update mechanism (good — nothing to spoof). Distribution integrity rests on: Authenticode signing gate (Store path), immutable versioned Pages URLs with MSI magic-byte validation and no-overwrite enforcement (workflow + script), and GitHub Release immutability checks. Actions checkout is pinned to `@v4` tag rather than commit SHA (minor supply-chain hardening opportunity). NuGet deps are version-pinned. The Fortnite watcher script is user-writable and runs at logon via `-ExecutionPolicy Bypass`; its integrity check is a marker-substring test only — any process running as the user can modify it, which adds no privilege beyond what user-malware already has, but the version marker gives weak tamper detection (INFO, SWT-029).

**Attack-surface summary:** the realistic surface is local: someone with the user's privileges can already do everything the tool does. No elevation-of-privilege path was identified; the elevated children execute fixed scripts, not user input.

---

## 9. Build & Distribution Assessment

**Build ([build-release.ps1](build-release.ps1)):** regenerate icon → `dotnet restore` → Debug build (compile check) → `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true` → copy `release-assets/{README.txt,CHANGELOG.md,VERSION.txt}`. Output: one 188 MB EXE (self-contained WPF + runtime + native libs, `IncludeNativeLibrariesForSelfExtract`).

**csproj:** single project; `net8.0-windows10.0.19041.0`; WPF; `Nullable`/`ImplicitUsings` on; version triple 2.0.0; `asInvoker` manifest; two PackageReferences. Publish profile duplicates the publish settings (`win-x64.pubxml`).

**MSI ([installer/SwiftwaveTweaks.wxs](installer/SwiftwaveTweaks.wxs)):** WiX **v4 schema** (built by WiX **v5** toolchain — consistent), per-machine, x64, `MajorUpgrade` with downgrade block, embedded CAB high compression, Start-Menu shortcut + ARP metadata, sources the *signed* `release\SwiftwaveTweaks.exe`.

**Store pipeline ([release-store.ps1](release-store.ps1)):** 8 gated stages: precondition checks (csproj regex pins v2.0.0 and self-contained/single-file) → clean build → PE-header/version identity verification → Authenticode sign EXE (thumbprint or PFX via env vars, Sectigo timestamp) → WiX build MSI → MSI table verification (ProductName/Version/Manufacturer/ALLUSERS=1/x64 template) → sign MSI → immutable direct GitHub Pages URL (HEAD-check: HTTP 200, **no redirects**, size match; refuses to overwrite existing versioned URLs) → Partner Center draft update + submit + poll via `msstore` CLI. Hard gates: unsigned ⇒ no hosting, no submission.

**CI ([deploy-msi-pages.yml](.github/workflows/deploy-msi-pages.yml)):** on `release:published` or manual dispatch, mirrors `*.msi` release assets onto `gh-pages` at `/<version>/<name>.msi`; enforces immutability (fails if target exists), validates OLE2 magic bytes, commits as the bot. Concurrency-grouped.

**Versioning consistency (verified):** csproj 2.0.0 = app.manifest 2.0.0.0 = VERSION.txt = CHANGELOG top entry = release-store constants = WiX `Version="2.0.0"` = Store listing "whatsNew" 2.0. The MSI product version drops the 4th field as documented.

**Discrepancies / gaps:**
- No CI *build or test* pipeline exists at all — the only workflow publishes MSIs. Nothing verifies compilation on push/PR.
- Version bumps require editing csproj **and** two constants in release-store.ps1 (script has a "future versions" note).
- Store-distributed (signed MSI) vs GitHub-distributed (unsigned EXE in `release/`) builds differ in signature only; both come from the same publish step — no drift in *content* was found. VERSION.txt explicitly records "Signing: Unsigned" for the raw release.
- `release-store.ps1` default `-GitHubTag v2.0.0` matches the current version; release/CHANGELOG documents 1.0.0 → 1.1.0 → 2.0.0 history consistent with README boundary text mentioning "v1.1".

---

## 10. Documentation Accuracy

Verified claim-by-claim against implementation.

**[README.md](README.md) — stale in three places:**
1. **Title "GameReady Optimizer"** — the product is now Swiftwave Tweaks everywhere else (assembly, UI, manifest, installer, Store listing).
2. **"A replayable Swiftwave Tweaks welcome animation with persisted startup preference"** — FALSE for v2: the welcome animation plays on every startup and is *not* configurable ([UserPreferences.cs](Services/UserPreferences.cs) doc comment states this explicitly; the persisted preferences are OnboardingCompleted/MinimizeOnStartup/CloseToTray). "Replayable ... with persisted preference" described a v1 setting that no longer exists.
3. **"Current boundary: NVIDIA profile automation, HAGS ... are intentionally manual/unsupported in v1.1"** — OUTDATED: v2 automates NVIDIA Power Management Mode via NVAPI DRS and HAGS via elevated registry write ([StorageOptimization.cs](Optimizations/StorageOptimization.cs) 101–117, [GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) 130–194). The README still describes the v1.1 feature set.
- Accurate README claims: WMI/CIM detection; power-plan activation with rollback; Game DVR with elevation prompt (partially — the *user* value write needs no elevation; only policy reading is HKLM); logs/rollback under `%LOCALAPPDATA%`; unsigned-exe disclosure; build instructions.

**[Store listing.json](store/listing.json) — accurate (verified line-by-line).** All six productFeatures match implementation; the "does not do" list matches (no drivers/services installed, no modification without explicit action, unverifiable things shown manual); certificationNotes correctly describe asInvoker + per-operation UAC + documented interfaces. One nuance: "Game DVR/Game Bar **policy**" — the app writes user-scope values and *reads* policy; it never writes policy. Wording is defensible.

**[release/README.txt](release/README.txt), [VERSION.txt](release/VERSION.txt), [CHANGELOG.md](release/CHANGELOG.md)** — all consistent with the code (unsigned, self-contained, version history).

**In-app claims — spot-checked:** "Every change is detected live, verified after applying, and restorable from History" (true except the defects in §6/§12); Power page disclaimer ("no forced minimum processor state... no BIOS or voltage modifications") matches `processor-policy` implementation exactly; Network card's "The link may reset briefly" is accurate for `Set-NetAdapterAdvancedProperty`.

---

## 11. Testing Coverage

- **Existing automated tests: none.** There is no test project, no test framework package, no test scripts. The ~10 root PowerShell scripts are interactive UIA helpers used during development, not tests.
- **What the audit ran (non-destructive):**
  - Repository consistency checks — done (version pinning, artifact inventory, cross-references). Consistent.
  - Secret/credential scan — done. Clean.
  - `dotnet build` — **NOT POSSIBLE:** the audit machine has .NET runtimes (8.0.31, 10.0.9 incl. WindowsDesktop) but **no .NET SDK**; `dotnet build` fails with "No .NET SDKs were found." WiX is also absent. This is an environmental limitation, explicitly documented per the assignment rules.
  - Unit/integration tests, static analyzers, `dotnet format`, vulnerability scanning — **NOT POSSIBLE** for the same reason (no SDK ⇒ no `dotnet test`, no NuGet audit).
- **Nothing was verified at runtime.** All findings in §12 are from static inspection; each finding's "Status" column records whether runtime validation is still required. No claim of "passed testing" is made anywhere in this report.

---

## 12. Prioritized Findings

Severity scale: CRITICAL / HIGH / MEDIUM / LOW / INFORMATIONAL. "Confirmed" = proven from code; "runtime validation required" flagged where static proof is incomplete.

| ID | Severity | Location | Summary | Evidence | Impact | Recommendation | Status |
|---|---|---|---|---|---|---|---|
| SWT-001 | **HIGH** | [VisualEffectsOptimization.cs](Optimizations/VisualEffectsOptimization.cs) L86 vs L122–125 | Revert reads rollback JSON key `minAnimate` as string, but CapturePrevious saved the string form under `minAnimateStr` — a REG_SZ `MinAnimate` original value is never restored | `minAnimateStr = Rs(WmKey,"MinAnimate")` (capture) vs `GetStr(e, "minAnimate")` (revert) | Visual-effects rollback silently incomplete for systems whose `MinAnimate` is stored as REG_SZ (the common case under `WindowMetrics`) — violates the reversibility promise | Read `minAnimateStr` in Revert; add a round-trip unit test for the capture/revert JSON schema | Confirmed (static) |
| SWT-002 | **HIGH** | [MachineServices.cs](Services/MachineServices.cs) L107–118 + whole tree | `RestorePointService.TryCreate` (Checkpoint-Computer) is fully implemented but never called; no restore point is created before any system change | Grep: only definition, zero call sites | The app writes HKLM values, power policy, and driver settings with no OS-level safety net; contradicts "Safe. Reversible." branding more than any other gap | Call it once before the first elevated mutation per session (and surface failure honestly), or delete the service | Confirmed |
| SWT-003 | **MEDIUM** | [CleanupService.cs](Services/CleanupService.cs) L61 | `bool ok = reclaimed > 0 || failed > 0;` — total failure (everything in-use, 0 reclaimed) returns Success=true; success criterion verifies nothing | Line 61 vs message at L63–65 | False "success" reporting; undermines the verify philosophy for the cleanup feature | `ok = reclaimed > 0 || (failed == 0 && nothingExisted)`; report skipped-count separately from success | Confirmed |
| SWT-004 | **MEDIUM** | [StartupService.cs](Services/StartupService.cs) L88–107 | Machine-scope disable writes the HKCU backup *before* elevation; if UAC is cancelled the backup remains while the Run value is untouched → Enumerate() lists a phantom "Disabled" duplicate of a still-enabled entry | Backup write L88–93 precedes elevated removal L101–107; backup removed only in Enable() | Misleading state; user may believe a driver/system app was disabled when it was not; duplicate rows | Write backup only after successful removal (or delete it on failure) | Confirmed |
| SWT-005 | **MEDIUM** | [StartupService.cs](Services/StartupService.cs) L133 + [RegHelper.cs](Services/RegHelper.cs) L47–51 | Startup backup loses value *type*: Run values are frequently REG_EXPAND_SZ, but Enable() restores via `New-ItemProperty -PropertyType String` | Backup stores command string only (L90–92); restore forces String | Re-enabled entries containing `%ProgramFiles%`-style env references may break because they are restored as REG_SZ | Capture and restore `RegistryValueKind` | Confirmed |
| SWT-006 | **MEDIUM** | [PowerOptimizations.cs](Optimizations/PowerOptimizations.cs) L128–134, L160–164 | Processor-policy revert writes captured DC index even when it was unreadable (−1 sentinel) → `powercfg /setdcvalueindex ... -1` | `dc = doc...GetInt32()` with no `< 0` guard (AC has one, DC does not) | Possible invalid DC value write / restore failure on battery-context systems | Guard `dc < 0` (skip DC write like AC) | Confirmed (static); runtime powercfg behavior needs validation |
| SWT-007 | **MEDIUM** | [MainWindow.xaml.cs](MainWindow.xaml.cs) L485–503, L845–869 + [RollbackStore.cs](RollbackStore.cs) | No global operation lock: a preset run and a card toggle (or two toggles) can run the same `Optimization.Apply()` concurrently; `RollbackStore.Add` is read-modify-write last-wins → one operation's rollback record is lost; shared instance fields race | SetBusy is display-only; card toggle not disabled during presets; RollbackStore has no lock | Lost rollback entry = unrestorable change; duplicated elevated prompts; nondeterministic state | Gate all apply/revert through a single queue/semaphore; lock RollbackStore mutations | Confirmed (static) |
| SWT-008 | **MEDIUM** | [ProcessRunner.cs](Services/ProcessRunner.cs) L60–90 | Elevated-with-output writes to `%TEMP%` of the *elevating* account: over-the-shoulder elevation (standard user, different admin) ⇒ parent reads its own (nonexistent) temp file ⇒ every elevated verification reports false failure; `WaitForExit()` has no timeout (hung child = forever-busy UI task) | L66 `Path.GetTempPath()` evaluated in non-elevated parent; L78 unconditional WaitForExit | False negatives on multi-account machines; potential permanent hang of an apply | Use a shared, ACL'd location agreed via the encoded script (or named pipe); add kill-on-timeout | Confirmed (static) |
| SWT-009 | **MEDIUM** | [MainWindow.xaml.cs](MainWindow.xaml.cs) L326–333 | `_snapshotTask`/`_catalog` memoized for process lifetime → Dashboard specs, drive list, free space, power-plan label go stale after in-app changes (e.g., Cleanup) and never refresh | `??=` memoization; `OptimizationCatalog.Build(snapshot)` drive-bound; no invalidation anywhere | Stale/incorrect information displayed as current; storage page may show removed drives | Invalidate snapshot after cleanup/apply; or snapshot per navigation with cache TTL | Confirmed |
| SWT-010 | **MEDIUM** | [ProcessRunner.cs](Services/ProcessRunner.cs) L20–24 | `WaitForExit(15_000)` timeout ignored: on timeout the code reads `ExitCode` of a live process (throws → null) and never kills the child → orphaned processes | Timeout parameter discarded; no `Kill()` | Process leak; occasional spurious detection failure | Check `.waitForExit` return; kill on timeout | Confirmed |
| SWT-011 | **MEDIUM** | [FortniteAffinityOptimization.cs](Optimizations/FortniteAffinityOptimization.cs) L147–179, L47–83 | Enabling the watcher mid-session installs artifacts but nothing runs until next logon (no immediate start); the hidden watcher is a permanent background PowerShell polling every 500 ms until Fortnite appears; neither behavior is disclosed on the card | ApplyChange only writes files; script loops `while ($true)` | Feature appears "Active" but is inert this session; undisclosed resident process | Launch the watcher once on successful install; document the resident-watcher behavior | Confirmed |
| SWT-012 | **LOW** | [GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) L125–126 | Game Mode absent value (= Windows default, enabled) is reported NeedsAttention with pill "Needs attention" though nothing is wrong | `Detect()` maps null → NeedsAttention | Misleading status; invites a no-op write | Treat null as Optimized with "Enabled (Windows default)" for this positive-default setting | Confirmed |
| SWT-013 | **LOW** | [GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) L81–98 | When HKLM policy disables Game DVR but HKCU value is 1: state line says "Disabled (enforced by system policy)" while pill says "Needs attention" and toggle is offered | base Detect uses raw value; StateDescription checks policy | Confusing mixed signal; toggle write is harmless but pointless | Return Optimized when policy==0 regardless of HKCU value | Confirmed |
| SWT-014 | **LOW** | [StartupService.cs](Services/StartupService.cs) L60–61, L90 | Machine scope: enumerates Registry64 only (Wow6432Node Run invisible); backup stored in *current user's* HKCU so other accounts can't re-enable | Collect(hive,view) params; BackupKey under CurrentUser | Incomplete startup picture; cross-account restore impossible | Enumerate both views; store machine backups under HKLM (with the same elevation) | Confirmed |
| SWT-015 | **LOW** | [SafeLog.cs](Services/SafeLog.cs) | Log file grows unbounded; no rotation/levels | AppendAllText only | Eventual disk clutter; hard to diagnose | Size-based rotation or date-stamped files | Confirmed |
| SWT-016 | **LOW** | [RollbackStore.cs](RollbackStore.cs), [UserPreferences.cs](Services/UserPreferences.cs) | JSON stores rewritten non-atomically (WriteAllText), no file lock | Add/Remove/Write | Crash mid-write or concurrent access can corrupt rollback history (the app's core safety artifact) | temp+File.Move + lock | Confirmed |
| SWT-017 | **LOW** | [NetworkAdapterService.cs](Services/NetworkAdapterService.cs) L135, L164–171 | Adapter description / keywords interpolated into single-quoted PS strings without escaping | `'{adapterDescription}'` | Apostrophe in driver description breaks apply (correctness); theoretical injection requires malicious driver metadata | Escape `'` → `''` like StartupService does | Confirmed |
| SWT-018 | **LOW** | [WindowsDebloatOptimization.cs](Optimizations/WindowsDebloatOptimization.cs) L84–108 | ApplyChange returns `failures.Count == 0` after already verifying all keys read 0 — a key that failed to write but reads 0 (or raced) yields contradictory Failed-with-"verified" message | L99–107 | Inconsistent result semantics | Return true when re-read verification passes; report failures only if verification failed | Confirmed |
| SWT-019 | **LOW** | [CaptionButton.cs](CaptionButton.cs) | Caption buttons are mouse-only; no keyboard focus/Invoke pattern (FrameworkElementAutomationPeer without automation behavior) | No focusable/ButtonBase base | Keyboard/screen-reader users cannot minimize/maximize/close from keyboard | Base on real Button with WindowChrome exemption | Confirmed |
| SWT-020 | **LOW** | [MainWindow.xaml.cs](MainWindow.xaml.cs) L565–567 | `BuildNvidia` runs `powerOpt.Detect()` (NVAPI session) synchronously on the UI thread | Direct call in page builder | Minor UI stall on slow driver state reads | Move into the LoadPage await chain | Confirmed |
| SWT-021 | **LOW** | [SystemDetector.cs](Services/SystemDetector.cs) L37–58; [GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) L197–216; [MachineServices.cs](Services/MachineServices.cs) L107; [GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) L15–16 | Dead code: `ToDisplayText()`; `NvidiaProfileManualState` (documented intentional); `RestorePointService` (see SWT-002); unused `_previous/_previousJson` fields in HkcuDwordOptimization | Grep: no call sites | Maintenance noise; masks the missing restore-point wiring | Remove or wire up | Confirmed |
| SWT-022 | **LOW** | [CleanupService.cs](Services/CleanupService.cs) L82–90, L102–107 | Recycle-bin success = `exitCode==0 && reclaimed>0`; failed size measurement reads as "already empty" | MeasureRecycleBin returns 0 on failure | Inaccurate reporting edge case | Distinguish measure-failure from empty | Confirmed |
| SWT-023 | **INFO** | [README.md](README.md) | Stale title/claims (v1.1 boundary text, "replayable welcome animation") — see §10 | §10 | New maintainers misled | Rewrite README for v2 | Confirmed |
| SWT-024 | **INFO** | repo-wide | No tests, no build/test CI | §11 | Regressions undetectable | Add xunit project for catalog/rollback JSON round-trips + CI build | Confirmed |
| SWT-025 | **INFO** | [GameReadyOptimizer.csproj](GameReadyOptimizer.csproj) L28 | `NvAPIWrapper.Net 0.8.1.101` — third-party wrapper of proprietary NVAPI; low upstream activity; loads vendor nvapi64.dll | csproj | Supply-chain trust consideration; no alternative documented | Pin + periodic review; consider auditing wrapper source | Confirmed |
| SWT-026 | **INFO** | `release/`, `store-artifacts/`, `bin/`, `obj/` | 188 MB EXE, 58 MB MSI, PDBs, build outputs committed to the tree | file sizes | Repo bloat; binary/source drift risk | Keep artifacts out of source control | Confirmed |
| SWT-027 | **INFO** | [release-store.ps1](release-store.ps1) L108–110, L125 | Version hardcoded in three places (csproj + 2 script constants) | Script text | Release friction/miss risk | Derive from csproj | Confirmed |
| SWT-028 | **INFO** | [store/capture-screenshots.ps1](store/capture-screenshots.ps1) L9 | Hardcoded `c:\Users\Administrator\Desktop\...` output path | Script text | Dev-machine coupling | Use $PSScriptRoot | Confirmed |
| SWT-029 | **INFO** | [FortniteAffinityOptimization.cs](Optimizations/FortniteAffinityOptimization.cs) L107 | Watcher integrity check is marker-substring only; script is user-writable and auto-runs at logon | Inspect() | Weak tamper detection (no privilege gain for attackers) | Hash-pin script contents in detection | Confirmed |
| SWT-030 | **INFO** | repo root | No LICENSE file despite `Copyright (c) Swiftwave` in csproj | tree | Legal ambiguity for consumers | Add license | Confirmed |
| SWT-031 | **INFO** | [SingleInstance.cs](SingleInstance.cs) L35 | Abandoned mutex (post-crash) can throw `AbandonedMutexException` from the `Mutex` constructor at startup | TryAcquire | Rare crash-on-relaunch after abnormal exit | try/catch → treat as acquired or retry | Confirmed (static; runtime validation required) |
| SWT-032 | **INFO** | [MachineServices.cs](Services/MachineServices.cs) L28; [GamingOptimizations.cs](Optimizations/GamingOptimizations.cs) L109 | Runtime assumptions needing validation: `ReBAR\s*:\s*(\w+)` regex vs actual nvidia-smi field text; `UseNexusForGameBarEnabled` effectiveness on current Win11 builds; HAGS/`HwSchMode` default semantics | Static only | Possible wrong/blank NVIDIA facts or stale Game Bar state | Validate on a live NVIDIA + Win11 24H2/25H2 machine | Runtime validation required |

**No CRITICAL findings.** No evidence of destructive, security-breaking, or system-integrity-threatening behavior was found in the implementation.

---

## 13. Engineering Knowledge Base

A compact reference for any future engineer or coding agent working on Swiftwave Tweaks.

### Architectural overview
- Single .NET 8 WPF project (`GameReadyOptimizer.csproj` → assembly `SwiftwaveTweaks`), no DI/MVVM. Pages are built in code by `MainWindow`. All system-changing features derive from `Core.Optimization` and are registered in `OptimizationCatalog.Build()`. Persistence: `%LOCALAPPDATA%\SwiftwaveTweaks\{preferences.json, rollback.json, logs\activity.log}`.
- Elevation model: `asInvoker` + per-operation UAC via `ProcessRunner.RunElevatedPowerShell[WithOutput]` (base64-encoded scripts; success detected via explicit `WRITE:True`/`SETACTIVE:True`/`OPT:True` markers; `WithOutput` captures through a temp file).

### Critical classes
| Class | Responsibility |
|---|---|
| `Core.Optimization` | Apply/verify/revert protocol; auto-revert on failed verification; writes RollbackStore |
| `HkcuDwordOptimization` | Base for HKCU DWORD toggles with absence-aware rollback (GameDVR/GameBar/GameMode) |
| `OptimizationCatalog` / `Presets` | Instance registry; preset→member resolution ("storage" expands to all drives) |
| `MainWindow` + `OptimizationCard` | Navigation token race guard; page builders; per-card live detect/refresh |
| `ProcessRunner` | All subprocess/elevated execution |
| `RegHelper` | Typed registry IO with explicit absence handling |
| `NvidiaDrsService` | NVAPI DRS PREFERRED_PSTATE read/apply/restore (predefined vs custom) |
| `StartupService` / `CleanupService` / `NetworkAdapterService` / `SystemDetector` | Feature services (each documented at file head) |

### Feature implementation locations
- Gaming toggles: `Optimizations/GamingOptimizations.cs`. Power: `Optimizations/PowerOptimizations.cs`. Storage + catalog + presets: `Optimizations/StorageOptimization.cs`. Visual effects: `Optimizations/VisualEffectsOptimization.cs`. NVIDIA: `Optimizations/NvidiaPowerManagementOptimization.cs` + `Services/NvidiaDrsService.cs`. Network: `Optimizations/NetworkPowerSavingOptimization.cs` + `Services/NetworkAdapterService.cs`. Fortnite: `Optimizations/FortniteAffinityOptimization.cs`. Suggestions: `Optimizations/WindowsDebloatOptimization.cs`. Startup: `Services/StartupService.cs`. Cleanup: `Services/CleanupService.cs`. Diagnostics: `Core/DiagnosticsEngine.cs`. Detection: `Services/SystemDetector.cs` + `Services/MachineServices.cs`.

### Important execution flows
1. **Page load:** nav click → `NavigationChanged` (token++) → `CrossfadeTo` → `LoadPage(key, token)` → `await Task.Run(detection)` → token check → swap content.
2. **Apply:** card toggle → `ApplyOptimization` → `Task.Run(opt.Apply)` → base: Detect → CapturePrevious → ApplyChange → DetectAfterApply → (auto-revert if unverified) → RollbackStore.Add → `card.Refresh()`.
3. **Restore:** History row → `opt.Rollback(entry.PreviousState)` → on Applied, `RollbackStore.Remove`.
4. **Second launch:** mutex busy → pipe to first instance → `ActivateFromSecondInstance` (restore + `AttachThreadInput` foreground).

### Critical invariants
- **Never cache UI state as truth:** toggle positions and statuses must come from `Detect()` on live system state (this is enforced by design; preserve it).
- **Rollback JSON schema per optimization Id is load-bearing:** changing `CapturePrevious`/`Revert` field names breaks restores of previously recorded entries (SWT-001 is exactly this class of bug). Bump the Fortnite `ScriptMarker` when its script changes.
- **`RollbackStore.Add` keyed by Id:** re-apply overwrites the previous capture by design.
- **Elevated scripts must print explicit success markers** and be verified by independent re-read; `WriteDword` on HKCU only — HKLM goes through elevation.
- **Detection must never mutate** (except the currently-sequential `PowerPlanOptimization._target` / `StorageOptimization._appliedSuccessfully` instance fields — do not add more).

### Safety constraints (do not cross)
- No services/components/apps disabled or removed; no offload/RSS/flow-control writes; no BIOS/voltage/undocumented keys; Important-classified startup entries refused; `powercfg` limited to documented scheme/value-index operations; NVAPI limited to PREFERRED_PSTATE on base profile; cleanup limited to the three categories.

### Build & test instructions
- Build: `powershell -ExecutionPolicy Bypass -File .\build-release.ps1` (needs .NET 8 SDK on Windows). Release+MSI+Store: `release-store.ps1` (needs WiX v5, signing cert env vars, MSStore CLI + Entra env vars). No tests exist; if adding any, the highest-value first targets are the rollback JSON round-trips (capture→revert) per optimization.

### Known limitations
- No restore point is created (SWT-002); snapshot/catalog stale within a session (SWT-009); no concurrent-operation lock (SWT-007); visual-effects revert bug (SWT-001); processor DC sentinel (SWT-006); machine-scope startup quirks (SWT-004/005/014); elevated-output capture breaks with cross-account UAC (SWT-008); English-only; no signing on GitHub-distributed builds.

### Areas requiring caution when modifying
- `Core/Optimization.Apply()` ordering (capture *before* change) — do not reorder.
- `ProcessRunner` encoded-script plumbing (temp-file lifecycle, markers).
- Any new registry value: mirror the absence-aware pattern and extend `Detect`/`CapturePrevious`/`Revert` together.
- Startup service backup semantics (HKCU vs HKLM scope mismatch).
- Version bumps: csproj **and** `release-store.ps1` constants; Pages URLs are immutable once published.

---

*End of report. All findings are the product of full static inspection of the repository as of 2026-11-10; no runtime verification was performed (no .NET SDK available), and no code or system state was modified during this audit.*
