# swiftwave tweaks.

Windows 11 desktop utility for conservative gaming-PC onboarding. The project intentionally favors supported Windows interfaces, verification, auditability, and reversibility over aggressive "booster" behavior.

## Build and package

On Windows 11 with the .NET 8 SDK installed, run:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

This first compiles Debug and then publishes a self-contained `win-x64` Release executable to `release/`. The publish does not need the .NET runtime installed on the target PC. The build script generates the included application icon before compilation.

## Implemented safely

- WMI/CIM-style hardware, memory, graphics, storage, Windows, and active power-plan detection.
- High-performance/Ultimate Performance activation with previous power-plan rollback.
- Game DVR policy disablement with an elevation prompt and activity history.
- Local logs, error containment, and rollback records under `%LOCALAPPDATA%`.
- Explicit manual/unsupported states instead of unsafe registry or GUI automation.
- A replayable Swiftwave Tweaks welcome animation with persisted startup preference and reduced-motion fallback.

## Current boundary

NVIDIA profile automation, HAGS, third-party overlays, display VRR, Startup impact, and cleanup estimation are intentionally manual/unsupported in v1.1 because the project does not use unsupported driver interfaces or destructive bulk actions.

The executable has not been code signed. Sign the Release executable with your organization’s code-signing certificate before external distribution.
