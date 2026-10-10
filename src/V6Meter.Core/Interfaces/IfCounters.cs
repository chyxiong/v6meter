using System.Runtime.InteropServices;

namespace V6Meter.Core.Interfaces;

/// <summary>Reads per-interface octet counters via iphlpapi GetIfEntry2.</summary>
public static class IfCounters
{
    public static bool TryRead(int interfaceIndex, out ulong inOctets, out ulong outOctets)
    {
        var row = new MibIfRow2 { InterfaceIndex = (uint)interfaceIndex };
        if (interfaceIndex >= 0 && GetIfEntry2(ref row) == 0)
        {
            inOctets = row.InOctets;
            outOctets = row.OutOctets;
            return true;
        }
        inOctets = outOctets = 0;
        return false;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIfEntry2(ref MibIfRow2 row);

    /// <summary>MIB_IF_ROW2 (netioapi.h); 1352 bytes on x64.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu;
        public uint Type;
        public int TunnelType;
        public int MediaType;
        public int PhysicalMediumType;
        public int AccessType;
        public int DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public int OperStatus;
        public int AdminStatus;
        public int MediaConnectState;
        public Guid NetworkGuid;
        public int ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;
        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;
        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }
}
