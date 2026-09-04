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
    bool CloseToTray = false);

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
        try { Directory.CreateDirectory(Path.GetDirectoryName(PathToPreferences)!); File.WriteAllText(PathToPreferences, JsonSerializer.Serialize(preferences)); }
        catch (Exception exception) { SafeLog.Write("Could not save preferences", exception); }
    }
}
