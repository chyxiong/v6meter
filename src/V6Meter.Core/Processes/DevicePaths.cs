using System.Runtime.InteropServices;
using System.Text;

namespace V6Meter.Core.Processes;

/// <summary>Converts NT device paths (\Device\HarddiskVolume3\...) to DOS paths (C:\...).</summary>
public sealed class DevicePaths
{
    private volatile IReadOnlyDictionary<string, string> _deviceToDrive;

    public DevicePaths(IReadOnlyDictionary<string, string>? deviceToDrive = null) =>
        _deviceToDrive = deviceToDrive ?? QueryDrives();

    public string ToDosPath(string ntPath)
    {
        var r = TryConvert(ntPath, _deviceToDrive);
        if (r != null)
            return r;
        // A drive may have been mounted since the last query.
        _deviceToDrive = QueryDrives();
        return TryConvert(ntPath, _deviceToDrive) ?? ntPath;
    }

    public static string? TryConvert(string ntPath, IReadOnlyDictionary<string, string> deviceToDrive)
    {
        if (!ntPath.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            return ntPath; // already a DOS path (or something we cannot map)
        foreach (var (device, drive) in deviceToDrive)
            if (ntPath.Length > device.Length && ntPath[device.Length] == '\\' &&
                ntPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
                return drive + ntPath[device.Length..];
        return null;
    }

    private static Dictionary<string, string> QueryDrives()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buf = new StringBuilder(1024);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string drive = c + ":";
            if (QueryDosDevice(drive, buf, buf.Capacity) != 0)
                map[buf.ToString()] = drive;
        }
        return map;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}
