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
            return key?.GetValue(name);
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

    public static void DeleteValue(RegistryHive hive, string path, string name)
    {
        using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Default).OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
