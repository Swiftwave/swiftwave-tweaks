using Microsoft.Win32;

namespace SwiftwaveTweaks.Services;

/// <summary>Convenience reader for the HKLM CurrentVersion key.</summary>
public static class RegistryRead
{
    public static string CurrentVersionValue(string name, string fallback) =>
        RegHelper.ReadString(RegistryHive.LocalMachine, RegistryView.Registry64, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", name) ?? fallback;
}

/// <summary>Small typed helpers around registry reads/writes with explicit absence handling.</summary>
public static class RegHelper
{
    public static object? ReadValue(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
            // DoNotExpandEnvironmentNames keeps REG_EXPAND_SZ reversible: capturing the expanded
            // value would rewrite the user's %VARS% as a literal path on restore (defect D-2 class).
            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch { return null; }
    }

    public static bool ValueExists(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
            return key?.GetValueNames().Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ?? false;
        }
        catch { return false; }
    }

    /// <summary>True if the key path exists in the given hive/view.</summary>
    public static bool KeyExists(RegistryHive hive, RegistryView view, string path)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
            return key is not null;
        }
        catch { return false; }
    }

    public static int? ReadDword(RegistryHive hive, RegistryView view, string path, string name)
        => ReadValue(hive, view, path, name) is int v ? v : null;

    public static string? ReadString(RegistryHive hive, RegistryView view, string path, string name)
        => ReadValue(hive, view, path, name) as string;

    public static void WriteDword(RegistryHive hive, string path, string name, int value)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).CreateSubKey(path, writable: true);
        key!.SetValue(name, value, RegistryValueKind.DWord);
    }

    public static void WriteString(RegistryHive hive, string path, string name, string value)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).CreateSubKey(path, writable: true);
        key!.SetValue(name, value, RegistryValueKind.String);
    }

    /// <summary>Writes a string value with an explicit RegistryValueKind (e.g. REG_EXPAND_SZ), so restored values keep their original type.</summary>
    public static void WriteStringAsKind(RegistryHive hive, string path, string name, string value, RegistryValueKind kind)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).CreateSubKey(path, writable: true);
        key!.SetValue(name, value, kind);
    }

    public static void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    public static void WriteBinary(RegistryHive hive, string path, string name, byte[] value)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).CreateSubKey(path, writable: true);
        key!.SetValue(name, value, RegistryValueKind.Binary);
    }

    public static byte[]? ReadBinary(RegistryHive hive, RegistryView view, string path, string name)
        => ReadValue(hive, view, path, name) is byte[] v ? v : null;

    /// <summary>Returns the RegistryValueKind of a named value, or null if the value or key does not exist.</summary>
    public static RegistryValueKind? ReadKind(RegistryHive hive, RegistryView view, string path, string name)
    {
        try
        {
            using var key = RegistryKey.OpenBaseKey(hive, view).OpenSubKey(path);
            if (key is null) return null;
            var names = key.GetValueNames();
            if (!names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return null;
            return key.GetValueKind(name);
        }
        catch { return null; }
    }
}
