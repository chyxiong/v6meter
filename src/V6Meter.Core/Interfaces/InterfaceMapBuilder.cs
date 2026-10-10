using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace V6Meter.Core.Interfaces;

public static class InterfaceMapBuilder
{
    // Last successful CIM answer; used when a later query fails so known adapters keep their kind.
    private static Dictionary<string, bool> _lastConnector = new(StringComparer.OrdinalIgnoreCase);

    public static InterfaceMap Build()
    {
        var connector = QueryConnectorPresent();
        bool degraded = connector == null;
        if (connector != null)
            _lastConnector = connector;
        connector ??= _lastConnector;
        var adapters = new List<(InterfaceInfo, IEnumerable<IPAddress>)>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            int index = TryIndex(() => props.GetIPv4Properties()?.Index) ?? TryIndex(() => props.GetIPv6Properties()?.Index) ?? -1;
            var addresses = props.UnicastAddresses.Select(u => u.Address).ToList();
            // NDIS filter pseudo-adapters ("<name>-QoS Packet Scheduler-0000" etc.) have neither.
            if (index < 0 && addresses.Count == 0)
                continue;

            bool hasGw = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            bool? present = connector.TryGetValue(nic.Id, out var p) ? p
                : degraded ? InterfaceKindRules.GuessConnectorPresent(nic.NetworkInterfaceType, hasGw)
                : null;

            var (kind, hvExt) = InterfaceKindRules.Decide(new AdapterFacts(nic.Name, nic.Description, nic.NetworkInterfaceType, present, hasGw));
            var info = new InterfaceInfo(index, nic.Name, nic.Description, kind, hvExt);
            adapters.Add((info, addresses));
        }
        return new InterfaceMap(adapters, degraded);
    }

    /// <summary>InterfaceGuid ("{...}", upper-case insensitive) → ConnectorPresent, from root\StandardCimv2:MSFT_NetAdapter; null if the query failed.</summary>
    private static Dictionary<string, bool>? QueryConnectorPresent()
    {
        var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var scope = new ManagementScope(@"root\StandardCimv2");
            using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT InterfaceGuid, ConnectorPresent FROM MSFT_NetAdapter"));
            foreach (ManagementObject mo in searcher.Get())
                using (mo)
                    if (mo["InterfaceGuid"] is string guid && mo["ConnectorPresent"] is bool present)
                        result[guid] = present;
        }
        catch (Exception ex) when (ex is ManagementException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Trace.TraceError($"v6meter MSFT_NetAdapter query failed: {ex}");
            return null;
        }
        return result;
    }

    private static int? TryIndex(Func<int?> f)
    {
        try { return f(); }
        catch (NetworkInformationException) { return null; }
    }
}
