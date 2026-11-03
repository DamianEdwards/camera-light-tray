using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CameraLightTray.Core;

public static class LanInterfaces
{
    public static LanAddress[] Get() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up &&
            n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
            .Select(a => new LanAddress(n.Id, n.Name, a.Address.ToString(), a.IPv4Mask.ToString())))
        .ToArray();

    public static LanAddress Resolve(string interfaceId, IPAddress destination)
    {
        var candidates = Get().Where(a => a.InterfaceId == interfaceId && SameSubnet(
            IPAddress.Parse(a.Address), destination, IPAddress.Parse(a.Mask))).ToArray();
        return candidates.FirstOrDefault() ?? throw new IOException(
            "Selected LAN interface is unavailable or the light is outside its subnet. Re-discover or choose an interface; VPN routes are never changed.");
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var x = a.GetAddressBytes(); var y = b.GetAddressBytes(); var m = mask.GetAddressBytes();
        return x.Length == 4 && y.Length == 4 && m.Length == 4 &&
            Enumerable.Range(0, 4).All(i => (x[i] & m[i]) == (y[i] & m[i]));
    }
}

public static class LightEndpoint
{
    public static void Validate(LightDevice device)
    {
        if (!IPAddress.TryParse(device.Host, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            IPAddress.IsLoopback(address) || address.GetAddressBytes()[0] is 0 or >= 224 ||
            address.Equals(IPAddress.Broadcast) || device.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(device.InterfaceId) || string.IsNullOrWhiteSpace(device.Id))
            throw new InvalidDataException("A light needs a unicast IPv4 address, port and explicit LAN interface.");
    }
}

public sealed class ElgatoTransport : ILightTransport
{
    private static HttpClient CreateClient(LightDevice device)
    {
        LightEndpoint.Validate(device);
        if (device.Tls) throw new NotSupportedException("TLS Elgato models are not supported by this spike.");
        var address = IPAddress.Parse(device.Host);
        var local = IPAddress.Parse(LanInterfaces.Resolve(device.InterfaceId, address).Address);
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    socket.Bind(new IPEndPoint(local, 0));
                    await socket.ConnectAsync(new IPEndPoint(address, device.Port), token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new(handler) { BaseAddress = new Uri($"http://{address}:{device.Port}"),
            Timeout = TimeSpan.FromSeconds(3), MaxResponseContentBufferSize = 65536 };
    }
    public async Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken)
    {
        using var client = CreateClient(device);
        var response = await client.GetFromJsonAsync("/elgato/lights", AppJson.Default.LightResponse, cancellationToken)
            ?? throw new InvalidDataException("Empty light response.");
        if (response.NumberOfLights != 1 || response.Lights is not { Length: 1 } ||
            response.Lights[0] is not { On: 0 or 1, Brightness: >= 0 and <= 100, Temperature: >= 100 and <= 500 })
            throw new InvalidDataException("Unsupported Elgato light-state response.");
        return response.Lights[0];
    }
    public async Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken)
    {
        using var client = CreateClient(device);
        using var response = await client.PutAsJsonAsync("/elgato/lights", new LightRequest(1, [state]),
            AppJson.Default.LightRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
    public async Task<string> IdentifyAsync(LightDevice device, CancellationToken cancellationToken)
    {
        using var client = CreateClient(device);
        var info = await client.GetFromJsonAsync("/elgato/accessory-info", AppJson.Default.AccessoryInfo, cancellationToken)
            ?? throw new InvalidDataException("Empty accessory response.");
        return info.DisplayName ?? info.ProductName ?? device.Name;
    }
}
