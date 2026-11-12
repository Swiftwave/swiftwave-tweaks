# Privacy Policy — Swiftwave Tweaks

**Effective date:** 12 November 2026
**Last updated:** 12 November 2026
**Applies to:** Swiftwave Tweaks for Windows (x64), version 3.1.0 and later

---

## 1. Summary

Swiftwave Tweaks is a local Windows desktop utility. It does not collect, transmit, sell, rent or share personal information.

- The application makes **no network connections**. There is no telemetry, analytics, advertising, crash reporting, account system, or automatic update check.
- Everything the application records stays on your own PC, inside your Windows user profile.
- Information is sent to a third party only if you personally choose to visit the project's GitHub pages in a web browser.

The rest of this policy explains exactly what the application reads, what it stores, where it is stored, and how to remove it.

---

## 2. Who this policy applies to

This policy applies to the Swiftwave Tweaks desktop application, its installer, and the copies distributed through GitHub Releases and GitHub Pages.

It does **not** apply to:

- GitHub, which hosts the download pages and issue tracker and has its own privacy statement.
- Microsoft Windows, which operates independently of this application.
- Other software you install or run alongside Swiftwave Tweaks.

**Publisher:** Swiftwave Tweaks is published under the project name "Swiftwave". This policy does not claim that a separate registered company, partnership or other legal entity operates the project.

---

## 3. Information the application processes

All processing described in this section happens **on your device**. None of it is transmitted anywhere.

### 3.1 System and hardware information

To show its dashboard and decide which optimizations are applicable, the application reads:

- Windows edition, version, build number and update revision.
- Processor name, core count and logical processor count.
- Graphics adapter name, driver version and driver date.
- Motherboard manufacturer and model.
- Installed memory total, plus per-module slot, capacity and speed.
- Drives: letter, volume label, model, bus type, total size and free space.
- Monitors: device name, resolution, current refresh rate and maximum refresh rate.
- Active power plan name and identifier.
- Windows settings relevant to gaming and power, such as hardware-accelerated GPU scheduling, Game DVR, Game Bar and Game Mode state.
- Current process count and system uptime.
- CPU load and physical memory usage, sampled once per second while the dashboard is open.
- NVIDIA GPU name, driver version, VRAM, temperature, power draw and Resizable BAR state, read through the locally installed `nvidia-smi` tool when an NVIDIA GPU is present.
- Network adapter descriptions and supported power-saving capabilities.
- Plug-and-play device identifiers used to match devices to drivers.

This information is used in memory to render the interface and evaluate optimization availability. Only the small subset described in section 4 is written to disk.

The application does **not** read your documents, photos, emails, browser history, passwords, keystrokes, clipboard, camera, microphone or location.

### 3.2 Application preferences

When you change a setting in the app, the following preferences are saved:

- Whether the welcome/onboarding sequence has been completed.
- Whether the app starts minimized when launched by Windows at sign-in.
- Whether closing the window keeps the app running in the notification area.
- Whether the app is registered to start with Windows.
- Your selected colour theme (Light, Dark, Swiftwave default, or follow Windows).

### 3.3 Change records and rollback history

Every optimization the app applies is recorded so it can be verified and reversed. These records contain:

- An identifier for the optimization and a transaction identifier.
- The timestamp of the change and whether it was applied, rolled back, or failed to roll back.
- The Windows setting that was changed and its previous and new values (for example a registry value or a scheduled-task state).

These records describe **system configuration**, not personal content. They are what allow the app to restore your previous settings.

### 3.4 Startup entries

The startup manager lists entries from the standard Windows startup registry locations. For entries not owned by Swiftwave Tweaks, it reads the entry name and command line so it can display and classify them.

If you disable another program's startup entry through the app, its original name, command and value type are backed up in your own registry so the entry can be restored.

### 3.5 Process information

The Fortnite-specific optimization watches for the process named `FortniteClient-Win64-Shipping` so it can apply a CPU affinity setting when the game launches. It reads the process name and identifier only. It does not inspect game files, game accounts or gameplay data.

### 3.6 Single-instance coordination

A second launch of the app signals the already-running instance through a local, session-scoped named pipe. The pipe name includes your Windows user name and session identifier, and is used only on your own machine to bring the existing window to the front. It is not an internet connection and carries no personal data beyond an activation signal.

---

## 4. Information stored on your device

| Location | Contents | When it exists |
|---|---|---|
| `%LOCALAPPDATA%\SwiftwaveTweaks\preferences.json` | The preferences listed in section 3.2 | After the app saves a setting |
| `%LOCALAPPDATA%\SwiftwaveTweaks\journal.jsonl` | Append-only transaction journal of applied, rolled-back and failed changes, including previous and new setting values | After the first optimization is applied |
| `%LOCALAPPDATA%\SwiftwaveTweaks\rollback.json` | Rollback entries: optimization name, previous state and timestamp | After the first verified optimization |
| `%LOCALAPPDATA%\SwiftwaveTweaks\logs\activity.log` | Diagnostic log described in section 7 | When the app logs an event |
| `%LOCALAPPDATA%\SwiftwaveTweaks\logs\activity.old.log` | The previous log after rotation | When the active log exceeds 5 MB |
| `%LOCALAPPDATA%\SwiftwaveTweaks\FortniteAffinity.ps1` | The watcher script for the Fortnite optimization | Only if you enable that optimization |
| `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\Swiftwave Fortnite Affinity.lnk` | A per-user Startup shortcut that runs the watcher | Only if you enable that optimization |
| `%LOCALAPPDATA%\SwiftwaveTweaks\v3-selftest.txt` and `ui-selftest.txt` | Self-test reports | Only if the app is started with its diagnostic self-test flags |
| `%TEMP%\SwiftwaveTweaks\swiftwave-ultimate-<id>.pow` | A temporary copy of the bundled power plan, used while importing it | Only during the power-plan import, then deleted |
| `%TEMP%\swt-elev-<id>.txt` | Output captured from an elevated PowerShell operation | Only during that operation, then deleted |

The application also writes registry data that is part of its normal operation:

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` — one value named `SwiftwaveTweaks`, present only while "start with Windows" is enabled.
- `HKCU\Software\SwiftwaveTweaks\StartupBackup` — backups of startup entries you disable through the app.

In addition, the optimizations you explicitly choose modify the specific Windows settings they describe, such as power plans, gaming policy values, network adapter power options, device interrupt and affinity settings, and NVIDIA driver profile settings. These are the product's intended function and are recorded so they can be reverted.

---

## 5. Network communications and third-party services

**The application performs no network communication.** It contains no code that opens an internet connection, and it does not check for updates automatically.

Specifically, the app does not:

- Send telemetry, usage statistics, diagnostics or crash reports.
- Contact an advertising, analytics or attribution service.
- Require or support a user account, sign-in or licence server.
- Download updates, content, drivers or configuration.
- Connect to GitHub, Microsoft or NVIDIA on your behalf.

Third-party components that may appear in or alongside the application:

- **Microsoft .NET runtime and Windows APIs** — included with the self-contained build. These are used locally and are not configured by this app to transmit data.
- **NvAPIWrapper** — an open-source library used to read and write supported NVIDIA driver profile settings through the locally installed NVIDIA driver. It does not transmit data.
- **GitHub** — the distribution channel. If you open a release page, the project website, or the issue tracker in a browser, GitHub processes that visit under its own privacy statement. Downloading the installer is your own action; the application is not involved.

The repository also contains maintainer-only release scripts that contact the GitHub API to publish releases. Those scripts are not part of the installed application and never run on a user's device.

Windows itself may perform network activity for its own features. That activity is controlled by Windows and Microsoft, not by Swiftwave Tweaks.

---

## 6. Cookies, tracking and advertising

The application sets no cookies, uses no tracking pixels, generates no advertising identifiers, and contains no advertising or profiling code.

---

## 7. Diagnostic information and logging

Swiftwave Tweaks keeps a plain-text activity log at `%LOCALAPPDATA%\SwiftwaveTweaks\logs\activity.log`.

The log records operational events such as:

- Which optimization was applied, verified, rolled back, or failed.
- Error messages produced by Windows, PowerShell or the NVIDIA driver.
- Cleanup results, such as megabytes reclaimed and files skipped.
- Startup registration and restore-point outcomes.

Log entries include a timestamp and a short description. They are written for troubleshooting on your own machine. **Logs are never uploaded, and there is no automatic log submission feature.**

The active log is rotated to `activity.old.log` when it exceeds 5 MB, and only one previous log is kept, so log storage is bounded.

---

## 8. Cleanup and deletion features

The cleanup tool deletes files only after you explicitly confirm the action, and only in these locations:

- Your per-user temporary folder (`%TEMP%`).
- The Windows temporary folder (`C:\Windows\Temp`), which requires administrator approval.
- The Recycle Bin, for all drives.

The tool does not touch documents, downloads, games, application data, browser profiles or Windows system files. Because emptying the Recycle Bin is permanent, treat any pending cleanup action as irreversible before you confirm it.

---

## 9. Data retention and deletion

Local records are kept until you remove them:

- Preferences, journal, rollback history, logs and the Fortnite watcher script remain under `%LOCALAPPDATA%\SwiftwaveTweaks` until that folder is deleted.
- Temporary files created during an operation are deleted by the app when the operation finishes.
- Uninstalling the application removes the installed program and its Start Menu shortcut. **It does not delete `%LOCALAPPDATA%\SwiftwaveTweaks`**, so your rollback history remains available if you reinstall.

To delete everything the application has stored:

1. Turn off any optimizations you want to reverse first, so the original Windows settings are restored.
2. Delete the folder `%LOCALAPPDATA%\SwiftwaveTweaks`.
3. If you enabled the Fortnite optimization, also delete `Swiftwave Fortnite Affinity.lnk` from your Startup folder.
4. Optionally remove `HKCU\Software\SwiftwaveTweaks` if you used the startup manager.

Because no data is transmitted, deletion on your device is complete deletion. There is no server-side copy to request.

---

## 10. Data security

- All application data remains on your device. There is no database, cloud storage or remote account to breach.
- Files are stored inside your Windows user profile and are protected by the access controls Windows applies to that profile. The application does not add its own encryption to these files.
- The application runs with administrator rights because changing system settings requires them. It requests administrator approval through the standard Windows User Account Control prompt.
- Before the first elevated change in a session, the app asks Windows to create a System Restore checkpoint where System Protection is enabled, so you have an additional recovery path.
- The app verifies changes after applying them and records enough information to revert them.

No method of storage is perfectly secure. You should protect your Windows account with a password or PIN and keep your device physically secure.

---

## 11. Children's privacy

Swiftwave Tweaks is a system configuration utility intended for general PC users. It is not directed to children, and it does not knowingly collect information from anyone, including children under 13. Because the application collects and transmits no personal information, there is no children's data to delete; removing the local folder described in section 9 removes all stored records.

---

## 12. International data transfers

There are none. The application does not transfer data across borders because it does not transmit data at all.

---

## 13. Your choices and rights

You control the application's data directly:

- Every optimization can be reverted from inside the app, which restores the recorded previous values.
- Preferences can be changed or reset at any time.
- All stored records can be deleted by removing the folder in section 9.
- The app can be uninstalled at any time through Windows Settings or Apps & Features.

Where data-protection law grants you rights of access, correction, deletion, restriction or objection, those rights concern data held by an organisation. Swiftwave Tweaks holds no such data about you, so the practical exercise of these rights is the local deletion described above.

---

## 14. Changes to this policy

This policy may be updated when the application's behaviour changes. The revised version will be published at the same public URL with a new "Last updated" date. If a future version of the application ever adds a feature that transmits data, this policy will be updated before or when that feature ships, and the change will be described here.

Policy version: 1.0

---

## 15. Contact

Questions about this policy or the application's data handling:

- **Issue tracker:** https://github.com/Swiftwave/swiftwave-tweaks/issues
- **Project page:** https://github.com/Swiftwave/swiftwave-tweaks

The issue tracker is the official contact channel for privacy questions about this application.
