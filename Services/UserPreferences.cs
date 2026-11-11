using System.IO;
using System.Text.Json;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Persisted user preferences. The welcome animation is not configurable: it plays on every
/// application startup. (spec: settings tab carries run-at-startup / minimize-on-startup /
/// close-to-tray instead.)
/// </summary>
public sealed record UserPreferences(
    bool OnboardingCompleted = false,
    bool MinimizeOnStartup = false,
    bool CloseToTray = false,
    // Mirrors the HKCU Run entry so it can be repaired after the executable moves or is updated.
    bool RunOnStartup = false,
    // Light / dark / Swiftwave default / follow Windows. Default = the existing navy theme.
    AppTheme Theme = AppTheme.Default);

public static class PreferencesStore
{
    private static readonly string PathToPreferences = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwiftwaveTweaks", "preferences.json");
    public static UserPreferences Read()
    {
        try { return JsonSerializer.Deserialize<UserPreferences>(File.ReadAllText(PathToPreferences)) ?? new UserPreferences(); }
        catch { return new UserPreferences(); }
    }
    public static void Write(UserPreferences preferences)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathToPreferences)!);
            // Atomic write (temp file + move) so a crash mid-write can never corrupt the file.
            var tmp = PathToPreferences + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(preferences));
            File.Move(tmp, PathToPreferences, overwrite: true);
        }
        catch (Exception exception) { SafeLog.Write("Could not save preferences", exception); }
    }
}
