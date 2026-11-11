using System.Management;

namespace SwiftwaveTweaks.Services;

public enum PnpDeviceClass { Other, Display, Net, StorageController, System }

/// <summary>A PCI device as discovered through Win32_PnPEntity (no writes).</summary>
public sealed record PnpDeviceInfo(
    string PnpId,
    string Name,
    string ClassGuid,
    string Service,
    int ConfigError,
    string Status,
    PnpDeviceClass Class)
{
    public bool Healthy => ConfigError == 0 &&
                           !string.Equals(Status, "Error", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(Status, "Unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>HKLM\SYSTEM\CurrentControlSet\Enum\PCI\VEN_x&amp;DEV_y\instance</summary>
    public string EnumRoot => @"SYSTEM\CurrentControlSet\Enum\" + PnpId.Replace('/', '\\');

    public string DeviceParameters => EnumRoot + @"\Device Parameters";

    public string VendorDevice => PnpId.Split('\\').ElementAtOrDefault(1) ?? "";
}

public static class PnpDeviceService
{
    public const string ClassDisplay = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    public const string ClassNet = "{4d36e972-e325-11ce-bfc1-08002be10318}";
    public const string ClassScsiAdapter = "{4d36e97b-e325-11ce-bfc1-08002be10318}";
    public const string ClassHdc = "{4d36e967-e325-11ce-bfc1-08002be10318}";
    public const string ClassSystem = "{4d36e97d-e325-11ce-bfc1-08002be10318}";

    // Intel Serial IO functions where MSI/affinity rewrites are known to break input.
    private static readonly HashSet<string> HardDeny = new(StringComparer.OrdinalIgnoreCase)
    {
        "VEN_8086&DEV_51ED", "VEN_8086&DEV_51F1", "VEN_8086&DEV_51CA"
    };

    private static readonly object CacheGate = new();
    private static List<PnpDeviceInfo>? _cache;
    private static HashSet<string>? _bootDisplayIds;

    public static List<PnpDeviceInfo> QueryAll(bool refresh = false)
    {
        lock (CacheGate)
        {
            if (_cache is not null && !refresh) return _cache;
            var list = new List<PnpDeviceInfo>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID,Name,ClassGuid,Service,ConfigManagerErrorCode,Status FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'PCI\\\\%'");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string pnp = mo["PNPDeviceID"] as string ?? "";
                    if (pnp.Length == 0) continue;
                    string cls = (mo["ClassGuid"] as string ?? "").ToLowerInvariant();
                    int cfg = mo["ConfigManagerErrorCode"] is uint u ? (int)u : 0;
                    list.Add(new PnpDeviceInfo(
                        pnp,
                        mo["Name"] as string ?? "",
                        cls,
                        mo["Service"] as string ?? "",
                        cfg,
                        mo["Status"] as string ?? "",
                        MapClass(cls)));
                }
            }
            catch (Exception ex) { SafeLog.Write("PCI enumeration failed", ex); }
            _cache = list.OrderBy(d => d.Class switch
            {
                PnpDeviceClass.Display => 0,
                PnpDeviceClass.StorageController => 1,
                PnpDeviceClass.Net => 2,
                _ => 3
            }).ToList();
            return _cache;
        }
    }

    /// <summary>Re-reads one device's health by PNP id (post-write assertion).</summary>
    public static PnpDeviceInfo? QueryOne(string pnpId)
    {
        try
        {
            string escaped = pnpId.Replace("\\", "\\\\");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PNPDeviceID,Name,ClassGuid,Service,ConfigManagerErrorCode,Status FROM Win32_PnPEntity WHERE PNPDeviceID = '{escaped}'");
            foreach (ManagementObject mo in searcher.Get())
            {
                string cls = (mo["ClassGuid"] as string ?? "").ToLowerInvariant();
                return new PnpDeviceInfo(
                    mo["PNPDeviceID"] as string ?? pnpId,
                    mo["Name"] as string ?? "",
                    cls,
                    mo["Service"] as string ?? "",
                    mo["ConfigManagerErrorCode"] is uint u ? (int)u : 0,
                    mo["Status"] as string ?? "",
                    MapClass(cls));
            }
        }
        catch (Exception ex) { SafeLog.Write("PnP re-query failed for " + pnpId, ex); }
        return null;
    }

    /// <summary>PNP ids of display adapters currently driving a monitor (boot/primary display).</summary>
    public static HashSet<string> BootDisplayIds()
    {
        lock (CacheGate)
        {
            if (_bootDisplayIds is not null) return _bootDisplayIds;
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID,CurrentHorizontalResolution FROM Win32_VideoController");
                foreach (ManagementObject mo in searcher.Get())
                {
                    var res = mo["CurrentHorizontalResolution"];
                    if (res is not uint w || w == 0) continue;
                    var id = mo["PNPDeviceID"] as string;
                    if (!string.IsNullOrEmpty(id)) ids.Add(id);
                }
            }
            catch (Exception ex) { SafeLog.Write("Boot display query failed", ex); }
            _bootDisplayIds = ids;
            return ids;
        }
    }

    public static bool IsHardDenied(string vendorDevice) =>
        HardDeny.Contains(vendorDevice);

    public static bool IsVirtualMachine()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer,Model FROM Win32_ComputerSystem");
            foreach (ManagementObject mo in searcher.Get())
            {
                string s = ((mo["Manufacturer"] as string ?? "") + " " + (mo["Model"] as string ?? "")).ToLowerInvariant();
                return s.Contains("vmware") || s.Contains("virtualbox") || s.Contains("kvm") ||
                       s.Contains("qemu") || s.Contains("hyper-v") || s.Contains("virtual machine") ||
                       s.Contains("xen");
            }
        }
        catch { }
        return false;
    }

    public static bool IsServer()
    {
        var pt = RegHelper.ReadString(Microsoft.Win32.RegistryHive.LocalMachine,
            Microsoft.Win32.RegistryView.Registry64,
            @"SYSTEM\CurrentControlSet\Control\ProductOptions", "ProductType");
        return pt is not null && !string.Equals(pt, "WinNT", StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasBattery(out bool known)
    {
        known = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT DeviceID FROM Win32_Battery");
            bool any = false;
            foreach (ManagementObject _ in searcher.Get()) { any = true; break; }
            known = true;
            return any;
        }
        catch { return false; }
    }

    /// <summary>SMBIOS chassis type codes (Win32_SystemEnclosure.ChassisTypes).</summary>
    public static int[] GetChassisTypes(out bool known)
    {
        known = false;
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ChassisTypes FROM Win32_SystemEnclosure");
            foreach (ManagementObject mo in searcher.Get())
            {
                known = true;
                if (mo["ChassisTypes"] is ushort[] arr) return arr.Select(x => (int)x).ToArray();
                if (mo["ChassisTypes"] is int[] iarr) return iarr;
                return Array.Empty<int>();
            }
            known = true;
            return Array.Empty<int>();
        }
        catch { return Array.Empty<int>(); }
    }

    private static PnpDeviceClass MapClass(string guid) => guid switch
    {
        ClassDisplay => PnpDeviceClass.Display,
        ClassNet => PnpDeviceClass.Net,
        ClassScsiAdapter or ClassHdc => PnpDeviceClass.StorageController,
        ClassSystem => PnpDeviceClass.System,
        _ => PnpDeviceClass.Other
    };
}
