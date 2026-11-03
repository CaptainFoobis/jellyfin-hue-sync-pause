using System.Net;
using System.Text;

namespace Jellyfin.Plugin.HueSync.Hue;

internal sealed record DnsRecord(
    string Name,
    ushort Type,
    string? Domain,
    string? Address,
    int? Port,
    IReadOnlyDictionary<string, string> Text);

/// <summary>
/// Reads and writes the small slice of DNS used for Hue mDNS discovery.
/// </summary>
internal static class DnsMessage
{
    private static readonly IReadOnlyDictionary<string, string> EmptyText = new Dictionary<string, string>();

    public static byte[] CreateQuery(string serviceName)
    {
        var packet = new MemoryStream();
        WriteU16(packet, 0);
        WriteU16(packet, 0);
        WriteU16(packet, 1);
        WriteU16(packet, 0);
        WriteU16(packet, 0);
        WriteU16(packet, 0);
        WriteName(packet, serviceName);
        WriteU16(packet, 12);
        // QU bit asks the device to answer this socket instead of multicast port 5353.
        WriteU16(packet, 0x8001);
        return packet.ToArray();
    }

    public static IReadOnlyList<DnsRecord> ReadRecords(ReadOnlySpan<byte> packet)
    {
        try
        {
            return Parse(packet);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return [];
        }
    }

    private static List<DnsRecord> Parse(ReadOnlySpan<byte> packet)
    {
        var reader = new Reader(packet);
        _ = reader.ReadU16();
        _ = reader.ReadU16();
        var questions = reader.ReadU16();
        var answers = reader.ReadU16();
        var authority = reader.ReadU16();
        var additional = reader.ReadU16();
        for (var i = 0; i < questions; i++)
        {
            _ = reader.ReadName();
            _ = reader.ReadU16();
            _ = reader.ReadU16();
        }

        var records = new List<DnsRecord>(answers + authority + additional);
        var count = answers + authority + additional;
        for (var i = 0; i < count; i++)
        {
            records.Add(ReadRecord(ref reader));
        }

        return records;
    }

    private static DnsRecord ReadRecord(ref Reader reader)
    {
        var name = reader.ReadName();
        var type = reader.ReadU16();
        _ = reader.ReadU16();
        _ = reader.ReadU32();
        var length = reader.ReadU16();
        var end = reader.Offset + length;
        string? domain = null;
        string? address = null;
        int? port = null;
        var text = EmptyText;
        switch (type)
        {
            case 1 when length == 4:
                address = new IPAddress(reader.ReadBytes(4)).ToString();
                break;
            case 12:
                domain = reader.ReadName();
                break;
            case 16:
                text = ReadText(reader.ReadBytes(length));
                break;
            case 28 when length == 16:
                address = new IPAddress(reader.ReadBytes(16)).ToString();
                break;
            case 33 when length >= 6:
                _ = reader.ReadU16();
                _ = reader.ReadU16();
                port = reader.ReadU16();
                domain = reader.ReadName();
                break;
            default:
                _ = reader.ReadBytes(length);
                break;
        }

        if (reader.Offset < end)
        {
            _ = reader.ReadBytes(end - reader.Offset);
        }
        else if (reader.Offset > end)
        {
            throw new InvalidOperationException("DNS record was longer than its length field.");
        }

        return new DnsRecord(name, type, domain, address, port, text);
    }

    private static Dictionary<string, string> ReadText(ReadOnlySpan<byte> data)
    {
        var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;
        while (offset < data.Length)
        {
            var length = data[offset];
            offset++;
            if (length == 0 || offset + length > data.Length)
            {
                break;
            }

            var entry = Encoding.UTF8.GetString(data.Slice(offset, length));
            offset += length;
            var split = entry.IndexOf('=');
            if (split > 0)
            {
                text[entry[..split]] = entry[(split + 1)..];
            }
            else if (!string.IsNullOrEmpty(entry))
            {
                text[entry] = string.Empty;
            }
        }

        return text;
    }

    private static void WriteU16(MemoryStream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteName(MemoryStream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        stream.WriteByte(0);
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _packet;
        private int _offset;

        public Reader(ReadOnlySpan<byte> packet)
        {
            _packet = packet;
            _offset = 0;
        }

        public int Offset => _offset;

        public ushort ReadU16()
        {
            if ((uint)_offset + 2 > (uint)_packet.Length)
            {
                throw new InvalidOperationException("DNS message ended early.");
            }

            var value = (ushort)((_packet[_offset] << 8) | _packet[_offset + 1]);
            _offset += 2;
            return value;
        }

        public uint ReadU32()
        {
            if ((uint)_offset + 4 > (uint)_packet.Length)
            {
                throw new InvalidOperationException("DNS message ended early.");
            }

            var value = ((uint)_packet[_offset] << 24)
                | ((uint)_packet[_offset + 1] << 16)
                | ((uint)_packet[_offset + 2] << 8)
                | _packet[_offset + 3];
            _offset += 4;
            return value;
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0 || (uint)_offset + (uint)count > (uint)_packet.Length)
            {
                throw new InvalidOperationException("DNS message ended early.");
            }

            var bytes = _packet.Slice(_offset, count).ToArray();
            _offset += count;
            return bytes;
        }

        public string ReadName()
        {
            var labels = new List<string>();
            var cursor = _offset;
            var jumped = false;
            var guard = 0;
            while (guard++ < 128)
            {
                if ((uint)cursor >= (uint)_packet.Length)
                {
                    throw new InvalidOperationException("DNS name ended early.");
                }

                var length = _packet[cursor];
                if (length == 0)
                {
                    cursor++;
                    if (!jumped)
                    {
                        _offset = cursor;
                    }

                    return string.Join('.', labels);
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if ((uint)cursor + 1 >= (uint)_packet.Length)
                    {
                        throw new InvalidOperationException("DNS name pointer ended early.");
                    }

                    var pointer = ((length & 0x3F) << 8) | _packet[cursor + 1];
                    if (pointer >= _packet.Length)
                    {
                        throw new InvalidOperationException("DNS name pointer is outside the message.");
                    }

                    if (!jumped)
                    {
                        _offset = cursor + 2;
                        jumped = true;
                    }

                    cursor = pointer;
                    continue;
                }

                if ((length & 0xC0) != 0)
                {
                    throw new InvalidOperationException("DNS name uses an unsupported label.");
                }

                cursor++;
                if ((uint)cursor + length > (uint)_packet.Length)
                {
                    throw new InvalidOperationException("DNS name ended early.");
                }

                labels.Add(Encoding.ASCII.GetString(_packet.Slice(cursor, length)));
                cursor += length;
                if (!jumped)
                {
                    _offset = cursor;
                }
            }

            throw new InvalidOperationException("DNS name is too long.");
        }
    }
}
