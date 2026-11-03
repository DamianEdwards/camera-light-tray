using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using CameraLightTray.App.Platform;
using CameraLightTray.Tests;
using CameraLightTray.Core;

if (args is ["--monitor-integration", var resultPath])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    try { await InstalledMonitorCheck.RunAsync(resultPath); }
    catch (Exception ex) { File.WriteAllText(resultPath, "FAIL: " + ex.Message); Environment.ExitCode = 1; }
    return;
}
int passed = 0;
void Test(string name, Action test)
{
    test(); passed++; Console.WriteLine("PASS " + name);
}
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}; got {actual}");
}
void Throws(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Expected invalid-data failure.");
}
var process = new ProcessIdentity(42, 200, 1, "test-user", "example.exe", "Example Meeting");
CameraTracker Tracker()
{
    var tracker = new CameraTracker(); tracker.Ready(100); tracker.Synchronize([process]); return tracker;
}
CameraStatus State(CameraTracker tracker) => tracker.Apps("test-user", 1).Single().Status;

Test("startup capture has no inferred inactive baseline", () =>
{
    var tracker = Tracker();
    var old = process with { StartTime = 50 };
    tracker.Synchronize([old]);
    Equal(CameraStatus.Unknown, State(tracker));
    tracker.Transition(old, 211, 1, 0); tracker.Transition(old, 213, 1, 0);
    Equal(CameraStatus.Unknown, State(tracker));
});
Test("new process baseline and successful preview/record aggregation", () =>
{
    var tracker = Tracker(); Equal(CameraStatus.Inactive, State(tracker));
    tracker.Transition(process, 207, 11, 0); tracker.Transition(process, 211, 11, 0);
    tracker.Transition(process, 211, 22, 0); Equal(CameraStatus.Active, State(tracker));
    tracker.Transition(process, 209, 11, 0); tracker.Transition(process, 213, 11, 0);
    Equal(CameraStatus.Active, State(tracker));
    tracker.Transition(process, 213, 22, 0); Equal(CameraStatus.Inactive, State(tracker));
});
Test("failed HRESULT and initialization stop never indicate capture", () =>
{
    var tracker = Tracker();
    tracker.Transition(process, 211, 1, 0x80004005); tracker.Transition(process, 7, 1, 0);
    Equal(CameraStatus.Inactive, State(tracker));
    tracker.Transition(process, 211, 1, 0); tracker.Transition(process, 213, 1, 0x80004005);
    Equal(CameraStatus.Active, State(tracker));
});
Test("unmatched stop and duplicate start are uncertain", () =>
{
    var tracker = Tracker();
    tracker.Transition(process, 213, 9, 0); Equal(CameraStatus.Unknown, State(tracker));
    tracker = Tracker(); tracker.Transition(process, 211, 1, 0); tracker.Transition(process, 211, 1, 0);
    tracker.Transition(process, 213, 1, 0); Equal(CameraStatus.Unknown, State(tracker));
});
Test("PID reuse, process death and user/session isolation", () =>
{
    var tracker = Tracker();
    tracker.Transition(process, 211, 1, 0);
    Equal(0, tracker.Apps("another-user", 1).Length);
    Equal(0, tracker.Apps("test-user", 2).Length);
    tracker.Synchronize([process with { StartTime = 300 }]);
    Equal(CameraStatus.Inactive, State(tracker));
    tracker.Synchronize([]); Equal(0, tracker.Apps("test-user", 1).Length);
});
Test("event loss discards known state and epoch", () =>
{
    var tracker = Tracker(); long epoch = tracker.Epoch;
    tracker.Transition(process, 211, 1, 0); tracker.Invalidate("lost events");
    tracker.Synchronize([process]);
    Equal(false, tracker.Healthy); Equal(epoch + 1, tracker.Epoch);
    Equal(CameraStatus.Unknown, State(tracker));
});

var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
Settings Preferences() => new() { AutomationEnabled = true, ActivationMilliseconds = 500, LeaveMilliseconds = 2500,
    Apps = [new("a", "App A", true), new("b", "App B", true)] };
MonitorSnapshot Snapshot(DateTimeOffset time, CameraStatus a, CameraStatus b = CameraStatus.Inactive, bool healthy = true, long epoch = 1) =>
    new(1, 1, epoch, time, healthy, "Synthetic", [new("a", "App A", a), new("b", "App B", b)]);
bool Decide(AutomationDecision decision, Settings settings, int milliseconds, CameraStatus a, CameraStatus b = CameraStatus.Inactive) =>
    decision.Update(settings, Snapshot(now.AddMilliseconds(milliseconds), a, b), ManualMode.Automatic, now.AddMilliseconds(milliseconds));

Test("118ms setup probes do not switch lights on", () =>
{
    var decision = new AutomationDecision(); var settings = Preferences();
    Equal(false, Decide(decision, settings, 0, CameraStatus.Active));
    Equal(false, Decide(decision, settings, 118, CameraStatus.Inactive));
    Equal(false, Decide(decision, settings, 500, CameraStatus.Active));
    Equal(false, Decide(decision, settings, 618, CameraStatus.Inactive));
});
Test("any-selected-app union and exact debounce/grace thresholds", () =>
{
    var decision = new AutomationDecision(); var settings = Preferences();
    Equal(false, Decide(decision, settings, 0, CameraStatus.Active));
    Equal(false, Decide(decision, settings, 499, CameraStatus.Active));
    Equal(true, Decide(decision, settings, 500, CameraStatus.Active));
    Equal(true, Decide(decision, settings, 800, CameraStatus.Inactive, CameraStatus.Active));
    Equal(true, Decide(decision, settings, 1000, CameraStatus.Inactive));
    Equal(true, Decide(decision, settings, 3499, CameraStatus.Inactive));
    Equal(false, Decide(decision, settings, 3500, CameraStatus.Inactive));
});
Test("unknown, disconnect, stale heartbeat and epoch prevent false off", () =>
{
    var decision = new AutomationDecision(); var settings = Preferences();
    Decide(decision, settings, 0, CameraStatus.Active); Decide(decision, settings, 500, CameraStatus.Active);
    Equal(true, Decide(decision, settings, 1000, CameraStatus.Unknown));
    Equal(true, Decide(decision, settings, 9999, CameraStatus.Unknown));
    Equal(true, decision.Update(settings, null, ManualMode.Automatic, now.AddMinutes(2)));
    Equal(true, decision.Update(settings, Snapshot(now, CameraStatus.Inactive), ManualMode.Automatic, now.AddSeconds(5)));
    Equal(true, decision.Update(settings, Snapshot(now.AddSeconds(6), CameraStatus.Inactive, epoch: 2), ManualMode.Automatic, now.AddSeconds(6)));
    Equal(true, decision.Update(settings, Snapshot(now.AddSeconds(8), CameraStatus.Inactive, epoch: 3), ManualMode.Automatic, now.AddSeconds(8)));
});
Test("positive observed activity can turn on despite an uncertain baseline", () =>
{
    var decision = new AutomationDecision(); var settings = Preferences();
    Equal(false, decision.Update(settings, Snapshot(now, CameraStatus.Active, healthy: false), ManualMode.Automatic, now));
    Equal(true, decision.Update(settings, Snapshot(now.AddMilliseconds(500), CameraStatus.Active, healthy: false),
        ManualMode.Automatic, now.AddMilliseconds(500)));
    Equal(true, decision.Update(settings, Snapshot(now.AddSeconds(5), CameraStatus.Inactive, healthy: false),
        ManualMode.Automatic, now.AddSeconds(5)));
});
Test("selection changes, pause, manual override and disabled defaults", () =>
{
    var decision = new AutomationDecision(); var settings = Preferences();
    settings.Apps[0] = settings.Apps[0] with { Enabled = false };
    Equal(false, Decide(decision, settings, 0, CameraStatus.Active));
    Equal(false, Decide(decision, settings, 1000, CameraStatus.Active));
    Equal(true, decision.Update(settings, null, ManualMode.ForceOn, now));
    Equal(false, decision.Update(settings, null, ManualMode.Paused, now));
    settings.AutomationEnabled = false;
    Equal(false, decision.Update(settings, null, ManualMode.ForceOn, now));
});
Test("crash-recovered ownership holds during initial unknown", () =>
{
    var decision = new AutomationDecision(); decision.HoldRecoveredOwnership();
    Equal(true, decision.Update(Preferences(), null, ManualMode.Automatic, now));
});
Test("settings and IPC use source-generated JSON", () =>
{
    var settings = Preferences();
    string json = JsonSerializer.Serialize(settings, AppJson.Default.Settings);
    Equal(500, JsonSerializer.Deserialize(json, AppJson.Default.Settings)!.ActivationMilliseconds);
    var snapshot = Snapshot(now, CameraStatus.Active);
    string line = JsonSerializer.Serialize(snapshot, AppJson.Default.MonitorSnapshot);
    Equal(CameraStatus.Active, JsonSerializer.Deserialize(line, AppJson.Default.MonitorSnapshot)!.Apps[0].Status);
    Equal("{\"numberOfLights\":1,\"lights\":[{\"on\":1,\"brightness\":40,\"temperature\":250}]}",
        JsonSerializer.Serialize(new LightRequest(1, [new(1, 40, 250)]), AppJson.Default.LightRequest));
});
Test("LAN subnet binding, validation and unsupported endpoints", () =>
{
    Equal(true, LanInterfaces.SameSubnet(System.Net.IPAddress.Parse("192.0.2.1"),
        System.Net.IPAddress.Parse("192.0.2.2"), System.Net.IPAddress.Parse("255.255.255.0")));
    Equal(false, LanInterfaces.SameSubnet(System.Net.IPAddress.Parse("192.0.2.1"),
        System.Net.IPAddress.Parse("198.51.100.2"), System.Net.IPAddress.Parse("255.255.255.0")));
    Throws(() => LightEndpoint.Validate(new("l", "Light", "127.0.0.1", 9123, "lan")));
    Throws(() => LightEndpoint.Validate(new("l", "Light", "example.invalid", 9123, "lan")));
    Throws(() => new Settings { ActivationMilliseconds = 99 }.Validate());
});
Test("mDNS query, compressed PTR/SRV/A and TLS TXT", () =>
{
    byte[] query = MdnsPacket.Query("_elg._tcp.local", 12);
    Equal(1, (int)query[5]); Equal(0, MdnsPacket.Read(query).Length);
    using var stream = new MemoryStream();
    void Word(ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); }
    void Name(string value)
    {
        foreach (var label in value.Split('.')) { stream.WriteByte((byte)label.Length); stream.Write(Encoding.ASCII.GetBytes(label)); }
        stream.WriteByte(0);
    }
    void Pointer(int offset) => Word((ushort)(0xC000 | offset));
    void Header(ushort type, ushort size) { Word(type); Word(1); stream.Write(new byte[] { 0, 0, 0, 120 }); Word(size); }
    stream.Write(new byte[12]);
    int serviceOffset = (int)stream.Position;
    Name("_elg._tcp.local"); Header(12, 9);
    int instanceOffset = (int)stream.Position;
    stream.WriteByte(6); stream.Write(Encoding.ASCII.GetBytes("studio")); Pointer(serviceOffset);
    Pointer(instanceOffset); Header(33, 18);
    Word(0); Word(0); Word(9123);
    int hostOffset = (int)stream.Position; Name("lamp.local");
    Pointer(hostOffset); Header(1, 4); stream.Write(new byte[] { 192, 0, 2, 20 });
    Pointer(instanceOffset); Header(16, 6);
    stream.Write(new byte[] { 5, (byte)'t', (byte)'l', (byte)'s', (byte)'=', (byte)'1' });
    byte[] packet = stream.ToArray(); packet[7] = 4;
    var records = MdnsPacket.Read(packet);
    Equal(4, records.Length);
    Equal("studio._elg._tcp.local", records[0].Target);
    Equal("studio._elg._tcp.local", records[1].Name);
    Equal("lamp.local", records[1].Target); Equal((ushort)9123, records[1].Port);
    Equal("lamp.local", records[2].Name); Equal((byte)192, records[2].Data[0]);
    Equal(true, MdnsPacket.UsesTls(records[3].Data));
    Equal(true, MdnsPacket.UsesTls(new byte[] { 5, (byte)'t', (byte)'l', (byte)'s', (byte)'=', (byte)'1' }));
    Equal(false, MdnsPacket.UsesTls(new byte[] { 5, (byte)'t', (byte)'l', (byte)'s', (byte)'=', (byte)'0' }));
});
Test("malformed mDNS packets and cyclic compression fail explicitly", () =>
{
    Throws(() => MdnsPacket.Read(new byte[3]));
    byte[] cycle = new byte[18]; cycle[5] = 1; cycle[12] = 0xC0; cycle[13] = 12;
    Throws(() => MdnsPacket.Read(cycle));
    Throws(() => MdnsPacket.UsesTls(new byte[] { 10, 1 }));
});

var light = new LightDevice("light", "Synthetic", "192.0.2.10", 9123, "test", true);
var fake = new FakeLights();
OwnershipJournal? journal = null;
var automation = new LightAutomation(fake, value => journal = value);
await automation.ReconcileAsync(true, [light], default);
Equal(new LightState(1, 40, 250), fake.State);
Equal(1, journal!.Lights.Count);
await automation.ReconcileAsync(false, [light], default);
Equal(new LightState(0, 40, 250), fake.State);
passed++; Console.WriteLine("PASS activation preserves brightness/temperature and restores owned state");

fake.State = new(1, 65, 300);
int writes = fake.Writes;
await automation.ReconcileAsync(true, [light], default);
await automation.ReconcileAsync(false, [light], default);
Equal(writes, fake.Writes);
passed++; Console.WriteLine("PASS already-on lights are never owned or switched off");

fake.State = new(0, 40, 250);
await automation.ReconcileAsync(true, [light], default);
fake.State = new(1, 75, 250);
await automation.ReconcileAsync(true, [light], default);
await automation.ReconcileAsync(false, [light], default);
Equal(new LightState(1, 75, 250), fake.State);
passed++; Console.WriteLine("PASS external light edits relinquish ownership");

fake.State = new(0, 40, 250);
await automation.ReconcileAsync(true, [light], default);
var recovered = new LightAutomation(fake, value => journal = value);
recovered.Recover(journal!);
await recovered.ReconcileAsync(false, [light], default);
Equal(new LightState(0, 40, 250), fake.State);
passed++; Console.WriteLine("PASS persisted crash ownership recovers and restores");

fake.FailWrite = true;
var failedAutomation = new LightAutomation(fake, value => journal = value);
try { await failedAutomation.ReconcileAsync(true, [light], default); throw new Exception("Expected network error"); }
catch (IOException) { }
Equal(1, journal!.Lights.Count);
fake.FailWrite = false;
passed++; Console.WriteLine("PASS failed writes retain recovery intent");

var partial = new FakeLights { FailId = "bad" };
var multi = new LightAutomation(partial, _ => { });
try
{
    await multi.ReconcileAsync(true, [light with { Id = "bad" }, light], default);
    throw new Exception("Expected partial network error");
}
catch (IOException) { }
Equal(1, partial.Writes);
passed++; Console.WriteLine("PASS an unreachable light does not block reachable lights");

string directory = Path.Combine(Path.GetTempPath(), "CameraLightTray-test-" + Guid.NewGuid().ToString("N"));
try
{
    var storage = new Storage(directory);
    storage.Save("settings.json", Preferences(), AppJson.Default.Settings);
    Equal(true, storage.Load("settings.json", AppJson.Default.Settings)!.AutomationEnabled);
    Equal(false, File.Exists(Path.Combine(directory, "settings.json.tmp")));
    passed++; Console.WriteLine("PASS atomic local preference persistence");
}
finally
{
    File.Delete(Path.Combine(directory, "settings.json"));
    Directory.Delete(directory);
}
if (OperatingSystem.IsWindows())
{
    using var identity = WindowsIdentity.GetCurrent();
    var security = new PipeSecurity();
    security.SetOwner(identity.User!);
    security.SetAccessRuleProtection(true, false);
    security.AddAccessRule(new(identity.User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
    string pipeName = "CameraLightTray-test-" + Guid.NewGuid().ToString("N");
    using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous, 4096, 4096, security);
    Task connection = server.WaitForConnectionAsync();
    using var client = MonitorClient.Create(pipeName);
    await client.ConnectAsync(2000);
    await connection;
    try { MonitorClient.VerifyServer(client); throw new Exception("Spoofed pipe must not be trusted."); }
    catch (UnauthorizedAccessException) { }
    await client.WriteAsync(new byte[] { 1 });
    byte[] handshake = new byte[1];
    Equal(1, await server.ReadAsync(handshake));
    string? sid = null;
    server.RunAsClient(() =>
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var caller = WindowsIdentity.GetCurrent(); sid = caller.User?.Value;
    });
    Equal(identity.User!.Value, sid);
    passed++; Console.WriteLine("PASS explicit read/write pipe rights, Windows client impersonation and spoofed-server rejection");
}
Console.WriteLine($"{passed} tests passed.");

internal sealed class FakeLights : ILightTransport
{
    internal LightState State = new(0, 40, 250);
    internal int Writes;
    internal bool FailWrite;
    internal string? FailId;
    public Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken) =>
        device.Id == FailId ? Task.FromException<LightState>(new IOException("Synthetic offline light")) : Task.FromResult(State);
    public Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken)
    {
        if (FailWrite) throw new IOException("Synthetic write failure");
        Writes++; State = state; return Task.CompletedTask;
    }
}
