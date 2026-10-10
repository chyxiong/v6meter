using System.Net;
using System.Net.Sockets;
using V6Meter.Core.Etw;

namespace V6Meter.Core.Model;

/// <summary>Normalized traffic record; classification and aggregation only see this type.</summary>
public readonly record struct NetEvent(
    uint Pid,
    NetDirection Direction,
    AddressFamily Family,
    IPAddress LocalAddr,
    IPAddress RemoteAddr,
    long Bytes);

public enum TrafficCategory
{
    Loopback,
    V4Billed,
    V4Free,
    V6,
    V6Nat64,
    Virtual,
    Local,
}

public readonly record struct TrafficClass(TrafficCategory Category, bool Tunneled = false);
