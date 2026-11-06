using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using CameraLightTray.App.Platform;
using CameraLightTray.Tests;
using CameraLightTray.Core;

if (args is ["--sensor-recovery-check", var sensorResultPath])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    try { await SensorMonitorCheck.RunAsync(sensorResultPath); }
    catch (Exception ex) { File.WriteAllText(sensorResultPath, "FAIL: " + ex.Message); Environment.ExitCode = 1; }
    return;
}
if (args is ["--sensor-observe"])
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    int reports = 0, failures = 0;
    using var monitor = new SensorActivityMonitor(activities =>
    {
        Interlocked.Increment(ref reports);
        Console.WriteLine($"{DateTimeOffset.UtcNow:O} cameras={activities.Length}; clients={activities.Sum(a => a.Processes.Length)}; " +
            $"streaming={activities.Sum(a => a.Processes.Count(p => p.Streaming))}; " +
            $"per-camera={string.Join(",", activities.Select(a => a.Processes.Count(p => p.Streaming)))}");
    }, error => { Interlocked.Increment(ref failures); Console.Error.WriteLine(error); });
    monitor.Start();
    await Task.Delay(TimeSpan.FromSeconds(20));
    if (reports == 0 || failures != 0) Environment.ExitCode = 1;
    return;
}
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
Test("unrelated owners do not poison health but unattributed session captures block release", () =>
{
    var tracker = Tracker();
    tracker.Synchronize([process], [99]);
    Equal(true, tracker.AttributionComplete);
    tracker.UnattributedTransition(98, 211, 0); // Not an unresolved owner in this session.
    tracker.UnattributedTransition(99, 7, 0);
    tracker.UnattributedTransition(99, 211, 0x80004005);
    Equal(true, tracker.AttributionComplete);
    tracker.UnattributedTransition(99, 211, 0);
    tracker.UnattributedTransition(99, 213, 0);
    Equal(false, tracker.AttributionComplete);
    tracker.Synchronize([process], [99]); Equal(false, tracker.AttributionComplete);
    tracker.Synchronize([process]); Equal(true, tracker.AttributionComplete);
});

var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
CameraDevice[] devices = [new("camera:one", "Example Camera"), new("camera:two", "Second Camera")];
var sensorProcess = process with { StartTime = now.AddMinutes(-1).ToFileTime() };
SensorActivity Activity(string id, bool streaming, int pid = 42, long? time = null) =>
    new(id, "Example Camera", [new(pid, streaming, time ?? now.ToFileTime())]);
MonitorSnapshot SensorSnapshot(SensorState sensor, string[]? selected = null, int[]? excluded = null) =>
    sensor.Snapshot([sensorProcess], excluded ?? [], devices, selected ?? ["camera:one"], now);
Test("sensor startup is unknown, authoritative inventory includes already-active camera", () =>
{
    var sensor = new SensorState();
    Equal(false, SensorSnapshot(sensor).Healthy);
    Equal(CameraStatus.Unknown, SensorSnapshot(sensor).Apps.Single().Status);
    sensor.Receive([Activity("camera:one", true)]);
    Equal(true, SensorSnapshot(sensor).Healthy);
    Equal(CameraStatus.Active, SensorSnapshot(sensor).Apps.Single().Status);
    sensor.Receive([]);
    Equal(CameraStatus.Active, SensorSnapshot(sensor).Apps.Single().Status);
    sensor.Receive([Activity("camera:one", false)]);
    Equal(CameraStatus.Inactive, SensorSnapshot(sensor).Apps.Single().Status);
});
Test("empty and partial sensor notifications never infer another camera stopped", () =>
{
    var sensor = new SensorState();
    sensor.Receive([]);
    Equal(false, SensorSnapshot(sensor).Healthy);
    sensor.Receive([Activity("camera:one", true)]);
    Equal(false, SensorSnapshot(sensor, []).Healthy);
    Equal(CameraStatus.Unknown, SensorSnapshot(sensor, ["camera:two"]).Apps.Single().Status);
    sensor.Receive([Activity("camera:two", false)]);
    Equal(true, SensorSnapshot(sensor, []).Healthy);
    Equal(CameraStatus.Active, SensorSnapshot(sensor, []).Apps.Single().Status);
    sensor.Receive([]);
    Equal(CameraStatus.Active, SensorSnapshot(sensor, []).Apps.Single().Status);
    sensor.Receive([new("camera:one", "Example Camera", [])]);
    Equal(CameraStatus.Inactive, SensorSnapshot(sensor, []).Apps.Single().Status);
});
Test("sensor camera filters and multiple cameras aggregate correctly", () =>
{
    var sensor = new SensorState();
    sensor.Receive([Activity("camera:one", true), Activity("camera:two", false)]);
    Equal(CameraStatus.Active, SensorSnapshot(sensor, []).Apps.Single().Status);
    Equal(CameraStatus.Inactive, SensorSnapshot(sensor, ["camera:two"]).Apps.Single().Status);
    Equal(CameraStatus.Active, SensorSnapshot(sensor, ["CAMERA:ONE"]).Apps.Single().Status);
    sensor.Receive([Activity("camera:one", false), Activity("camera:two", true)]);
    Equal(CameraStatus.Active, SensorSnapshot(sensor, []).Apps.Single().Status);
    Equal(CameraStatus.Inactive, SensorSnapshot(sensor, ["camera:one"]).Apps.Single().Status);
    Equal(false, SensorSnapshot(sensor, ["camera:missing"]).Healthy);
    Equal(CameraStatus.Unknown, SensorSnapshot(sensor, ["camera:missing"]).Apps.Single().Status);
});
Test("sensor failures and resume discard state, not inferred camera-off", () =>
{
    var sensor = new SensorState(); sensor.Receive([Activity("camera:one", true)]);
    long epoch = SensorSnapshot(sensor).Epoch;
    sensor.Invalidate("Disconnected");
    Equal(epoch + 1, SensorSnapshot(sensor).Epoch);
    Equal(false, SensorSnapshot(sensor).Healthy);
    Equal(CameraStatus.Unknown, SensorSnapshot(sensor).Apps.Single().Status);
    sensor.Receive([Activity("camera:one", false)]);
    Equal(true, SensorSnapshot(sensor).Healthy);
    Equal(CameraStatus.Inactive, SensorSnapshot(sensor).Apps.Single().Status);
});
Test("sensor unresolved clients block release, positively foreign clients do not", () =>
{
    var sensor = new SensorState();
    sensor.Receive([Activity("camera:one", true, 99)]);
    Equal(false, SensorSnapshot(sensor).Healthy);
    Equal(CameraStatus.Unknown, SensorSnapshot(sensor).Apps.Single().Status);
    Equal(true, SensorSnapshot(sensor, excluded: [99]).Healthy);
    sensor.Receive([new("camera:one", "Example Camera", [new(42, true, now.ToFileTime()), new(99, true, now.ToFileTime())])]);
    Equal(false, SensorSnapshot(sensor).Healthy);
    Equal(CameraStatus.Active, SensorSnapshot(sensor).Apps.Single().Status);
    sensor.Receive([Activity("camera:two", false)]);
    Equal(true, SensorSnapshot(sensor, ["camera:two"]).Healthy);
});
Test("sensor PID reuse and future report times cannot activate a new identity", () =>
{
    var sensor = new SensorState();
    sensor.Receive([Activity("camera:one", true, time: sensorProcess.StartTime - 1)]);
    Equal(false, SensorSnapshot(sensor).Healthy);
    Equal(CameraStatus.Unknown, SensorSnapshot(sensor).Apps.Single().Status);
    sensor.Receive([Activity("camera:one", true, time: now.AddSeconds(2).ToFileTime())]);
    Equal(false, SensorSnapshot(sensor).Healthy);
});
Test("camera filters roundtrip, legacy defaults and invalid identities", () =>
{
    var settings = JsonSerializer.Deserialize("{}", AppJson.Default.Settings)!;
    Equal(0, settings.CameraIds.Count);
    settings.CameraIds = ["camera:one"];
    settings.Validate();
    Equal("camera:one", JsonSerializer.Deserialize(JsonSerializer.Serialize(settings, AppJson.Default.Settings),
        AppJson.Default.Settings)!.CameraIds.Single());
    settings.CameraIds = ["camera:one", "CAMERA:ONE"]; Throws(settings.Validate);
    settings.CameraIds = [" "]; Throws(settings.Validate);
});
Test("app suggestions recognize communications and packaged camera identities, not arbitrary names", () =>
{
    Equal(true, AppPriority.IsCameraOrCommunicationApp("package:Example_abc|WindowsCamera.exe"));
    Equal(true, AppPriority.IsCameraOrCommunicationApp(@"C:\Example\Signal.exe"));
    Equal(true, AppPriority.IsCameraOrCommunicationApp(@"C:\Example\ms-teams.exe"));
    Equal(false, AppPriority.IsCameraOrCommunicationApp(@"C:\Example\steam.exe"));
});
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
Test("removing every selected app releases during sensor failure without disabling manual-on", () =>
{
    var decision = new AutomationDecision();
    var settings = Preferences();
    decision.HoldRecoveredOwnership();
    settings.Apps = [];
    Equal(false, decision.Update(settings, null, ManualMode.Automatic, now));
    Equal(true, decision.Update(settings, null, ManualMode.ForceOn, now));
    Equal(false, decision.Update(settings, null, ManualMode.Automatic, now));
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
Test("explicit LAN source binding, routed subnets, validation and unsupported endpoints", () =>
{
    Equal(true, LanInterfaces.SameSubnet(System.Net.IPAddress.Parse("192.0.2.1"),
        System.Net.IPAddress.Parse("192.0.2.2"), System.Net.IPAddress.Parse("255.255.255.0")));
    Equal(false, LanInterfaces.SameSubnet(System.Net.IPAddress.Parse("192.0.2.1"),
        System.Net.IPAddress.Parse("198.51.100.2"), System.Net.IPAddress.Parse("255.255.255.0")));
    Throws(() => LightEndpoint.Validate(new("l", "Light", "127.0.0.1", 9123, "lan")));
    Throws(() => LightEndpoint.Validate(new("l", "Light", "example.invalid", 9123, "lan")));
    Throws(() => new Settings { ActivationMilliseconds = 99 }.Validate());
    LanAddress[] addresses = [new("vpn", "VPN", "192.0.2.1", "255.255.255.0"),
        new("lan", "LAN", "198.51.100.1", "255.255.255.0"),
        new("lan", "LAN alias", "203.0.113.1", "255.255.255.0")];
    Equal("198.51.100.1", LanInterfaces.SelectSource(addresses, "lan", System.Net.IPAddress.Parse("192.0.2.20")).Address);
    Equal("203.0.113.1", LanInterfaces.SelectSource(addresses, "lan", System.Net.IPAddress.Parse("203.0.113.20")).Address);
    try { LanInterfaces.SelectSource(addresses, "missing", System.Net.IPAddress.Parse("192.0.2.20")); throw new Exception("Unavailable interface must fail."); }
    catch (IOException) { }
});
Test("mDNS query, compressed PTR/SRV/A and TLS TXT", () =>
{
    byte[] query = MdnsPacket.Query("_elg._tcp.local", 12);
    Equal(1, (int)query[5]); Equal(0, MdnsPacket.Read(query).Length);
    Equal((ushort)1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(query.Length - 2)));
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
    var discovered = MdnsDiscovery.ResolveRecords(records, "routed-lan");
    Equal(1, discovered.Length); Equal("192.0.2.20", discovered[0].Host);
    Equal("routed-lan", discovered[0].InterfaceId); Equal(true, discovered[0].Tls);
});
Test("malformed mDNS packets and cyclic compression fail explicitly", () =>
{
    Throws(() => MdnsPacket.Read(new byte[3]));
    byte[] cycle = new byte[18]; cycle[5] = 1; cycle[12] = 0xC0; cycle[13] = 12;
    Throws(() => MdnsPacket.Read(cycle));
    Throws(() => MdnsPacket.UsesTls(new byte[] { 10, 1 }));
});

var light = new LightDevice("light", "Synthetic", "192.0.2.10", 9123, "test", true);
using (var content = ElgatoTransport.CreateWriteContent(new(1, 26, 205)))
{
    string body = await content.ReadAsStringAsync();
    Equal("application/json", content.Headers.ContentType!.ToString());
    Equal((long)Encoding.UTF8.GetByteCount(body), content.Headers.ContentLength!.Value);
    Equal("{\"numberOfLights\":1,\"lights\":[{\"on\":1,\"brightness\":26,\"temperature\":205}]}", body);
    passed++; Console.WriteLine("PASS Elgato HTTP writes have explicit byte length, no chunked JSON or charset suffix");
}
var fake = new FakeLights();
var lightClock = new ManualClock(now);
OwnershipJournal? journal = null;
var automation = new LightAutomation(fake, value => journal = value, lightClock);
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
lightClock.Advance(TimeSpan.FromSeconds(1));
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
var failedAutomation = new LightAutomation(fake, value => journal = value, lightClock);
try { await failedAutomation.ReconcileAsync(true, [light], default); throw new Exception("Expected network error"); }
catch (IOException) { }
Equal(1, journal!.Lights.Count);
fake.FailWrite = false;
passed++; Console.WriteLine("PASS failed writes retain recovery intent");
lightClock.Advance(TimeSpan.FromSeconds(2));
await failedAutomation.ReconcileAsync(true, [light], default);
Equal(new LightState(1, 40, 250), fake.State);
Equal(true, journal!.Lights.Single().AppliedConfirmed);
await failedAutomation.ReconcileAsync(false, [light], default);
Equal(new LightState(0, 40, 250), fake.State);
Equal(0, journal!.Lights.Count);
passed++; Console.WriteLine("PASS failed un-applied writes retry without being mistaken for external edits");
fake.FailWrite = true;
var abandoned = new LightAutomation(fake, value => journal = value);
try { await abandoned.ReconcileAsync(true, [light], default); throw new Exception("Expected network error"); }
catch (IOException) { }
fake.FailWrite = false;
writes = fake.Writes;
await abandoned.ReconcileAsync(false, [light], default);
Equal(writes, fake.Writes);
Equal(0, journal!.Lights.Count);
passed++; Console.WriteLine("PASS disabling a failed unapplied activation leaves original light state alone");
Test("older ownership journals retain confirmed-state restoration semantics", () =>
{
    string json = JsonSerializer.Serialize(new OwnershipJournal([new(light, new(0, 40, 250), new(1, 40, 250))]),
        AppJson.Default.OwnershipJournal).Replace(",\"appliedConfirmed\":true", "", StringComparison.Ordinal);
    Equal(false, json.Contains("appliedConfirmed"));
    Equal(true, JsonSerializer.Deserialize(json, AppJson.Default.OwnershipJournal)!.Lights.Single().AppliedConfirmed);
});

{
    var blocked = new TaskCompletionSource<LightState>(TaskCreationOptions.RunContinuationsAsynchronously);
    var reachable = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var independent = new DelegateLights((device, _) => device.Id == "blocked" ? blocked.Task : Task.FromResult(new LightState(0, 40, 250)),
        (_, _, _) => { reachable.TrySetResult(); return Task.CompletedTask; });
    var parallel = new LightAutomation(independent, _ => { });
    var reconcile = parallel.ReconcileAsync(true, [light with { Id = "blocked" }, light], default);
    try
    {
        await reachable.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Equal(false, reconcile.IsCompleted);
    }
    finally { blocked.TrySetException(new IOException("Offline fixture")); }
    try { await reconcile; throw new Exception("Expected partial network error"); }
    catch (IOException) { }
    Equal(1, parallel.Owned.Count);
    passed++; Console.WriteLine("PASS a blocked light cannot delay another light's activation");
}
{
    var retryClock = new ManualClock(now);
    int reads = 0;
    bool offline = true;
    var state = new LightState(0, 37, 220);
    var retryTransport = new DelegateLights((_, _) =>
    {
        reads++;
        return offline ? Task.FromException<LightState>(new IOException("Offline fixture")) : Task.FromResult(state);
    }, (_, value, _) => { state = value; return Task.CompletedTask; });
    var retryLights = new LightAutomation(retryTransport, _ => { }, retryClock);
    async Task FailsAsync()
    {
        try { await retryLights.ReconcileAsync(true, [light], default); throw new Exception("Expected offline error"); }
        catch (IOException) { }
    }
    await FailsAsync();
    await FailsAsync();
    Equal(1, reads);
    retryClock.Advance(TimeSpan.FromMilliseconds(1999));
    await FailsAsync();
    Equal(1, reads);
    retryClock.Advance(TimeSpan.FromMilliseconds(1));
    await FailsAsync();
    Equal(2, reads);
    int expectedReads = 2;
    foreach (int seconds in new[] { 4, 8, 16, 30, 30 })
    {
        retryClock.Advance(TimeSpan.FromMilliseconds(seconds * 1000 - 1));
        await FailsAsync();
        Equal(expectedReads, reads);
        retryClock.Advance(TimeSpan.FromMilliseconds(1));
        await FailsAsync();
        Equal(++expectedReads, reads);
    }
    offline = false;
    retryClock.Advance(TimeSpan.FromSeconds(30));
    await retryLights.ReconcileAsync(true, [light], default);
    Equal(1, state.On);
    await retryLights.ReconcileAsync(false, [light], default);
    Equal(new LightState(0, 37, 220), state);
    passed++; Console.WriteLine("PASS offline light retries back off at exact boundaries, recover and restore original state");
}
{
    var pollingClock = new ManualClock(now);
    int reads = 0;
    var polling = new LightAutomation(new DelegateLights((_, _) =>
    {
        reads++;
        return Task.FromResult(new LightState(1, 40, 250));
    }, (_, _, _) => throw new InvalidOperationException("Already-on light must not be written.")), _ => { }, pollingClock);
    await polling.ReconcileAsync(true, [light], default);
    await polling.ReconcileAsync(true, [light], default);
    Equal(1, reads);
    pollingClock.Advance(TimeSpan.FromMilliseconds(999));
    await polling.ReconcileAsync(true, [light], default);
    Equal(1, reads);
    pollingClock.Advance(TimeSpan.FromMilliseconds(1));
    await polling.ReconcileAsync(true, [light], default);
    Equal(2, reads);
    passed++; Console.WriteLine("PASS healthy light polling is limited at the exact one-second boundary");
}
{
    var ackClock = new ManualClock(now);
    var state = new LightState(0, 40, 250);
    bool loseAck = true;
    OwnershipJournal? ackJournal = null;
    var ackTransport = new DelegateLights((_, _) => Task.FromResult(state), (_, value, _) =>
    {
        state = value;
        if (loseAck) throw new IOException("Device applied write but acknowledgment was lost.");
        return Task.CompletedTask;
    });
    var ackLights = new LightAutomation(ackTransport, value => ackJournal = value, ackClock);
    try { await ackLights.ReconcileAsync(true, [light], default); throw new Exception("Expected lost acknowledgment"); }
    catch (IOException) { }
    Equal(false, ackJournal!.Lights.Single().AppliedConfirmed);
    loseAck = false;
    ackClock.Advance(TimeSpan.FromSeconds(2));
    await ackLights.ReconcileAsync(true, [light], default);
    Equal(true, ackJournal!.Lights.Single().AppliedConfirmed);
    state = state with { On = 0 };
    ackClock.Advance(TimeSpan.FromSeconds(1));
    await ackLights.ReconcileAsync(true, [light], default);
    Equal(0, state.On);
    Equal(0, ackJournal!.Lights.Count);
    passed++; Console.WriteLine("PASS lost write acknowledgments become confirmed on readback, preserving subsequent external manual-off");
}
{
    var endpointTransport = new FakeLights();
    OwnershipJournal? endpointJournal = null;
    var endpointLights = new LightAutomation(endpointTransport, value => endpointJournal = value);
    await endpointLights.ReconcileAsync(true, [light], default);
    var changed = light with { Host = "192.0.2.11", InterfaceId = "replacement-lan" };
    await endpointLights.ReconcileAsync(true, [changed], default);
    Equal(changed, endpointJournal!.Lights.Single().Device);
    await endpointLights.ReconcileAsync(false, [changed], default);
    Equal(changed, endpointTransport.LastDevice);
    Equal(new LightState(0, 40, 250), endpointTransport.State);
    Equal(0, endpointJournal!.Lights.Count);
    passed++; Console.WriteLine("PASS rediscovered endpoint/interface updates existing ownership before restoration");
}
{
    var durableClock = new ManualClock(now);
    var durableTransport = new FakeLights();
    bool storageBroken = true;
    bool failAfterRestore = false;
    OwnershipJournal? durableJournal = null;
    var durable = new LightAutomation(durableTransport, value =>
    {
        if (storageBroken && (!failAfterRestore || durableTransport.Writes >= 2))
            throw new IOException("Synthetic journal failure");
        durableJournal = value;
    }, durableClock);
    try { await durable.ReconcileAsync(true, [light], default); throw new Exception("Expected persistence error"); }
    catch (IOException) { }
    Equal(0, durableTransport.Writes);
    storageBroken = false;
    durableClock.Advance(TimeSpan.FromSeconds(2));
    await durable.ReconcileAsync(true, [light], default);
    Equal(1, durableTransport.Writes);
    Equal(true, durableJournal!.Lights.Single().AppliedConfirmed);
    storageBroken = true;
    failAfterRestore = true;
    try { await durable.ReconcileAsync(false, [light], default); throw new Exception("Expected persistence error"); }
    catch (IOException) { }
    storageBroken = false;
    await durable.ReconcileAsync(false, [light], default);
    Equal(0, durableJournal!.Lights.Count);
    Equal(new LightState(0, 40, 250), durableTransport.State);
    passed++; Console.WriteLine("PASS journal failures prevent unrecorded writes and retry pending persistence");
}
{
    int requests = 0;
    var responseTransport = new ElgatoTransport(_ => new HttpClient(new FixtureHttp(_ =>
    {
        int request = Interlocked.Increment(ref requests);
        return new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(request == 1 ? "{bad-json" :
                "{\"numberOfLights\":1,\"lights\":[{\"on\":0,\"brightness\":26,\"temperature\":205}]}")
        };
    })) { BaseAddress = new Uri("http://192.0.2.10:9123") });
    try { await responseTransport.ReadAsync(light, default); throw new Exception("Expected malformed response error"); }
    catch (InvalidDataException ex) { Equal(true, ex.InnerException is JsonException); }
    Equal(new LightState(0, 26, 205), await responseTransport.ReadAsync(light, default));
    passed++; Console.WriteLine("PASS malformed device JSON is recoverable I/O failure, not a fatal automation worker exception");
}
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
using (var retryStop = new CancellationTokenSource())
{
    var sensor = new SensorState();
    long retiredEpoch = sensor.Epoch;
    int attempts = 0, failures = 0;
    await SensorMonitorRecovery.RunAsync(_ =>
    {
        if (++attempts == 1) throw new System.ComponentModel.Win32Exception(1722, "Process owner query failed.");
        Equal(false, sensor.Snapshot([], [], [], [], DateTimeOffset.UtcNow).Healthy);
        Equal(false, sensor.Receive(retiredEpoch, [new("camera:retry", "Retry Camera", [])]));
        Equal(false, sensor.Snapshot([], [], [], [], DateTimeOffset.UtcNow).Healthy);
        Equal(true, sensor.Receive(sensor.Epoch, [new("camera:retry", "Retry Camera", [])]));
        Equal(true, sensor.Snapshot([], [], [], [], DateTimeOffset.UtcNow).Healthy);
        retryStop.Cancel();
        return Task.CompletedTask;
    }, ex =>
    {
        Equal(1722, ((System.ComponentModel.Win32Exception)ex).NativeErrorCode);
        failures++;
        sensor.Invalidate(ex.Message);
        Equal(false, sensor.Snapshot([], [], [], [], DateTimeOffset.UtcNow).Healthy);
    }, TimeSpan.Zero, retryStop.Token);
    Equal(2, attempts);
    Equal(1, failures);
    passed++; Console.WriteLine("PASS transient process lookup failure retries with unknown state until fresh reports; retired callbacks are rejected");
}
using (var retryStop = new CancellationTokenSource())
{
    int attempts = 0;
    var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var recovery = SensorMonitorRecovery.RunAsync(_ =>
    {
        attempts++;
        throw new IOException("Sensor disconnected.");
    }, _ => failed.SetResult(), TimeSpan.FromSeconds(30), retryStop.Token);
    await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    retryStop.Cancel();
    await recovery.WaitAsync(TimeSpan.FromSeconds(2));
    Equal(1, attempts);
    passed++; Console.WriteLine("PASS monitor retry backoff cancels promptly without starting another source");
}
{
    bool reported = false, propagated = false;
    try
    {
        await SensorMonitorRecovery.RunAsync(_ => throw new InvalidOperationException("Unexpected worker failure."),
            _ => reported = true, TimeSpan.Zero, default);
    }
    catch (InvalidOperationException) { propagated = true; }
    Equal(true, propagated);
    Equal(false, reported);
    passed++; Console.WriteLine("PASS unexpected monitor failures propagate instead of silently retrying");
}
if (OperatingSystem.IsWindows())
{
    using var identity = WindowsIdentity.GetCurrent();
    using var ownProcess = System.Diagnostics.Process.GetCurrentProcess();
    var catalogClock = System.Diagnostics.Stopwatch.StartNew();
    var ownCatalog = CameraLightTray.Monitor.ProcessCatalog.Enumerate(identity.User!.Value, ownProcess.SessionId);
    Equal(true, ownCatalog.Processes.Any(p => p.Pid == ownProcess.Id && p.UserSid == identity.User.Value));
    Equal(ownProcess.SessionId, ownCatalog.Processes.Single(p => p.Pid == ownProcess.Id).SessionId);
    if (catalogClock.Elapsed >= TimeSpan.FromSeconds(4))
        throw new InvalidOperationException($"Process catalog exceeded the 4-second heartbeat deadline: {catalogClock.ElapsedMilliseconds}ms.");
    passed++; Console.WriteLine($"PASS process catalog stays within heartbeat deadline ({catalogClock.ElapsedMilliseconds}ms)");
    var otherCatalog = CameraLightTray.Monitor.ProcessCatalog.Enumerate("S-1-0-0", ownProcess.SessionId);
    Equal(0, otherCatalog.Processes.Length);
    passed++; Console.WriteLine("PASS WTS process owner enumeration and SID/session-filtered executable identities");
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
    using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
    try
    {
        await MonitorClient.ReadHeartbeatAsync(reader, CancellationToken.None);
        throw new InvalidOperationException("Stalled helper must time out.");
    }
    catch (TimeoutException ex)
    {
        Equal(true, ex.Message.Contains("connected") && ex.Message.Contains("does not mean setup failed"));
    }
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        await MonitorClient.ReadHeartbeatAsync(reader, cancelled.Token);
        throw new InvalidOperationException("Shutdown must remain cancellation, not a helper timeout.");
    }
    catch (OperationCanceledException) { }
    passed++; Console.WriteLine("PASS stalled helper reports heartbeat timeout separately from setup failure and shutdown");
}
passed += await StudioChecks.RunAsync();
Console.WriteLine($"{passed} tests passed.");

internal sealed class FakeLights : ILightTransport
{
    internal LightState State = new(0, 40, 250);
    internal int Writes;
    internal bool FailWrite;
    internal string? FailId;
    internal LightDevice? LastDevice;
    public Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken) =>
        device.Id == FailId ? Task.FromException<LightState>(new IOException("Synthetic offline light")) : Task.FromResult(State);
    public Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken)
    {
        if (FailWrite) throw new IOException("Synthetic write failure");
        LastDevice = device;
        Writes++; State = state; return Task.CompletedTask;
    }

}

internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;
    public override DateTimeOffset GetUtcNow() => _now;
    internal void Advance(TimeSpan duration) => _now += duration;
}

internal sealed class DelegateLights(Func<LightDevice, CancellationToken, Task<LightState>> read,
    Func<LightDevice, LightState, CancellationToken, Task> write) : ILightTransport
{
    public Task<LightState> ReadAsync(LightDevice device, CancellationToken cancellationToken) => read(device, cancellationToken);
    public Task WriteAsync(LightDevice device, LightState state, CancellationToken cancellationToken) => write(device, state, cancellationToken);
}

internal sealed class FixtureHttp(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(respond(request));
}
