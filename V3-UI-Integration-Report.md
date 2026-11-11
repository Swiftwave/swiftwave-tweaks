# Swiftwave Tweaks V3 — UI Integration Report

Date: 2026-11-11
Published artifact: `publish\SwiftwaveTweaks.exe` — 188,431,596 bytes (179.7 MiB),
SHA-256 `059D8574ADCC08E95831337033DF40CF47B158C70E06F6DB7FC2FF600765AAE5`.
Publish directory contains exactly one file and no subdirectories.

> **2026-11-11 refinement pass.** The navigation described in §1 was superseded: the
> **Gaming** page is now **Windows**, **NVIDIA** is now **GPU**, and the **System** page was
> removed with its two features redistributed (Debloat → Windows, Storage & Memory → Storage).
> See §10–§13 for the current structure, the tray menu rework, the power-mode description and
> the CPU core-parking audit. Sections §1–§9 are retained for history; where they name the old
> pages, §10 is authoritative.
>
> **2026-11-11 dashboard pass.** The Dashboard page was rebuilt around live telemetry, quick
> actions and a six-card hardware overview; see §14. No optimization logic, journal, preset or
> catalog entry changed in that pass.
>
> **2026-11-11 theme pass.** A saved colour-theme setting (Default / Light / Dark / Follow
> system) now drives every surface and text colour in the application; see §15.


## 1. What was added to the UI

A new **System** navigation page was added between Storage and Network. It hosts the two
system-level features (Storage & Memory, Debloat). Gaming now has a dedicated
**Performance & latency** section with the four V3 gaming features in the specified order,
the obsolete `power-plan` card is no longer rendered, and the Network page gained the
Windows TCP/IP feature independently of adapter presence.

Feature names are now identical in cards, catalog registration, presets, logs and this report:

| # | Page | Feature name | Id | Category |
|---|---|---|---|---|
| 1 | Gaming | General Windows FPS & Input Delay Optimizations | `v3-fps-input-lag` | Gaming |
| 2 | Gaming | Optimize Device Affinities | `v3-device-affinity` | Gaming |
| 3 | Gaming | Optimize Message Signal Interrupts | `v3-msi` | Gaming |
| 4 | Network | Optimize Network Performance | `v3-network-performance` | Network |
| 5 | Gaming | Disable Dynamic Interrupt Steering | `v3-dynamic-interrupt-steering` | Gaming |
| 6 | System | Optimize Storage & Memory | `v3-storage-memory` | System |
| 7 | System | Debloat Windows & Reduce Background Activity | `debloat` | System |
| 8 | Power | Enable Maximum Performance Power Mode | `v3-max-performance-power` | Power |

## 2. Registration and wiring

- All eight remain members of the single existing `Optimization` catalog
  (`OptimizationCatalog.Build`); no parallel framework was introduced.
- Every card is the existing `OptimizationCard`, so Apply → Verify → Journal → Restore,
  elevation, status detection, warnings, tags and logging are the existing lifecycle.
- Page membership is driven by `Category`, with explicit id ordering for the Gaming V3
  section. `BuildPagesForTest` builds the exact same page trees headlessly.
- `Declarative cards`: retired `power-plan` and `windows-suggestions` stay registered under
  `Category = "Legacy"` so History & Restore can still resolve old rollback records, but no
  page filter matches `Legacy`, so they never render.

## 3. State detection

- `V3State.Evaluate`/`JournalDetection` remain authoritative: a matching registry value
  without a committed journal transaction is reported as **not applied**.
- `E-FOREIGN-EDIT` now renders as **Incomplete** and `E-JOURNAL-CORRUPT` as **Unverifiable**;
  both are visually distinct from **Needs attention**.
- Unsupported hardware is shown with its reason text (e.g. `E-DRV-NO-AFFINITY`,
  `E-DRV-NO-MSI`, `E-VM`, `E-CLASS-AMBIGUOUS`) and a disabled control, never hidden.
- Permission failure in the FPS feature: `bcdedit` requires elevation, so a non-elevated
  session now reports `Unknown` with "Administrator permission is required to read the boot
  configuration…" instead of incorrectly claiming the machine is unsupported.
- While an apply/restore runs, the card shows **Working…** and disables its control; the
  global status line shows the operation. Refresh always restores the real detected state.

## 4. Debloat consolidation

One card (`debloat`) owns promotional-content suppression, Hone's service reduction and
background-activity reduction. Legacy `windows-suggestions` rollback data is adopted as the
prior state on first apply and the legacy record is removed after success, so rollback
restores the true pre-optimization values. Gated/optional operations (Print Spooler,
Windows Search, SharedAccess, etc.) stay excluded and are described in the card.

## 5. Recommended presets

- **Recommended**: maximum-performance power mode, FPS/input-delay profile, Game DVR off,
  Game Bar off, Game Mode on, default visuals, storage optimization. No device, service or
  network tuning.
- **Gaming performance**: all five gaming/network V3 features plus power, Game DVR/Bar/Mode,
  processor policy, balanced visuals and storage.
- **Balanced**: maximum-performance power mode, Game Mode, default visuals.
- `power-plan` and `windows-suggestions` were removed from every preset; presets resolve
  with no duplicates (asserted by tests).

## 6. Preserved behaviour

Journal/ownership substrate, topology-derived affinity masks with `E-GRP-GT64` and
`E-HYBRID-AMBIGUOUS`, typed registry restore, absent-target skipping, reverse-order partial
failure undo, service/task state capture, power-scheme D-1 rollback, single-instance guard,
tray behaviour, preferences, logging and the single-file publish configuration are unchanged.

## 7. Tests

| Test | Result |
|---|---|
| Debug build | 0 warnings, 0 errors |
| Release build | 0 warnings, 0 errors |
| `--v3-selftest` (Debug) | 98 passed, 0 failed |
| `--ui-selftest` (Debug) | 84 passed, 0 failed |
| `--v3-selftest` (published exe, run from `%TEMP%`) | 98 passed, 0 failed |
| `--ui-selftest` (published exe, run from `%TEMP%`) | 84 passed, 0 failed |
| Published exe creates no files beside itself | pass (1 file before and after) |
| Rendered UI inspection (published exe, UI Automation + screenshots) | Gaming, System, Power, Network and Recommended pages verified |

The UI self-test constructs the real `MainWindow` and page trees and asserts: eight features
registered once with the contract names/categories, each on exactly one page, retired cards
never rendered, every card has a description and a legal detected state, presets resolve
without duplicates or retired ids, and Recommended excludes advanced device/service tuning.

A real integration defect was found and fixed during this validation: the `system` key had no
arm in `LoadPage`, so the page silently fell through to the loading placeholder.

## 8. Feature verification matrix

| Feature | UI integrated | Apply tested | Verify tested | Restore tested | Elevated tested | Status |
|---|---|---|---|---|---|---|
| General Windows FPS & Input Delay Optimizations | Yes | No | Detection only | No | No | Implemented but not runtime-verified |
| Optimize Device Affinities | Yes | No | Detection only | No | No | Implemented but not runtime-verified (unsupported on test laptop: `E-DRV-NO-AFFINITY`) |
| Optimize Message Signal Interrupts | Yes | No | Detection only | No | No | Implemented but not runtime-verified (unsupported on test laptop: `E-DRV-NO-MSI`) |
| Optimize Network Performance | Yes | No | Detection only | No | No | Implemented but not runtime-verified |
| Disable Dynamic Interrupt Steering | Yes | No | Detection only | No | No | Implemented but not runtime-verified |
| Optimize Storage & Memory | Yes | Blocked (not elevated) | Detection only | No | No | Implemented but not runtime-verified |
| Debloat Windows & Reduce Background Activity | Yes | No | Detection only (legacy adoption covered by selftest) | No | No | Implemented but not runtime-verified |
| Enable Maximum Performance Power Mode | Yes | No | Detection only (laptop path) | No | No | Implemented but not runtime-verified |

"Apply/Restore tested" refers to the actual feature through the UI. Backend journal,
partial-failure, foreign-edit and restore-fidelity tests pass, but they are not a substitute
for elevated end-to-end runs.

## 9. Remaining limitations / unverified operations

1. No elevated apply → verify → restore cycle was executed for any of the eight features
   through the UI; the development machine is not disposable.
2. The desktop `powercfg /import` branch of the power feature was not exercised (test machine
   classifies as laptop); the embedded `.pow` extraction itself is tested (hash, size, cleanup).
3. No clean Windows 11 x64 machine without .NET and without NVIDIA drivers was available.
4. BCD-based FPS detection is only meaningful when elevated; the elevated detection path was
   not exercised.
5. Cross-feature conflicts and external modification are covered by backend tests but not by
   interactive UI runs.

## 10. Navigation structure (2026-11-11 refinement)

| Nav label | Key | Contents |
|---|---|---|
| Dashboard | `dashboard` | live state |
| Recommended | `recommended` | presets |
| **Windows** (was *Gaming*) | `windows` | General (`fortnite-affinity`), Performance & background activity (FPS → **Debloat** → Device Affinities → MSI → Dynamic Interrupt Steering), Windows features, Visual effects |
| **GPU** (was *NVIDIA*) | `gpu` | NVIDIA power management + NVIDIA tuning card |
| Power | `power` | `processor-policy`, `v3-max-performance-power` |
| Storage | `storage` | Drives (`storage-*`) + **Storage & memory** (`v3-storage-memory`) |
| Network | `network` | adapter card, `v3-network-performance` |

The **System** page was removed. Both of its cards keep their original implementations,
optimization ids, journal records and rollback code; only the `Category` property (the tab a
card belongs to) changed:

- [DebloatOptimization.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Optimizations/DebloatOptimization.cs) — `Category` `"System"` → `"Gaming"`; rendered by fixed id order in
  [MainWindow.xaml.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/MainWindow.xaml.cs) `V3WindowsIds` (`v3-fps-input-lag`, `debloat`,
  `v3-device-affinity`, `v3-msi`, `v3-dynamic-interrupt-steering`).
- [V33WwEZo0M.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Core/V33WwEZo0M.cs) — `Category` `"System"` → `"Storage"`; rendered in the Storage page's
  dedicated "Storage & memory" section after the drive cards.

Obsolete references were updated in `PageDefs`, `LoadPage`, `BuildPagesForTest` and
[DiagnosticsEngine.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Core/DiagnosticsEngine.cs) ("Gaming page" → "Windows page"). No card is duplicated: the
`ui` self-test asserts each V3 id appears on exactly one page, and adds an explicit
FPS → Debloat → Device Affinities ordering check.

## 11. Tray context menu

`TrayIcon` still uses a real WPF `ContextMenu` (native popup semantics: cursor placement,
outside-click dismissal, Escape, keyboard navigation) but it is now fully custom-templated:

- [App.xaml](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/App.xaml) `SwiftwaveTrayMenu` — transparent popup host with a `#141F2E` surface, `#243345`
  border, `CornerRadius="10"`, soft shadow, `Segoe UI Variable Text` (same as the application),
  `SnapsToDevicePixels`/`UseLayoutRounding` for non-100 % scaling.
- `SwiftwaveTrayMenuItem` — **no icon/check column** (the stock template's icon column was the
  "two white squares"), `Padding="10,7"`, `CornerRadius="6"`, `#2E4259` hover, `#1C2A3B` pressed,
  45 % opacity when disabled.
- `SwiftwaveTraySeparator` — 1 px `#243345` hairline between Open and Exit.
- [TrayIcon.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Services/TrayIcon.cs) `OpenMenu()` builds the menu once and opens it at the cursor. All commands
  (`Open`, `Exit`) are unchanged. `Shell_NotifyIcon` now imports the **Unicode** entry point;
  the previous ANSI import with Unicode `NOTIFYICONDATA` marshaling truncated the tray tooltip
  to its first character.

Verified on Windows 11 26200 in a high-DPI multi-monitor session (icon inside the hidden-icons
flyout): menu
`ContextMenu` window 189×117 DIP, exactly two `MenuItem` peers (`Open`, `Exit`); `Open` restored
the window (`IsWindowVisible=True`), closing it returned it to the tray, `Exit` terminated the
process (`HasExited=True`). Screenshots show no white squares, dark blue surface, rounded
corners and the blue hover fill.

## 12. Power-mode description

[V3K6ad89bg.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Core/V3K6ad89bg.cs) `Description` is now exactly
`Applies Swiftwave's optimal power plan tailored for your PC.`; detection, import/duplicate,
verification and rollback are untouched.

## 13. CPU core-parking overlap audit

### 13.1 What the code actually writes

Only one optimization touches processor power management at all: **Enable Maximum Performance
Power Mode** (`v3-max-performance-power`, [V3K6ad89bg.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Core/V3K6ad89bg.cs)).

Desktop path (`FormFactor.Desktop`, line 113): extracts the embedded `Swiftwave-Ultimate-Performance.pow`,
imports it, renames it, then calls `FirstMismatch` (line 292) to verify all 27 AC/DC rows
(lines 383-411) and activates it. The shipped values include (SUB_PROCESSOR
`54533251-82be-4824-96c1-47b60b740d00`):

| Setting | GUID | Swiftwave AC/DC | Windows High Performance default |
|---|---|---|---|
| Processor performance core parking min cores (CPMINCORES) | `0cc5b647-c1df-4637-891a-dec35c318583` | **100 / 100** | 100 / 100 |
| … min cores for Efficiency Class 1 (CPMINCORES1) | `0cc5b647-c1df-4637-891a-dec35c318584` | **100 / 100** | 0 / 0 |
| Core parking increase time (CPINCREASETIME) | `2ddd5a84-5a71-437e-912a-db0b8c788732` | 1 / 7 | 7 / 7 |
| Core parking parked performance state (CPPERF) | `447235c7-6a8d-4cc0-8e24-9eaf70b96e2b` | 1 / 1 | 0 / 0 |
| … parked performance state Class 1 (CPPERF1) | `447235c7-6a8d-4cc0-8e24-9eaf70b96e2c` | 2 / 1 | 0 / 0 |
| Processor performance time check interval (PERFCHECK) | `4d2b0152-7d5c-498b-88e2-34345392a2c5` | 5000 / 5000 ms | 15 / 15 ms |
| Latency sensitivity hint min unparked cores Class 1 | `616cdaa5-695e-4545-97ad-97dc2d1bdd89` | 25 / 0 | 0 / 0 |

Source of the defaults: `v3 functionality pack\Hone-PowerPlan-V5\Hone-Ultimate-Power-Plan-V5-Comparison.csv`
rows 10-18 (runtime hive export + shipped `.pow` verification).

Laptop path (line 152): `PowerSchemeService.DuplicateScheme(HighPerformanceGuid)` and activate.
No processor value is written by Swiftwave; the copy inherits whatever the local
`8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c` scheme contains, and the 27 rows are **not** verified on
this path.

`Processor frequency policy (max 100 %)` ([PowerOptimizations.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Optimizations/PowerOptimizations.cs) lines 139-150) writes only
`bc5038f7-23e0-4960-96da-33abaf5935ec` (PROCTHROTTLEMAX) = 100 AC/DC. No other optimization
reads or writes `HKLM\SYSTEM\CurrentControlSet\Control\Power` — grepping the whole source for
`54533251`, `powercfg` and the parking GUIDs returns only these two files plus
`PowerSchemeService`.

### 13.2 Findings

| Existing optimization | Relevant setting | Effect on core parking | Explicitly disables it? |
|---|---|---|---|
| Enable Maximum Performance Power Mode — desktop import | CPMINCORES `0cc5b647-…583` = 100 AC / 100 DC | Minimum unparked cores forced to 100 % → nothing is left parkable | **Yes** |
| Enable Maximum Performance Power Mode — desktop import | CPMINCORES1 `0cc5b647-…584` = 100 AC / 100 DC | Same for Processor Power Efficiency Class 1 (E-cores) | **Yes** |
| Enable Maximum Performance Power Mode — desktop import | CPINCREASETIME `2ddd5a84-…` = 1 AC / 7 DC | Unparking allowed after 1 perf-check interval on AC instead of 7 | No — influences |
| Enable Maximum Performance Power Mode — desktop import | CPPERF `447235c7-…2b` = 1/1, CPPERF1 `447235c7-…2c` = 2/1 | Changes the P-state a parked core enters; does not change how many park | No — influences (policy only) |
| Enable Maximum Performance Power Mode — desktop import | PERFCHECK `4d2b0152-…` = 5000 ms | Parking/unparking is re-evaluated every 5 s instead of 15 ms | No — influences |
| Enable Maximum Performance Power Mode — desktop import | LATENCYHINTUNPARK1 `616cdaa5-…` = 25 AC / 0 DC | At least 25 % of Class-1 cores stay unparked while a latency hint is active on AC | No — influences |
| Enable Maximum Performance Power Mode — laptop duplicate | none written | Copies the local High Performance scheme; parking follows that scheme (typically CPMINCORES 100/100 on stock Windows) | No — inherited, not written |
| Processor frequency policy (max 100 %) — Advanced | PROCTHROTTLEMAX `bc5038f7-…` = 100 AC / 100 DC | Caps P-state at 100 %; parking is an independent control | No — no interaction |
| General Windows FPS & Input Delay Optimizations | BCD/`Dwm`/system timers, not `Control\Power` | None | No — no interaction |
| Debloat Windows & Reduce Background Activity | services / tasks / policies | None | No — no interaction |
| Optimize Device Affinities | IRQ `Affinity Policy` registry | CPU *interrupt routing*, not core parking | No — no interaction |
| Optimize Message Signal Interrupts | `MessageSignaledInterruptProperties` | None | No — no interaction |
| Disable Dynamic Interrupt Steering | device interrupt policy | None | No — no interaction |
| Optimize Network Performance | TCP/IP registry | None | No — no interaction |
| Optimize Storage & Memory | SvcHost threshold, NTFS | None | No — no interaction |
| Storage Optimize Drives | `defrag`/retrim | None | No — no interaction |
| NVIDIA power management / GPU tuning | NVAPI P-state preference | GPU clocks, not CPU parking | No — no interaction |
| Network adapter power saving | NIC power management | None | No — no interaction |

### 13.3 Direct answer

**Yes — but only on the desktop path of `Enable Maximum Performance Power Mode`.** Its imported
plan sets *Processor performance core parking min cores* and *…min cores for Efficiency Class 1*
to 100 % on both AC and DC, which leaves Windows no parkable cores (effective parking disabled),
and it also changes the parking timing/threshold inputs. No other optimization disables or
modifies core parking. On the laptop path Swiftwave writes no parking value at all; it only
duplicates the local High Performance scheme, so the outcome depends on that scheme and is not
asserted by Swiftwave. On this audit machine (i5-13500H, Windows 11 26200) `powercfg -q` exposes
only Minimum/Maximum processor state in SUB_PROCESSOR — the core-parking settings are hidden
here — so the audit evidence is the shipped plan plus its verified hive comparison, not a local
readback.

### 13.4 Limitations found by the audit

1. The Power page intro previously claimed "no global core-parking changes"; that phrase has
   been removed from the page copy now that §13.1 documents the desktop plan's CPMINCORES
   values. The intro now reads: "Only supported, verifiable Windows power policies are exposed.
   No forced minimum processor state, no forced maximum frequency, no BIOS or voltage
   modifications."
2. The laptop path inherits High Performance's parking values without verifying them, so the
   status card cannot claim whether parking is on or off on a given laptop.
3. Core-parking GUIDs are not exposed by `powercfg` on this machine, so the desktop branch's
   values could not be read back locally; verification relies on the shipped plan and its
   recorded comparison.

## 14. Dashboard redesign (2026-11-11)

### 14.1 Files

| File | Change |
|---|---|
| [MainWindow.xaml.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/MainWindow.xaml.cs) | `BuildDashboard` replaced; new intro, quick actions, live panel and hardware grid; `BuildPcSpecs` removed; telemetry lifecycle (dispose on navigation and close) |
| [Services/LiveTelemetry.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Services/LiveTelemetry.cs) | New: one-second CPU / memory / uptime sampler on a thread-pool timer |
| [RingGauge.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/RingGauge.cs) | New: circular percentage gauge with an unavailable "—" state |
| [Ui.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Ui.cs) | `Ui.Icon` stroke-only 24×24 line-icon factory |
| [Services/SystemDetector.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Services/SystemDetector.cs) | `MotherboardVendor` / `MotherboardModel` from `Win32_BaseBoard` added to `SystemSnapshot` |

No optimization implementation, journal record, preset definition or catalog registration was
touched; `Apply Recommended` only navigates to the existing Recommended page.

### 14.2 Structure

1. **Introduction** — "Your PC, fully tuned.", the coverage ring and the coverage caveat.
2. **Quick actions** — three cards on one row:
   * *Apply recommended* — counts recommended members that are still `Selectable` and not
     `Optimized`, then selects the Recommended navigation item. It applies nothing itself.
   * *Restore point* — calls the existing `RestorePointService.TryCreate`, with disabled state and
     "Creating…" while running, then a green/amber result line.
   * *Clean junk* — calls the existing `CleanupService` for the `user-temp` category only;
     documents, downloads, application data and the Recycle Bin are untouched.
3. **Live system load** — CPU and memory rings, uptime, used/total RAM, and a LIVE indicator.
4. **Your hardware** — Processor, Graphics, Memory, Storage (system drive + usage bar),
   Motherboard, Windows, plus the real administrator state.

**Coverage rule.** The denominator is every non-Legacy optimization that is either already
`Optimized` or currently `Selectable`; the numerator is those in the `Optimized` state.
Unsupported and manual items are excluded from both sides, while unknown, partially configured
(`NeedsAttention`) or unapplied items count as inactive. It is derived from the live detection
results, never from stored values.

The page renders from the cached hardware snapshot straight away (gauges, quick actions and
hardware cards), while the coverage ring, the "N of M" line and the recommended count show neutral
placeholders until detection finishes. The update is guarded by the navigation token, so leaving
the page before detection completes cannot write into a page that is no longer visible.

### 14.3 Telemetry

`LiveTelemetry` samples `GetSystemTimes` deltas (kernel time includes idle time, so busy time is
`kernel + user − idle`), `GlobalMemoryStatusEx` and `Environment.TickCount64` once per second.
Sampling happens on a `System.Threading.Timer` thread and the result is marshalled to the UI with
`Dispatcher.BeginInvoke`. Availability is tracked per metric: a failed CPU or memory read renders
"—" instead of 0 % and, when both fail, the pill reads `UNAVAILABLE`. The sampler is disposed when
navigating away from the dashboard, when the dashboard is rebuilt, when the window is hidden to the
tray (the dashboard is rebuilt on return so the gauges restart), and on window close.

### 14.4 Verification

- Debug and Release builds: **0 warnings, 0 errors**.
- `--ui-selftest`: **82 passed, 0 failed**; `--v3-selftest`: **111 passed, 0 failed**
  (Release, elevated).
- UIA inspection of the running dashboard (elevated) found the expected peers: coverage 37 %
  ("7 of 19 applicable optimizations are active"), LIVE, CPU/Memory values, the three buttons,
  and all six hardware cards with real values (i5-13500H, RTX 4050 Laptop, 16 GB, C: 475 GB,
  HP 8BB1, Windows 11 Pro 25H2 build 26200.9711).
- Two captures three seconds apart showed different CPU and memory percentages and RAM totals,
  confirming the one-second refresh.
- UIA `Invoke` on *Open Recommended* changed the page title to **Recommended** and left every
  preset member in its previous state — navigation only, no application.
- The published single-file `publish\SwiftwaveTweaks.exe` starts, shows the dashboard and
  responds normally.
- Screenshots: `.screenshots\dashboard-live-1.png`, `.screenshots\dashboard-live-2.png`,
  `.screenshots\dashboard-after-recommended.png`.

### 14.5 Remaining limitations

1. GPU VRAM is not shown: WMI `AdapterRAM` is unreliable above 4 GB and NVAPI only covers NVIDIA;
   the card shows the adapter name and driver version instead.
2. Memory speed is the WMI *configured* speed, not the module's rated maximum.
3. The restore-point action inherits the existing service behaviour: if System Protection is
   disabled for the system drive, Windows returns a failure and the card shows it in amber.
4. The cleanup action is limited to the current user's temporary files by design.
5. `build-release.ps1` still expects a `release-assets\` folder that is absent from this
   workspace, so its final copy step fails even though `dotnet publish` and the single-file
   output succeed. The published artifact was produced with the same publish arguments directly.
6. UIA click-through was verified for *Open Recommended* only; *Create restore point* and
   *Clean temporary files* were not executed in testing to avoid changing system state. Their
   wiring is a direct call to the existing services.

## 15. Colour themes (2026-11-11)

### 15.1 Options

| Option | Meaning |
|---|---|
| **Default** | The existing Swiftwave navy identity. Enum value 0, so old preference files keep this look. |
| **Light** | White cards on a light window, near-black (`#11161D`) primary text, darker semantic accents for contrast. |
| **Dark** | Neutral charcoal surfaces without the navy tint. |
| **Follow system** | Reads `HKCU\…\Themes\Personalize\AppsUseLightTheme` and applies Light or Dark; re-applies when Windows broadcasts a theme change. |

The choice is persisted as `Theme` in `preferences.json` (numeric enum; missing value reads as
Default). It is applied in `App.OnStartup` **before** the window is created, so the first frame
and the welcome overlay already use it.

### 15.2 How it stays consistent

- [Theme.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Theme.cs) holds one
  `ThemePalette` per mode (42 colour roles) and `ThemeService.Apply`, which writes the palette to
  two places:
  1. [Ui.cs](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/Ui.cs) — the brushes are now
     **unfrozen singletons** mutated in place, so every already-rendered code-built element
     recolours without rebuilding.
  2. The `Application.Resources` brushes consumed by `DynamicResource` in
     [App.xaml](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/App.xaml) (button,
     navigation, scrollbar, tray-menu templates) and
     [MainWindow.xaml](file:///c:/Users/alanz/Desktop/swiftwave-tweaks-tweaks/MainWindow.xaml)
     (window/sidebar/welcome surfaces, title, status, version, busy dots).
- Caption buttons keep cloned icon strokes, so `ThemeService` also rewrites their shared colours
  and each instance resyncs its glyph.
- `ThemeService.Changed` makes the window rebuild the visible page, which refreshes controls whose
  colours are local clones (toggle thumbs) or were animated (hover accents). Navigation, size and
  the detection cache are untouched.
- Linear colour animations that previously hard-coded dark hex values (nav hover/selected,
  scrollbar thumb) were replaced with `DynamicResource` setters so they cannot fight the theme.

### 15.3 Settings UI

The Settings page opens with an **Appearance** section: four selectable tiles (Default, Light,
Dark, Follow system) with the active one outlined in the accent colour. Selecting a tile saves the
preference, applies it immediately and returns to the same page under the new palette.

### 15.4 Verification

- Debug and Release: **0 warnings, 0 errors**; `--ui-selftest` and `--v3-selftest`: **0 (pass)**
  on both the Release build and the published single-file EXE.
- UIA click-through in the running app captured Default, Light, Dark, Follow system (resolved to
  Dark on this `AppsUseLightTheme=0` machine) and the restored Default.
- A fresh launch after choosing Light started light before any navigation, confirming persistence.
- Recommended and Dashboard pages were captured under Light: all card surfaces, pills, gauges,
  quick-action buttons and hardware text follow the palette.
- Screenshots: `.screenshots\settings-default.png`, `settings-light.png`, `settings-dark.png`,
  `settings-system.png`, `settings-final.png`, `dashboard-light.png`, `recommended-light.png`,
  `startup-light.png`.
- The published EXE still produces exactly one file in `publish\`.

### 15.5 Remaining limitations

1. The welcome overlay's two decorative radial glows keep their blue/violet tint in Light mode;
   they are 11–16 % opacity and do not affect text contrast.
2. "Follow system" deliberately maps dark Windows to the neutral **Dark** palette rather than
   **Default**, so the setting always means the system's light/dark mode.
3. The taskbar/tray icon is a fixed multi-colour `.ico` asset and is not recoloured.

