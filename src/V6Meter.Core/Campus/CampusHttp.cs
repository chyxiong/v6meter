using System.Net;
using System.Net.Sockets;

namespace V6Meter.Core.Campus;

/// <summary>HTTP client for the campus gateways and the connectivity probe.</summary>
public static class CampusHttp
{
    /// <summary>
    /// No proxy (the gateways are only reachable directly), no automatic redirects (a captive-portal redirect is
    /// information, not something to follow), and IPv4 only: campus IPv6 works without logging in, so a probe
    /// that went over IPv6 would report "online" while billed IPv4 is still locked.
    /// </summary>
    public static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = async (ctx, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, ctx.DnsEndPoint.Port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        return new HttpClient(handler) { Timeout = timeout };
    }
}
