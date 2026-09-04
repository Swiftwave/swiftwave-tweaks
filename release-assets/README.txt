Swiftwave Tweaks 1.0.0
======================

Run "Swiftwave Tweaks.exe" on Windows 11 (x64). No .NET runtime or developer tools are required.

The app runs normally as a standard user and prompts through Windows UAC only when a selected system change requires elevation. It writes activity logs and rollback records under:
%LOCALAPPDATA%\SwiftwaveTweaks\

This application is unsigned unless your organization signs the produced executable after publishing. Windows SmartScreen may warn about unsigned software.

Safety: this tool does not disable Windows security, services, updates, firewall, BIOS/firmware functions, or apply undocumented tweaks. Unsupported capabilities are explicitly shown as manual.

Build from a Windows 11 machine with the .NET 8 SDK:
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
