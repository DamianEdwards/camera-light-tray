using CameraLightTray.App.Native;
using CameraLightTray.Core;
using CameraLightTray.App.UI;

internal static class StudioChecks
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task<int> RunAsync()
    {
        int passed = 0;
        void Pass(string text) { Console.WriteLine("PASS " + text); passed++; }
        var device = new LightDevice("studio", "Renamed desk light", "192.0.2.10", 9123, "lan", true) { ProductName = "Elgato Key Light Air" };
        Check(LightControls.Model(device) == LightModel.KeyLightAir, "Product metadata must survive renamed lights.");
        Check(LightControls.Model(device with { ProductName = "Unknown light" }) == LightModel.Unknown, "Do not guess unknown product art.");
        Check(LightControls.Model(device with { ProductName = "Elgato Key Light Neo" }) == LightModel.Unknown, "A different model must not inherit Key Light artwork.");
        var legacy = System.Text.Json.JsonSerializer.Deserialize("{\"id\":\"x\",\"name\":\"Key Light\",\"host\":\"192.0.2.10\",\"port\":9123,\"interfaceId\":\"lan\"}", AppJson.Default.LightDevice);
        Check(legacy is not null && legacy.ProductName is null && LightControls.Model(legacy) == LightModel.KeyLight, "Legacy preferences must remain readable.");
        Check(LightControls.Temperature(2900) == 344 && LightControls.Temperature(7000) == 143 &&
            LightControls.Temperature(4000) == 250 && LightControls.Kelvin(250) == 4000, "Kelvin/mired boundaries must match firmware.");
        Check(LightControls.Adjust(new(0, 26, 205), brightness: 40) == new LightState(0, 40, 205), "Changing brightness must not change power or temperature.");
        Pass("Studio product metadata, legacy preferences, firmware temperature boundaries and field-preserving edits");
        byte[] Panel(LightState state) => Convert.FromHexString(LightPanelColor.FromReadback(state)[1..]);
        var warm = Panel(new(1, 100, LightControls.Temperature(2900)));
        var cool = Panel(new(1, 100, LightControls.Temperature(7000)));
        Check(warm[0] > warm[2] && cool[2] > cool[0] && cool[2] > warm[2], "Confirmed temperature must visibly tint the panel.");
        var dim = Panel(new(1, 10, 205));
        var bright = Panel(new(1, 90, 205));
        Check(dim.Zip(bright).All(pair => pair.First < pair.Second), "Confirmed brightness must change all panel channels monotonically.");
        Check(LightPanelColor.FromReadback(new(1, -1, 205)) == LightPanelColor.FromReadback(new(1, 0, 205)) &&
            LightPanelColor.FromReadback(new(1, 101, 205)) == LightPanelColor.FromReadback(new(1, 100, 205)), "Illustrative panel intensity must remain bounded.");
        Pass("product panels follow confirmed temperature and brightness with bounded illustrative colors");
        var oldSettings = System.Text.Json.JsonSerializer.Deserialize("{}", AppJson.Default.Settings)!;
        Check(oldSettings.Appearance == AppearanceMode.System, "Older preferences must follow the Windows theme.");
        foreach (var mode in Enum.GetValues<AppearanceMode>())
        {
            oldSettings.Appearance = mode;
            oldSettings.Validate();
            string json = System.Text.Json.JsonSerializer.Serialize(oldSettings, AppJson.Default.Settings);
            Check(System.Text.Json.JsonSerializer.Deserialize(json, AppJson.Default.Settings)!.Appearance == mode, "Appearance must survive restart.");
        }
        oldSettings.Appearance = (AppearanceMode)99;
        try { oldSettings.Validate(); throw new InvalidOperationException("Invalid appearance should fail explicitly."); }
        catch (InvalidDataException) { }
        Pass("shared appearance defaults to System for old preferences and persists all supported modes");

        var transport = new FakeLights();
        var automation = new LightAutomation(transport, _ => { });
        await automation.ReconcileAsync(true, [device], default);
        await automation.AdjustAsync(device, 0, null, null, default);
        Check(transport.State.On == 0 && automation.IsManual(device.Id) && automation.Owned.Count == 0, "Manual off must disown automation.");
        await automation.ReconcileAsync(true, [device], default);
        Check(transport.State.On == 0, "Camera activity must not fight manual off.");
        await automation.ResumeAsync(device.Id, default);
        await automation.ReconcileAsync(true, [device], default);
        Check(transport.State.On == 1 && !automation.IsManual(device.Id), "Explicit resume must immediately allow activation.");
        await automation.ReconcileAsync(false, [device], default);
        Check(transport.State.On == 0, "Resume establishes a new baseline which must restore.");
        Pass("manual off is respected during activity, explicit resume reactivates, original new baseline restores");

        await automation.ReconcileAsync(true, [device], default);
        await automation.AdjustAsync(device, null, 71, 3200, default);
        await automation.ReconcileAsync(false, [device], default);
        Check(transport.State.Brightness == 71 && transport.State.On == 1 && automation.Owned.Count == 0,
            "Manual brightness/temperature changes must survive activation ending.");
        Pass("manual brightness and temperature changes survive automatic release without stale ownership");

        var failTransport = new FakeLights();
        var recovery = new LightAutomation(failTransport, _ => { });
        await recovery.ReconcileAsync(true, [device], default);
        failTransport.FailWrite = true;
        try { await recovery.AdjustAsync(device, 0, null, null, default); throw new InvalidOperationException("Expected failed manual write."); }
        catch (IOException) { }
        Check(recovery.Owned.Count == 1, "Failed unacknowledged manual writes must retain recovery intent.");
        failTransport.FailWrite = false;
        await recovery.ReconcileAsync(false, [device], default);
        Check(failTransport.State.On == 0 && recovery.Owned.Count == 0, "Failed manual action must not orphan the old automatic state.");
        Pass("failed manual write retains the original ownership recovery intent");

        var unacknowledged = new FakeLights();
        var lostAck = new LightAutomation(new DelegateLights(unacknowledged.ReadAsync, async (light, state, token) =>
        {
            await unacknowledged.WriteAsync(light, state, token);
            if (state.On == 0) throw new IOException("Lost manual write acknowledgment.");
        }), _ => { });
        await lostAck.ReconcileAsync(true, [device], default);
        try { await lostAck.AdjustAsync(device, 0, null, null, default); throw new InvalidOperationException("Expected lost acknowledgment."); }
        catch (IOException) { }
        await lostAck.ReconcileAsync(true, [device], default);
        Check(unacknowledged.State.On == 0, "Do not fight a manual off with a lost acknowledgment.");
        await lostAck.ReconcileAsync(false, [device], default);
        Check(unacknowledged.State.On == 0 && lostAck.Owned.Count == 0, "Readback must preserve an actually applied manual off.");
        Pass("lost manual acknowledgments never reactivate the light or revert the user's applied state");

        var ignored = new LightAutomation(new DelegateLights((_, _) => Task.FromResult(new LightState(0, 26, 205)),
            (_, _, _) => Task.CompletedTask), _ => { });
        try { await ignored.AdjustAsync(device, 1, null, null, default); throw new InvalidOperationException("Expected mismatched readback."); }
        catch (IOException) { }
        Check(ignored.IsManual(device.Id), "A mismatched physical readback must surface failure, not claim success or resume automation.");
        Pass("manual controls verify physical readback instead of presenting requested state as confirmed");

        foreach (var work in new[] { new PixelRect(0, 0, 1920, 1040), new PixelRect(-2560, 100, 2560, 1340), new PixelRect(0, 0, 320, 240) })
        foreach (var anchor in new[] { new PixelRect(work.X + work.Width - 24, work.Y + work.Height, 20, 20),
            new PixelRect(work.X - 20, work.Y + 40, 20, 20), new PixelRect(work.X + 20, work.Y - 20, 20, 20) })
        {
            var result = FlyoutPlacement.Fit(work, anchor, 600, 950, 12);
            Check(result.X >= work.X && result.Y >= work.Y &&
                result.X + result.Width <= work.X + work.Width && result.Y + result.Height <= work.Y + work.Height,
                "Flyout must remain inside the target monitor work area at all taskbar positions.");
        }
        Pass("Studio flyout fits negative-origin monitors, small/high-DPI work areas and side/top/bottom taskbars");
        return passed;
    }
}
