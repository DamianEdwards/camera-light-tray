using System.Text.Json.Serialization;

namespace CameraLightTray.Core;

public enum CameraStatus { Unknown, Inactive, Active }
public enum ManualMode { Automatic, Paused, ForceOn, ForceOff }
public sealed record AppChoice(string Key, string Name, bool Enabled = false);
public sealed record ProcessIdentity(int Pid, long StartTime, int SessionId, string UserSid, string AppKey, string Name);
public sealed record CameraApp(string Key, string Name, CameraStatus Status);
public sealed record MonitorSnapshot(int Version, long Sequence, long Epoch, DateTimeOffset Time,
    bool Healthy, string Detail, CameraApp[] Apps);
public sealed record LanAddress(string InterfaceId, string Name, string Address, string Mask);
public sealed record LightDevice(string Id, string Name, string Host, int Port, string InterfaceId,
    bool Enabled = false, bool Tls = false)
{
    public string? ProductName { get; init; }
}
public sealed record LightState(int On, int Brightness, int Temperature);
public sealed record Settings
{
    public bool AutomationEnabled { get; set; }
    public bool StartAtLogin { get; set; }
    public int ActivationMilliseconds { get; set; } = 500;
    public int LeaveMilliseconds { get; set; } = 2500;
    public string InterfaceId { get; set; } = "";
    public List<string> CameraIds { get; set; } = [];
    public List<AppChoice> Apps { get; set; } = [];
    public List<LightDevice> Lights { get; set; } = [];

    public void Validate()
    {
        if (Apps is null || Lights is null || CameraIds is null) throw new InvalidDataException("App, light and camera lists are required.");
        if (CameraIds.Count > 256 || CameraIds.Any(string.IsNullOrWhiteSpace) ||
            CameraIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != CameraIds.Count)
            throw new InvalidDataException("Camera selections must be unique nonempty device identities.");
        if (ActivationMilliseconds is < 100 or > 10000 || LeaveMilliseconds is < 500 or > 60000)
            throw new InvalidDataException("Activation must be 100-10000ms; leave grace must be 500-60000ms.");
        if (Apps.Count > 500 || Lights.Count > 100) throw new InvalidDataException("Too many configured items.");
        if (Apps.Any(a => a is null || string.IsNullOrWhiteSpace(a.Key) || string.IsNullOrWhiteSpace(a.Name)) ||
            Lights.Any(l => l is null || string.IsNullOrWhiteSpace(l.Name)))
            throw new InvalidDataException("App/light identities and names are required.");
        if (Apps.DistinctBy(a => a.Key, StringComparer.OrdinalIgnoreCase).Count() != Apps.Count ||
            Lights.DistinctBy(l => l.Id).Count() != Lights.Count)
            throw new InvalidDataException("Duplicate app or light identities.");
        foreach (var light in Lights) LightEndpoint.Validate(light);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(MonitorSnapshot))]
[JsonSerializable(typeof(LightResponse))]
[JsonSerializable(typeof(LightRequest))]
[JsonSerializable(typeof(LightDevice))]
[JsonSerializable(typeof(AccessoryInfo))]
[JsonSerializable(typeof(OwnershipJournal))]
public partial class AppJson : JsonSerializerContext;

public sealed record LightResponse(int NumberOfLights, LightState[] Lights);
public sealed record LightRequest(int NumberOfLights, LightState[] Lights);
public sealed record AccessoryInfo(string? DisplayName, string? ProductName, string? SerialNumber);
public sealed record OwnedLight(LightDevice Device, LightState Before, LightState Applied, bool AppliedConfirmed = true);
public sealed record OwnershipJournal(List<OwnedLight> Lights);
