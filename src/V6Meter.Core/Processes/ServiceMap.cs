using System.Runtime.InteropServices;

namespace V6Meter.Core.Processes;

/// <summary>PID → running Win32 services hosted in that process (EnumServicesStatusEx).</summary>
public static class ServiceMap
{
    public static Dictionary<uint, List<ServiceEntry>> Query()
    {
        var result = new Dictionary<uint, List<ServiceEntry>>();
        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero)
            return result;
        try
        {
            uint resume = 0;
            EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, IntPtr.Zero, 0, out uint needed, out _, ref resume, null);
            if (needed == 0)
                return result;
            IntPtr buf = Marshal.AllocHGlobal((int)needed);
            try
            {
                resume = 0;
                if (!EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE, buf, needed, out _, out uint count, ref resume, null))
                    return result;
                int stride = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (int i = 0; i < count; i++)
                {
                    var s = Marshal.PtrToStructure<EnumServiceStatusProcess>(buf + i * stride);
                    if (s.ProcessId == 0)
                        continue;
                    var entry = new ServiceEntry(Marshal.PtrToStringUni(s.ServiceName) ?? "?", Marshal.PtrToStringUni(s.DisplayName) ?? "?");
                    if (!result.TryGetValue(s.ProcessId, out var list))
                        result[s.ProcessId] = list = [];
                    list.Add(entry);
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseServiceHandle(scm); }
        return result;
    }

    private const uint SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint SERVICE_WIN32 = 0x30;
    private const uint SERVICE_ACTIVE = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumServicesStatusEx(IntPtr scm, int infoLevel, uint serviceType, uint serviceState,
        IntPtr services, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
