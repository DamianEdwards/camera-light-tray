using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CameraLightTray.Core;

public sealed record DnsRecord(string Name, ushort Type, string? Target, ushort Port, byte[] Data);

public static class MdnsPacket
{
    public static byte[] Query(string name, ushort type)
    {
        using var data = new MemoryStream();
        data.Write(new byte[12]);
        foreach (string part in name.TrimEnd('.').Split('.'))
        {
            byte[] label = Encoding.UTF8.GetBytes(part);
            if (label.Length is 0 or > 63) throw new InvalidDataException("Invalid DNS label.");
            data.WriteByte((byte)label.Length); data.Write(label);
        }
        data.WriteByte(0);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], 1); // IN, normal multicast response
        data.Write(tail);
        byte[] result = data.ToArray(); result[5] = 1;
        return result;
    }

    public static DnsRecord[] Read(byte[] data)
    {
        if (data.Length < 12) throw new InvalidDataException("Truncated DNS header.");
        int offset = 12;
        ushort Number(int index) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(index, 2));
        int questions = Number(4), records = Number(6) + Number(8) + Number(10);
        if (questions + records > 512) throw new InvalidDataException("Oversized DNS answer.");
        for (int i = 0; i < questions; i++) { Name(data, ref offset); Require(data, offset, 4); offset += 4; }
        var result = new List<DnsRecord>();
        for (int i = 0; i < records; i++)
        {
            string name = Name(data, ref offset);
            Require(data, offset, 10);
            ushort type = Number(offset), size = Number(offset + 8);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4, 4));
            offset += 10;
            Require(data, offset, size);
            string? target = null; ushort port = 0;
            int cursor = offset;
            if (type == 12) target = Name(data, ref cursor);
            if (type == 33)
            {
                if (size < 7) throw new InvalidDataException("Truncated SRV record.");
                port = Number(offset + 4); cursor += 6; target = Name(data, ref cursor);
            }
            if (cursor > offset + size) throw new InvalidDataException("DNS name exceeds record.");
            if (ttl > 0) result.Add(new(name, type, target, port, data.AsSpan(offset, size).ToArray()));
            offset += size;
        }
        return result.ToArray();
    }
    private static void Require(byte[] data, int offset, int size)
    {
        if (offset < 0 || size < 0 || offset > data.Length - size) throw new InvalidDataException("Truncated DNS record.");
    }
    private static string Name(byte[] data, ref int offset)
    {
        int cursor = offset, jumps = 0, length = 0; bool jumped = false;
        var labels = new List<string>();
        while (true)
        {
            Require(data, cursor, 1);
            byte size = data[cursor++];
            if (size == 0) { if (!jumped) offset = cursor; break; }
            if ((size & 0xC0) == 0xC0)
            {
                Require(data, cursor, 1);
                if (!jumped) offset = cursor + 1;
                cursor = ((size & 0x3F) << 8) | data[cursor];
                jumped = true;
                if (++jumps > 20) throw new InvalidDataException("Cyclic DNS pointer.");
                continue;
            }
            if (size > 63 || (length += size + 1) > 255) throw new InvalidDataException("Invalid DNS name.");
            Require(data, cursor, size);
            labels.Add(Encoding.UTF8.GetString(data, cursor, size)); cursor += size;
        }
        return string.Join('.', labels).ToLowerInvariant();
    }

    public static bool UsesTls(byte[] txt)
    {
        for (int offset = 0; offset < txt.Length;)
        {
            int size = txt[offset++]; Require(txt, offset, size);
            string item = Encoding.UTF8.GetString(txt, offset, size); offset += size;
            if (item.Equals("tls", StringComparison.OrdinalIgnoreCase) ||
                item.StartsWith("tls=", StringComparison.OrdinalIgnoreCase) && item[4..] is not ("0" or "false"))
                return true;
        }
        return false;
    }
}

public static class MdnsDiscovery
{
    private const string Service = "_elg._tcp.local";
    public static async Task<LightDevice[]> DiscoverAsync(LanAddress lan, CancellationToken cancellationToken)
    {
        var local = IPAddress.Parse(lan.Address);
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.ExclusiveAddressUse = false;
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        socket.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"), local);
        var destination = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
        await socket.SendAsync(MdnsPacket.Query(Service, 12), destination, cancellationToken);
        var records = new List<DnsRecord>();
        var queried = new HashSet<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (records.Count < 1024)
            {
                var packet = await socket.ReceiveAsync(timeout.Token);
                if (!LanInterfaces.SameSubnet(local, packet.RemoteEndPoint.Address, IPAddress.Parse(lan.Mask))) continue;
                if (packet.RemoteEndPoint.Port != 5353 || packet.Buffer.Length < 12 ||
                    (packet.Buffer[2] & 0x80) == 0) continue;
                records.AddRange(MdnsPacket.Read(packet.Buffer));
                foreach (var target in records.Where(r => r.Type is 12 or 33 && r.Target is not null)
                    .Select(r => (r.Target!, Type: (ushort)(r.Type == 12 ? 255 : 1))).ToArray())
                    if (queried.Add(target.Item1))
                        await socket.SendAsync(MdnsPacket.Query(target.Item1, target.Type), destination, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return ResolveRecords(records, lan.InterfaceId);
    }

    public static LightDevice[] ResolveRecords(IEnumerable<DnsRecord> answers, string interfaceId)
    {
        var records = answers.ToArray();
        var devices = new List<LightDevice>();
        foreach (var ptr in records.Where(r => r.Type == 12 && r.Name == Service && r.Target is not null))
        {
            var srv = records.LastOrDefault(r => r.Type == 33 && r.Name == ptr.Target);
            var a = records.LastOrDefault(r => r.Type == 1 && r.Name == srv?.Target && r.Data.Length == 4);
            if (srv is null || a is null) continue;
            bool tls = records.Where(r => r.Type == 16 && r.Name == ptr.Target).Any(r => MdnsPacket.UsesTls(r.Data));
            var device = new LightDevice(ptr.Target!, ptr.Target!.Replace("." + Service, "", StringComparison.Ordinal),
                new IPAddress(a.Data).ToString(), srv.Port, interfaceId, Tls: tls);
            LightEndpoint.Validate(device);
            devices.Add(device);
        }
        return devices.DistinctBy(d => d.Id).ToArray();
    }
}
