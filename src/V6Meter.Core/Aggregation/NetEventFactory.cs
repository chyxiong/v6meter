using System.Net;
using System.Net.Sockets;
using V6Meter.Core.Etw;
using V6Meter.Core.Model;

namespace V6Meter.Core.Aggregation;

public static class NetEventFactory
{
    /// <summary>
    /// Builds a <see cref="NetEvent"/> from a flow. The local end is whichever address is a local unicast
    /// address; if neither or both are (e.g. the map has not caught up with a new IPv6 temporary address),
    /// fall back to saddr, which M0 verified to be the local end for both send and recv.
    /// </summary>
    public static NetEvent FromFlow(FlowKey key, long bytes, Func<IPAddress, bool> isLocal)
    {
        var s = KernelNetEvent.ToAddress(key.Saddr, key.IsV6);
        var d = KernelNetEvent.ToAddress(key.Daddr, key.IsV6);
        bool swap = isLocal(d) && !isLocal(s);
        var (local, remote) = swap ? (d, s) : (s, d);
        return new NetEvent(key.Pid, key.Direction, key.IsV6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, local, remote, bytes);
    }
}
