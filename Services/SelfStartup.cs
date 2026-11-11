using System.IO;
using Microsoft.Win32;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Swiftwave Tweaks' own per-user autostart entry.
///
/// This is deliberately separate from <see cref="StartupService"/>, which only manages *other*
/// applications' startup items. The Windows startup folder cannot be parsed reliably for an
/// elevated target, and HKLM\...\Run would require administrator rights to toggle, so the
/// per-user HKCU\...\Run value is used: no elevation needed to enable/disable, one value name
/// (so no duplicates are possible), and the command is the real executable
/// (<see cref="Environment.ProcessPath"/>, correct for a single-file apphost).
///
/// The registry is the authoritative state; the stored preference only exists so the entry can
/// be repaired after the executable moves (update / relocation).
/// </summary>
public static class SelfStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>The Run value name. One name per app — writing it can never create a duplicate.</summary>
    public const string ValueName = "SwiftwaveTweaks";

    /// <summary>
    /// Argument carried by the startup registration. It marks the process as a Windows sign-in
    /// launch, which is the only case where "Start minimized" is allowed to take effect —
    /// manual launches must always open in the foreground.
    /// </summary>
    public const string StartupArgument = "--minimized";

    public enum State
    {
        /// <summary>No startup entry.</summary>
        Disabled,
        /// <summary>An entry exists and points at this executable, which exists on disk.</summary>
        Enabled,
        /// <summary>An entry exists but points at a missing or different executable.</summary>
        Mismatched,
    }

    public readonly record struct Result(bool Success, string Message);

    /// <summary>The executable that is actually running (the apphost for single-file builds).</summary>
    public static string? ExecutablePath
    {
        get
        {
            string? path = Environment.ProcessPath;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
    }

    /// <summary>
    /// Command line written to the Run key. The executable is quoted so paths with spaces are
    /// parsed correctly, and <see cref="StartupArgument"/> marks the launch as a device startup.
    /// </summary>
    public static string? CurrentCommand
    {
        get
        {
            string? path = ExecutablePath;
            return path is null ? null : "\"" + path + "\" " + StartupArgument;
        }
    }

    public static string? RegisteredCommand
        => RegHelper.ReadString(RegistryHive.CurrentUser, RegistryView.Default, RunKeyPath, ValueName);

    public static State GetState()
    {
        string? command = RegisteredCommand;
        if (string.IsNullOrWhiteSpace(command)) return State.Disabled;

        string? target = TargetOf(command);
        string? current = ExecutablePath;
        if (target is null || current is null) return State.Mismatched;
        if (!File.Exists(target)) return State.Mismatched;
        return SamePath(target, current) ? State.Enabled : State.Mismatched;
    }

    /// <summary>True when the running executable sits in a build output folder, not an installed location.</summary>
    public static bool IsDevelopmentBuild()
    {
        string? path = ExecutablePath;
        if (path is null) return false;
        return path.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Enables or disables the startup entry. Idempotent: a no-op when already in the requested state.</summary>
    public static Result SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (RegisteredCommand is null)
                    return new Result(true, "Swiftwave Tweaks no longer starts with Windows.");
                RegHelper.DeleteValue(RegistryHive.CurrentUser, RunKeyPath, ValueName);
                return RegisteredCommand is null
                    ? new Result(true, "Swiftwave Tweaks no longer starts with Windows.")
                    : new Result(false, "The startup entry could not be removed. Check that the HKCU Run key is writable and try again.");
            }

            string? path = ExecutablePath;
            string? command = CurrentCommand;
            if (path is null || command is null)
                return new Result(false, "The executable path for this session could not be determined, so startup was not registered.");
            if (!File.Exists(path))
                return new Result(false, $"The running executable was not found at {path}. Startup was not registered.");
            if (IsTemporary(path))
                return new Result(false, "Swiftwave Tweaks is running from a temporary folder. Move it to a permanent location, then enable startup again.");

            if (string.Equals(RegisteredCommand, command, StringComparison.Ordinal) && GetState() == State.Enabled)
                return new Result(true, EnabledMessage());

            RegHelper.WriteString(RegistryHive.CurrentUser, RunKeyPath, ValueName, command);
            return GetState() == State.Enabled
                ? new Result(true, EnabledMessage())
                : new Result(false, "Windows did not keep the startup entry. Try again, or check the HKCU Run key permissions.");
        }
        catch (Exception exception)
        {
            SafeLog.Write("Startup registration failed", exception);
            return new Result(false, "Windows rejected the startup registration: " + exception.Message);
        }
    }

    /// <summary>
    /// Brings the registry in line with the stored preference on every launch and returns the
    /// preference that should be persisted. Repairs an entry that points at an old location,
    /// removes a stale entry when startup is off, adopts an entry created outside the app, and
    /// never creates a second value or deletes a working one.
    /// </summary>
    public static bool Reconcile(bool preferred)
    {
        try
        {
            var state = GetState();
            if (preferred)
            {
                // Enabled with the exact current command is fine; an older command (different exe
                // or missing the startup argument) is rewritten so the entry stays correct.
                if (state == State.Enabled &&
                    string.Equals(RegisteredCommand, CurrentCommand, StringComparison.Ordinal))
                    return true;
                var repair = SetEnabled(true);
                if (!repair.Success) SafeLog.Write("Startup entry could not be repaired: " + repair.Message);
                // Keep the user's choice even when this launch could not register it (e.g. the exe
                // runs from a temporary folder); the Settings switch shows the real state.
                return true;
            }

            // Preference is off. An entry that already points at this executable was created
            // outside the app, so adopt it rather than silently deleting the user's own choice.
            if (state == State.Enabled) return true;
            if (state == State.Mismatched)
            {
                // A dead path (moved, deleted or old copy) would try to launch nothing at sign-in.
                var removal = SetEnabled(false);
                if (!removal.Success) SafeLog.Write("Stale startup entry could not be removed: " + removal.Message);
            }
            return false;
        }
        catch (Exception exception)
        {
            SafeLog.Write("Startup reconciliation failed", exception);
            return preferred;
        }
    }

    private static string EnabledMessage() => IsDevelopmentBuild()
        ? "Registered for this Windows account — note that this is a development build path. Windows will ask for administrator approval (UAC) when it starts."
        : "Registered for this Windows account. Windows will ask for administrator approval (UAC) when it starts.";

    /// <summary>Extracts the executable from a Run command line (quoted or not).</summary>
    private static string? TargetOf(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            int end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }

    private static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    private static bool IsTemporary(string path)
    {
        try { return Path.GetFullPath(path).StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
