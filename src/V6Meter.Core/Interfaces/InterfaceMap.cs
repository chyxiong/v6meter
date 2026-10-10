using System.Net;
using V6Meter.Core.Classification;

namespace V6Meter.Core.Interfaces;

/// <summary>Immutable snapshot: local unicast address → interface. Rebuilt on address changes.</summary>
public sealed class InterfaceMap
{
    private readonly Dictionary<IPAddress, InterfaceInfo> _byAddress;

    /// <param name="degraded">The adapter query failed and some kinds were guessed; callers should rebuild soon.</param>
    public InterfaceMap(IEnumerable<(InterfaceInfo Info, IEnumerable<IPAddress> Addresses)> adapters, bool degraded = false)
    {
        Degraded = degraded;
        _byAddress = new();
        var list = new List<InterfaceInfo>();
        foreach (var (info, addrs) in adapters)
        {
            list.Add(info);
            foreach (var a in addrs)
                _byAddress.TryAdd(IpRanges.Normalize(a), info);
        }
        Interfaces = list;
        // Physical adapters with no address are bound to a virtual switch; their traffic is
        // already counted on the host vNIC, so they are excluded from reconciliation.
        AddressedPhysical = list.Where(i => i.Kind == InterfaceKind.Physical && _byAddress.ContainsValue(i)).ToList();
    }

    public static InterfaceMap Empty { get; } = new([]);

    public IReadOnlyList<InterfaceInfo> Interfaces { get; }
    public IReadOnlyList<InterfaceInfo> AddressedPhysical { get; }
    public bool Degraded { get; }
    public bool HasHyperVExternal => Interfaces.Any(i => i.IsHyperVExternal);

    public InterfaceInfo? Find(IPAddress localAddr) =>
        _byAddress.TryGetValue(IpRanges.Normalize(localAddr), out var i) ? i : null;

    public bool IsLocal(IPAddress addr) => _byAddress.ContainsKey(IpRanges.Normalize(addr));
}
