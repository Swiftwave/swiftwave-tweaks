using System.IO;
using System.Reflection;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Access to the power-plan asset that must ship inside the executable. The `.pow` is an
/// <see cref="EmbeddedResource"/> with a fixed logical name, so the published single-file exe
/// never needs an Assets folder beside it. Extraction goes to a unique file under the user's
/// temp directory and the caller is responsible for deleting it.
/// </summary>
public static class PowerPlanAsset
{
    public const string ResourceName = "SwiftwaveTweaks.Assets.Swiftwave-Ultimate-Performance.pow";

    /// <summary>True when the asset is present in the executing assembly.</summary>
    public static bool IsEmbedded
    {
        get
        {
            try { return Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) is not null; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Writes the embedded plan to a collision-free temp file and returns its path.
    /// Returns null with an actionable message on any failure.
    /// </summary>
    public static string? ExtractToTemp(out string? error)
    {
        error = null;
        string? path = null;
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                error = "The bundled power plan is missing from this executable (resource '" +
                        ResourceName + "' not found).";
                return null;
            }

            string dir = Path.Combine(Path.GetTempPath(), "SwiftwaveTweaks");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, "swiftwave-ultimate-" + Guid.NewGuid().ToString("N") + ".pow");

            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.CopyTo(file);

            long length = new FileInfo(path).Length;
            if (length == 0)
            {
                error = "The bundled power plan extracted to an empty file.";
                TryDelete(path);
                return null;
            }

            return path;
        }
        catch (Exception ex)
        {
            if (path is not null) TryDelete(path);
            error = "Could not extract the bundled power plan: " + ex.Message;
            return null;
        }
    }

    /// <summary>Best-effort cleanup; never throws.</summary>
    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
