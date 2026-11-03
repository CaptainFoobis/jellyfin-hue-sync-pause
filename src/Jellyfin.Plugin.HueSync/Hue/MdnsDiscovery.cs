using System.Net;
using System.Net.Sockets;

namespace Jellyfin.Plugin.HueSync.Hue;

/// <summary>
/// Finds Hue Sync devices and Hue Bridges on the local network.
/// </summary>
internal static class MdnsDiscovery
{
    private static readonly IPEndPoint MdnsEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        var mdns = QueryMdnsAsync(cancellationToken);
        var portal = QueryPortalAsync(cancellationToken);
        await Task.WhenAll(mdns, portal).ConfigureAwait(false);
        return DeviceCatalog.Merge(mdns.Result.Concat(portal.Result));
    }

    private static async Task<IReadOnlyList<DiscoveredDevice>> QueryMdnsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await QuerySocketAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return [];
        }
    }

    private static async Task<IReadOnlyList<DiscoveredDevice>> QuerySocketAsync(CancellationToken cancellationToken)
    {
        using var listen = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        listen.CancelAfter(TimeSpan.FromMilliseconds(3500));
        using var socket = BindSocket();
        var packets = new List<byte[]>();
        var receive = ReceiveAsync(socket, packets, listen.Token, cancellationToken);
        try
        {
            var queries = new[]
            {
                DnsMessage.CreateQuery("_huesync._tcp.local"),
                DnsMessage.CreateQuery("_hue._tcp.local")
            };
            await SendAsync(socket, queries, listen.Token).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), listen.Token).ConfigureAwait(false);
            await SendAsync(socket, queries, listen.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The listen window ended before the queries finished.
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            listen.Cancel();
        }
        finally
        {
            try
            {
                await receive.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The listen window ended while a query was still being sent.
            }
        }
        var records = new List<DnsRecord>();
        foreach (var packet in packets)
        {
            records.AddRange(DnsMessage.ReadRecords(packet));
        }

        return DeviceCatalog.FromRecords(records);
    }

    private static async Task SendAsync(Socket socket, IEnumerable<byte[]> queries, CancellationToken cancellationToken)
    {
        foreach (var query in queries)
        {
            await socket.SendToAsync(query, SocketFlags.None, MdnsEndpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ReceiveAsync(Socket socket, List<byte[]> packets, CancellationToken listenToken, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        try
        {
            while (!listenToken.IsCancellationRequested)
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), listenToken).ConfigureAwait(false);
                if (result.ReceivedBytes <= 0)
                {
                    continue;
                }

                var copy = new byte[result.ReceivedBytes];
                buffer.AsSpan(0, result.ReceivedBytes).CopyTo(copy);
                packets.Add(copy);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The listen window ended.
        }
        catch (SocketException)
        {
            // The socket closed or the network rejected the multicast group.
        }
    }

    private static Socket BindSocket()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 5353));
        }
        catch (SocketException)
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsEndpoint.Address));
        }
        catch (SocketException)
        {
            // A query with the QU bit still receives a direct reply on this socket.
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        }
        catch (SocketException)
        {
            // The default TTL can still reach a device on the same link.
        }

        return socket;
    }

    private static async Task<IReadOnlyList<DiscoveredDevice>> QueryPortalAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        try
        {
            var json = await http.GetStringAsync(new Uri("https://discovery.meethue.com/"), timeout.Token).ConfigureAwait(false);
            return DeviceCatalog.ParsePortal(json);
        }
        catch (HttpRequestException)
        {
            return [];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [];
        }
    }
}
