using System.Net;
using System.Text;
using Jellyfin.Plugin.HueSync.Configuration;
using Jellyfin.Plugin.HueSync.Hue;

namespace Jellyfin.Plugin.HueSync.Tests;

public class HueParsingTests
{
    [Fact]
    public void Execution_json_reads_the_sync_fields()
    {
        var execution = SyncExecution.Parse("""
            {
              "syncActive": true,
              "mode": "video",
              "lastSyncMode": "game",
              "hueTarget": "area-1"
            }
            """);

        Assert.True(execution.SyncActive);
        Assert.Equal("video", execution.Mode);
        Assert.Equal("game", execution.LastSyncMode);
        Assert.Equal("area-1", execution.HueTarget);
        Assert.True(SyncExecution.IsSyncMode("music"));
        Assert.False(SyncExecution.IsSyncMode("passthrough"));
        Assert.False(SyncExecution.IsSyncMode("powersave"));
        Assert.False(SyncExecution.IsSyncMode(" "));
    }

    [Fact]
    public void Nested_execution_json_is_accepted()
    {
        var execution = SyncExecution.Parse("""{"execution":{"syncActive":false,"mode":"passthrough","hueTarget":"/groups/2"}}""");

        Assert.False(execution.SyncActive);
        Assert.Equal("passthrough", execution.Mode);
        Assert.Equal("/groups/2", execution.HueTarget);
    }

    [Fact]
    public void Entertainment_members_resolve_to_light_ids()
    {
        const string area = """
            {
              "data": [
                {
                  "id": "area-1",
                  "metadata": { "name": "Living room" },
                  "channels": [
                    { "members": [ { "service": { "rid": "ent-1", "rtype": "entertainment" } } ] },
                    { "members": [ { "service": { "rid": "light-direct", "rtype": "light" } } ] }
                  ]
                },
                { "id": "area-2", "channels": [] }
              ]
            }
            """;
        const string entertainment = """
            { "data": [ { "id": "ent-1", "owner": { "rid": "device-1", "rtype": "device" } } ] }
            """;
        const string lights = """
            {
              "data": [
                { "id": "light-1", "owner": { "rid": "device-1", "rtype": "device" } },
                { "id": "light-direct", "owner": { "rid": "device-2", "rtype": "device" } }
              ]
            }
            """;

        var resolved = EntertainmentLights.Resolve(area, entertainment, lights, "area-1");

        Assert.Equal(["light-1", "light-direct"], resolved);
        Assert.Empty(EntertainmentLights.Resolve(area, entertainment, lights, "missing"));
        Assert.Equal("8", EntertainmentLights.NormalizeAreaId("/groups/8"));
        Assert.True(EntertainmentLights.IsGroupId("8"));
        Assert.False(EntertainmentLights.IsGroupId("7c4e1a2b-9f63-4d18-b5a0-2e8c6f91d4ab"));
    }

    [Fact]
    public void A_single_area_document_is_used_when_the_id_is_absent()
    {
        const string area = """
            { "data": [ { "channels": [ { "members": [ { "service": { "rid": "light-1", "rtype": "light" } } ] } ] } ] }
            """;

        var resolved = EntertainmentLights.Resolve(area, """{"data":[]}""", """{"data":[]}""", "area-1");

        Assert.Equal(["light-1"], resolved);
    }

    [Fact]
    public void Host_addresses_keep_the_ip_and_reject_a_path()
    {
        Assert.True(HostAddress.TryNormalize(" https://192.0.2.8/ ", out var host, out _));
        Assert.Equal("192.0.2.8", host);
        Assert.False(HostAddress.TryNormalize("192.0.2.8/api", out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.False(HostAddress.TryNormalize("user@192.0.2.8", out _, out _));
        Assert.Equal("[2001:db8::1]", HostAddress.ForUrl("2001:db8::1"));
        Assert.Equal("192.0.2.8:8443", HostAddress.ForUrl("192.0.2.8:8443"));
    }

    [Fact]
    public void Uncompressed_mdns_packet_reads_the_sync_device()
    {
        var packet = DnsPacket.Create(
            DnsPacket.Pointer("_huesync._tcp.local", "LivingRoom._huesync._tcp.local"),
            DnsPacket.Service("LivingRoom._huesync._tcp.local", 443, "huesync.local"),
            DnsPacket.Address("huesync.local", IPAddress.Parse("192.0.2.20")),
            DnsPacket.Text("LivingRoom._huesync._tcp.local", "name=Living Room"));

        var devices = DeviceCatalog.FromRecords(DnsMessage.ReadRecords(packet));

        var device = Assert.Single(devices);
        Assert.Equal("sync", device.Kind);
        Assert.Equal("192.0.2.20", device.Host);
        Assert.Equal("Living Room", device.Name);
    }

    [Fact]
    public void Compressed_mdns_name_still_resolves_the_address()
    {
        var packet = DnsPacket.CreateCompressedSyncReply();
        var devices = DeviceCatalog.FromRecords(DnsMessage.ReadRecords(packet));

        var device = Assert.Single(devices);
        Assert.Equal("sync", device.Kind);
        Assert.Equal("192.0.2.20", device.Host);
        Assert.Equal("Box", device.Name);
    }

    [Fact]
    public void A_truncated_packet_is_ignored()
    {
        Assert.Empty(DnsMessage.ReadRecords([0, 1, 2]));
    }

    [Fact]
    public void Discovery_portal_json_reads_bridge_addresses()
    {
        var devices = DeviceCatalog.ParsePortal("""
            [
              {"id":"001788fffe123456","internalipaddress":"192.0.2.2","port":443},
              {"internalipaddress":"192.0.2.3","port":8443}
            ]
            """);

        Assert.Equal(2, devices.Count);
        Assert.Equal("bridge", devices[0].Kind);
        Assert.Equal("192.0.2.2", devices[0].Host);
        Assert.Equal("Hue Bridge 001788fffe123456", devices[0].Name);
        Assert.Equal("192.0.2.3:8443", devices[1].Host);
        Assert.Empty(DeviceCatalog.ParsePortal("not-json"));
    }
}

internal static class DnsPacket
{
    public static byte[] Create(params byte[][] records)
    {
        var packet = new MemoryStream();
        WriteU16(packet, 0);
        WriteU16(packet, 0x8400);
        WriteU16(packet, 0);
        WriteU16(packet, records.Length);
        WriteU16(packet, 0);
        WriteU16(packet, 0);
        foreach (var record in records)
        {
            packet.Write(record);
        }

        return packet.ToArray();
    }

    public static byte[] Pointer(string name, string target)
    {
        return Record(name, 12, Name(target));
    }

    public static byte[] Service(string name, int port, string target)
    {
        var data = new MemoryStream();
        WriteU16(data, 0);
        WriteU16(data, 0);
        WriteU16(data, port);
        WriteName(data, target);
        return Record(name, 33, data.ToArray());
    }

    public static byte[] Address(string name, IPAddress address)
    {
        return Record(name, 1, address.GetAddressBytes());
    }

    public static byte[] Text(string name, string entry)
    {
        var bytes = Encoding.UTF8.GetBytes(entry);
        var data = new byte[bytes.Length + 1];
        data[0] = (byte)bytes.Length;
        bytes.CopyTo(data, 1);
        return Record(name, 16, data);
    }

    public static byte[] CreateCompressedSyncReply()
    {
        var packet = new MemoryStream();
        WriteU16(packet, 0);
        WriteU16(packet, 0x8400);
        WriteU16(packet, 0);
        WriteU16(packet, 3);
        WriteU16(packet, 0);
        WriteU16(packet, 0);
        WriteName(packet, "_huesync._tcp.local");
        WriteU16(packet, 12);
        WriteU16(packet, 1);
        WriteU32(packet, 10);
        var instanceOffset = (int)packet.Length + 2;
        var instance = Name("Box._huesync._tcp.local");
        WriteU16(packet, instance.Length);
        packet.Write(instance);
        WritePointer(packet, instanceOffset);
        WriteU16(packet, 33);
        WriteU16(packet, 1);
        WriteU32(packet, 10);
        var target = new MemoryStream();
        WriteU16(target, 0);
        WriteU16(target, 0);
        WriteU16(target, 443);
        WriteName(target, "huesync.local");
        var targetBytes = target.ToArray();
        WriteU16(packet, targetBytes.Length);
        packet.Write(targetBytes);
        WriteName(packet, "huesync.local");
        WriteU16(packet, 1);
        WriteU16(packet, 1);
        WriteU32(packet, 10);
        WriteU16(packet, 4);
        packet.Write(IPAddress.Parse("192.0.2.20").GetAddressBytes());
        return packet.ToArray();
    }

    private static byte[] Record(string name, int type, byte[] data)
    {
        var record = new MemoryStream();
        WriteName(record, name);
        WriteU16(record, type);
        WriteU16(record, 1);
        WriteU32(record, 120);
        WriteU16(record, data.Length);
        record.Write(data);
        return record.ToArray();
    }

    private static byte[] Name(string name)
    {
        var stream = new MemoryStream();
        WriteName(stream, name);
        return stream.ToArray();
    }

    private static void WritePointer(MemoryStream stream, int offset)
    {
        stream.WriteByte((byte)(0xC0 | (offset >> 8)));
        stream.WriteByte((byte)offset);
    }

    private static void WriteName(MemoryStream stream, string name)
    {
        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
    }

    private static void WriteU16(MemoryStream stream, int value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteU32(MemoryStream stream, int value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}

public class HueSyncRegistrationTests
{
    [Fact]
    public async Task A_successful_http_response_with_code_16_means_the_button_was_not_pressed()
    {
        using var client = Client("""{"code":16,"message":"Invalid State"}""");

        var error = await Assert.ThrowsAsync<HueApiException>(() => client.RegisterAsync("192.0.2.10", CancellationToken.None));

        Assert.True(error.IsButtonNotPressed);
        Assert.Equal(16, error.Code);
    }

    [Fact]
    public async Task Registration_sends_a_complete_json_body_with_a_length()
    {
        HttpRequestMessage? sent = null;
        string? body = null;
        var handler = new CapturingHandler("""{"code":16,"message":"Invalid State"}""", request => sent = request, text => body = text);
        using var client = new HueSyncClient(() => new PluginConfiguration(), new HttpClient(handler), ownsClient: true);

        await Assert.ThrowsAsync<HueApiException>(() => client.RegisterAsync("192.0.2.10", CancellationToken.None));

        Assert.NotNull(sent);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("/api/v1/registrations", sent.RequestUri!.AbsolutePath);
        Assert.Equal(new Version(1, 1), sent.Version);
        Assert.NotNull(body);
        Assert.Contains("\"appName\":\"Jellyfin Hue\"", body, StringComparison.Ordinal);
        Assert.Contains("\"instanceName\":\"Jellyfin\"", body, StringComparison.Ordinal);
        Assert.Contains("\"appSecret\":", body, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), sent.Content!.Headers.ContentLength);
        Assert.Equal("application/json", sent.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Registration_reads_the_token_and_device_name()
    {
        using var client = Client(
            """{"registrationId":"reg-1","accessToken":"token-1"}""",
            """{"name":"Living room"}""");

        var paired = await client.RegisterAsync("192.0.2.10", CancellationToken.None);

        Assert.Equal("token-1", paired.AccessToken);
        Assert.Equal("reg-1", paired.RegistrationId);
        Assert.Equal("Living room", paired.DeviceName);
    }

    private static HueSyncClient Client(params string[] bodies)
    {
        var pending = new Queue<string>(bodies);
        return new HueSyncClient(() => new PluginConfiguration(), new HttpClient(new StubHandler(pending)), ownsClient: true);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly Action<HttpRequestMessage> _onRequest;
        private readonly Action<string> _onBody;

        public CapturingHandler(string body, Action<HttpRequestMessage> onRequest, Action<string> onBody)
        {
            _body = body;
            _onRequest = onRequest;
            _onBody = onBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onRequest(request);
            _onBody(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;

        public StubHandler(Queue<string> bodies)
        {
            _bodies = bodies;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_bodies.Dequeue(), Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
