using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Jellyfin.Plugin.HueSync.Hue;

internal sealed record DiscoveredDevice(string Kind, string Host, string Name);

/// <summary>
/// Turns mDNS answers and the Hue discovery portal into device addresses.
/// </summary>
internal static class DeviceCatalog
{
    public static IReadOnlyList<DiscoveredDevice> FromRecords(IEnumerable<DnsRecord> records)
    {
        var list = records as IReadOnlyList<DnsRecord> ?? records.ToArray();
        var devices = new List<DiscoveredDevice>();
        AddService(devices, list, "_huesync._tcp.local", "sync");
        AddService(devices, list, "_hue._tcp.local", "bridge");
        return Merge(devices);
    }

    public static IReadOnlyList<DiscoveredDevice> ParsePortal(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var devices = new List<DiscoveredDevice>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("internalipaddress", out var addressElement)
                    || addressElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var host = addressElement.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(host))
                {
                    continue;
                }

                if (item.TryGetProperty("port", out var portElement)
                    && portElement.TryGetInt32(out var port)
                    && port is > 0 and not 443)
                {
                    host += ":" + port.ToString(CultureInfo.InvariantCulture);
                }

                var name = "Hue Bridge";
                if (item.TryGetProperty("id", out var idElement)
                    && idElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(idElement.GetString()))
                {
                    name = "Hue Bridge " + idElement.GetString();
                }

                devices.Add(new DiscoveredDevice("bridge", host, name));
            }

            return devices;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<DiscoveredDevice> Merge(IEnumerable<DiscoveredDevice> devices)
    {
        var merged = new List<DiscoveredDevice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.Host))
            {
                continue;
            }

            if (seen.Add(device.Kind + "|" + device.Host.Trim()))
            {
                merged.Add(device with { Host = device.Host.Trim() });
            }
        }

        return merged;
    }

    private static void AddService(List<DiscoveredDevice> devices, IReadOnlyList<DnsRecord> records, string service, string kind)
    {
        foreach (var pointer in records)
        {
            if (pointer.Type != 12 || string.IsNullOrWhiteSpace(pointer.Domain) || !NameEquals(pointer.Name, service))
            {
                continue;
            }

            var instance = pointer.Domain;
            DnsRecord? serviceRecord = null;
            foreach (var record in records)
            {
                if (record.Type == 33 && NameEquals(record.Name, instance))
                {
                    serviceRecord = record;
                    break;
                }
            }

            var host = AddressFor(records, serviceRecord?.Domain) ?? HostFromName(serviceRecord?.Domain);
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            if (IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                host = "[" + host + "]";
            }

            if (serviceRecord?.Port is int port && port is > 0 and not 443)
            {
                host += ":" + port.ToString(CultureInfo.InvariantCulture);
            }

            var name = FirstLabel(instance);
            foreach (var record in records)
            {
                if (record.Type == 16
                    && NameEquals(record.Name, instance)
                    && record.Text.TryGetValue("name", out var textName)
                    && !string.IsNullOrWhiteSpace(textName))
                {
                    name = textName;
                    break;
                }
            }

            devices.Add(new DiscoveredDevice(kind, host, name));
        }
    }

    private static string? AddressFor(IReadOnlyList<DnsRecord> records, string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        foreach (var record in records)
        {
            if ((record.Type == 1 || record.Type == 28)
                && !string.IsNullOrWhiteSpace(record.Address)
                && NameEquals(record.Name, target))
            {
                return record.Address;
            }
        }

        return null;
    }

    private static string? HostFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var text = name.Trim().TrimEnd('.');
        if (text.Contains("_tcp", StringComparison.OrdinalIgnoreCase) || text.Contains("_udp", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return text;
    }

    private static string FirstLabel(string name)
    {
        var text = name.Trim().TrimEnd('.');
        var dot = text.IndexOf('.');
        return dot > 0 ? text[..dot] : text;
    }

    private static bool NameEquals(string left, string right)
    {
        return string.Equals(left.Trim().TrimEnd('.'), right.Trim().TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }
}
