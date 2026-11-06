# Camera Light Tray

An initial Windows tray-app spike: turn selected Elgato lights on when **any
selected app uses its camera**, then restore the states this app changed.
Written in C# targeting **.NET 11**, with **Microsoft UI Reactor / WinUI 3**,
published as **Windows x64 Native AOT**. The Windows App SDK is a **shared
dependency**, not bundled with the app. No .NET runtime installation is needed
for the published executables.

This is not yet a production camera-activity detector. Read the limitations
below before using it unattended. Automation, login startup, new app selections
and newly discovered lights are all opt-in. Background monitoring uses camera
activity metadata only and needs **no elevation or installed helper**.
An explicit local camera preview is available for verification; it never
records images, audio or video. The app sends no telemetry.

## What works in the spike

- Normal-user tray UI with friendly app names, per-app selections and camera
  status; saved choices survive process restarts. Packaged apps use package
  family plus executable identity, desktop apps use full executable paths.
- Windows Media Foundation **Sensor Activity Monitor** reports identify
  streaming processes and their actual camera devices, including an already
  active Windows Camera session. Select all cameras or a subset. Activity
  aggregates across selected apps and cameras; activation debounce defaults
  to 500ms and leave grace to 2500ms.
- Temporary sensor or process-owner query failures are shown as unknown and
  retried automatically after two seconds, rather than stopping monitoring.
  Recovery requires fresh process identities and camera reports; callbacks
  from the previous observation are ignored. Native query errors include
  their Windows error code for diagnostics.
- Empty or partial notifications are not a camera-off signal. Each monitored
  camera needs its own reported baseline; updates for another camera do not
  erase existing activity. Observations are renewed every minute to recover
  a silently stalled subscription. Unknown continues to hold owned lights.
- A compact **Studio tray flyout** with original product illustrations, distinct
  on/off/unknown states, independent Auto switches, power controls, brightness
  and color-temperature sliders. A cog opens the full settings window.
  The flyout anchors to the tray icon, stays within the monitor work area,
  and dismisses on Escape or loss of activation.
- Separate Automation, Apps, Cameras, Lights, and General settings. App suggestions
  prioritize saved selections, active clients, installed packages declaring
  webcam capability, and known communications apps such as Teams and Signal.
  Search or show all running processes for other apps. Suggestions are not
  proof of permission, and never enable an app automatically.
- Explicit shared-read-only local preview, with camera selection and physical
  light-state readback. The preview selector and automation camera filter are
  independent. Preview is under Cameras and stops on leaving that page,
  closing the settings window, or exit.
- Explicit LAN-interface selection, mDNS `_elg._tcp.local` discovery, read-only
  connection diagnostics and manual IPv4 configuration for port 9123.
  Discovery joins the standard IPv4 multicast group on UDP 5353 using the
  selected interface; it does not depend on devices honoring unicast-response
  requests to an ephemeral port. Results and errors appear beside discovery.
- Classic HTTP Elgato Key Light / Key Light Air control. Brightness and color
  temperature are preserved. TLS-advertising models are visibly unsupported.
- Light requests run independently with bounded concurrency and timeouts.
  Healthy states are checked at most once per second; failures back off
  from two to thirty seconds. Release or endpoint changes bypass the previous
  retry delay. Separate readbacks keep one offline device from hiding another.
  Failed reachability triggers read-only rediscovery at most every thirty
  seconds on the explicitly selected interface. Stable discovered identities
  update DHCP endpoints without losing original-state ownership.
- Camera monitoring, optional app details/icons, reachability checks and
  lighting control have separate workers. A stalled shell thumbnail or one
  malformed light response cannot permanently stop automation. Ownership
  writes remain serialized and journaled before physical changes.
- Previous-state restoration, persistent crash-recovery intent, external-change
  ownership relinquishment, pause, manual-on and manual-off/release.
  Disabling automation or removing every app selection releases owned changes
  even while the sensor is unavailable.
- Manual power/brightness/temperature edits serialize with automation and
  preserve fields you did not change. The light enters a per-light manual
  override, so automatic activation will not fight your edit. **Resume auto**
  explicitly returns control; completing an automatic activation also ends
  the override. Manual edits are not rolled back when a call ends. Failed or
  lost acknowledgments retain recovery intent, and displayed confirmed edits
  require matching device readback. Temperature controls use Kelvin, converted
  to the classic firmware's 143–344 mired range.
- An optional experimental fixed-function LocalService ETW helper and local
  authenticated IPC remain available for diagnostics and installed-service CI.
  **The UI uses normal-user sensor reports, not this legacy helper.**

## Run a published build

Windows 11 x64 (build 22621+) is the supported spike target. ARM64 and x86
builds are not provided yet. GitHub Actions retains an **unsigned development
ZIP**, symbols ZIP and SHA-256 manifest. The manual release workflow creates a
**draft prerelease**; it does not automatically publish unsigned software as
a stable release.

1. Install the Microsoft-signed **Windows App Runtime 2.x**, version **2.5.1 or
   later**, x64. The framework identity is `Microsoft.WindowsAppRuntime.2`.
   See [Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).
   WinApp SDK 1.x is not sufficient. This repository does not download or bundle
   the runtime into the application ZIP.
2. Extract the entire ZIP into a stable directory and run `CameraLightTray.exe`
   as yourself, **not as administrator**. The tray flyout shows the current
   camera/automation state. Open the cog for full settings; detailed monitor
   health is under Cameras. Do not install/repair the
   legacy helper to fix a sensor-report or light-networking problem.
3. In **Apps**, select apps such as Windows Camera, Teams or Signal.
   In **Cameras**, choose All cameras or select specific trigger cameras.
   No selected **apps** means no automatic activation, not any app.
   In **Lights**, choose your physical LAN interface, discover lights, test
   connections (read only), and select the lights to automate.
4. In **Automation** (or the tray flyout), explicitly enable automation. Open a selected
   app's camera: the selected lights should turn on after debounce and restore
   their previous state after capture stops plus leave grace.

For a visible verification, expand **Cameras / Local camera preview**, choose the same camera,
and press **Start preview**. Preview alone does not grant automation permission:
select this exact `CameraLightTray.exe` under Apps if you want its preview to
trigger lights. Alternatively, leave it unselected and watch another selected
app's activation/release while the preview stays live. Desktop app identities
include their full path, so moving a portable build requires reselecting it.
Camera access must be allowed in Windows privacy settings. A failed or
unavailable camera reports an explicit preview error; it never starts recording.

If monitoring or networking fails temporarily, leave the app running: the
status explains the failure and recovery is automatic. No reinstall, helper
repair or elevation is required. A missing/unreported camera remains unknown;
select supported trigger cameras explicitly if another virtual or unavailable
camera cannot provide an activity baseline. Manually entered IP addresses
are fixed endpoints; use mDNS discovery for automatic DHCP endpoint updates.

Click the tray icon for quick light controls; the cog opens full settings.
To launch directly into settings when the app is not already running, use
`CameraLightTray.exe --settings`.
Unknown/unreachable power is never depicted as off, and stale readings disable
physical controls. Newly discovered lights remain opted out until selected.
Closing the settings window hides it; right-click the tray icon to pause/resume or exit.
**Start at login** writes only the current user's Run registration. Normal
login launches the UI hidden and never invokes setup or UAC. Leave the app in
its stable location when enabling login startup. Disable that preference
before moving/removing the UI.

### Optional legacy ETW helper: setup, repair and removal

**Skip this section for normal use.** The helper is not needed by, or connected
to, the normal-user UI sensor source. Its setup action is deliberately isolated
under **Rules / helper** and is never invoked by launching the app.

The ZIP contains `setup-monitor.ps1` and `monitor\CameraLightTray.Monitor.exe`.
Setup copies **only that fixed helper executable** into
`%ProgramFiles%\CameraLightTray\Monitor`, protects executable code against
ordinary-user writes, and registers `CameraLightTrayMonitor` as an automatic
**LocalService** Windows service. It never uses LocalSystem or adds the user
to Performance Log Users. The UI is not elevated and never runs from the
service. Setup does not enable physical light automation.

For a normal UI update, exit the app and replace/extract its portable files;
no service repair is necessary. To update the optional helper, use the new
ZIP's explicit legacy setup/repair action to stop/replace/start it. There is
no automatic updater.
To remove the helper, explicitly run the following in an elevated PowerShell
from the extracted ZIP:

```powershell
.\setup-monitor.ps1 -Remove
```

This removes the named service and its fixed executable, not your preferences.
The directories can then be removed manually. Cancelled or failed setup is
reported rather than silently treated as success. Provider-specific ETW
permissions are checked by the actual `StartTrace`/`EnableTraceEx2` calls: if
LocalService is denied, the helper reports an unhealthy snapshot. This spike
does **not** broaden provider ACLs or silently fall back to an elevated UI.

The installed-service integration check validates the helper's authenticated
snapshots; a service setup success is not proof of ETW/IPC health. The legacy
pipe client distinguishes a four-second heartbeat timeout from setup failure.
Process-session queries use the direct Windows API rather than refreshing the
entire system process table once per process.
Executable version resources and shell thumbnails are resolved in an optional
UI worker, never in the process-identity heartbeat.

### Why a ZIP rather than MSIX for now?

MSIX is not an MSI-style arbitrary custom-action installer. It **does** support
declarative machine services through `desktop6:Service` under an application's
`windows.service` extension, including `StartAccount="localService"`, with the
restricted `packagedServices` capability. Installing an MSIX containing a
service requires administrative privileges. Store eligibility/restricted
capability approval, package signing, service upgrade/removal and distribution
need separate treatment.

For this spike the ZIP keeps normal UI development and launching non-elevated
while making machine setup an explicit, inspectable action. It also avoids
requiring a machine-wide installer for the normal-user sensor API. A future
signed MSIX can declare the shared framework dependency and, if retained, the
optional service; no arbitrary elevated custom action is needed or implied.
No Store submission workflow is provided.

References:
[MSIX services](https://learn.microsoft.com/windows/msix/packaging-tool/convert-an-installer-with-services),
[desktop6:Service](https://learn.microsoft.com/uwp/schemas/appxpackage/uapmanifestschema/element-desktop6-service),
[StartTrace permissions](https://learn.microsoft.com/windows/win32/api/evntrace/nf-evntrace-starttracew).

## Development and isolated worktrees

Use the SDK in `global.json` (currently a .NET 11 prerelease), Visual Studio
C++ x64 build tools, and Windows SDK 10.0.26100.0 or later for Native AOT.
The shared Windows App Runtime prerequisite is needed to launch the UI,
not for the pure-core tests.

```powershell
.\tools\verify.ps1 -NativeTests
dotnet run --project src\CameraLightTray.App -c Release -- --isolated --data-dir .\artifacts\dev-data
dotnet run --project src\CameraLightTray.App -c Release -- --isolated --demo --data-dir .\artifacts\demo-data
.\tools\package.ps1 -Version 0.1.5
.\tools\smoke.ps1 -Executable .\artifacts\package-0.1.5\CameraLightTray.exe
```

On a Windows 11 desktop, the native test executable also supports a real,
normal-user metadata-only recovery check:

```powershell
.\artifacts\native-tests\CameraLightTray.Tests.exe --sensor-recovery-check .\artifacts\sensor-recovery-result.txt
Get-Content .\artifacts\sensor-recovery-result.txt
```

It injects a transient process-owner lookup error after native monitor start,
then requires automatic disposal/recreation and two fresh healthy sensor and
own-process identity snapshots. It does not open a camera, change a light,
install a helper, or request elevation. Windows Server is not a supported
platform for the sensor API, so this check is separate from hosted service CI.

`--isolated` requires a separate data directory, disables setup and login
registration, and starts automation off even if that directory previously
enabled it. It uses normal-user sensor metadata; use `--demo` for
fully synthetic camera/light behavior with **no** camera access, ETW, service connection,
network discovery or physical light writes. Demo and smoke modes also require
an explicit data directory. Use a different directory per worktree; a file
lock prevents concurrent instances from sharing one directory.

The unattended smoke run opens a non-activating native Reactor window, invokes
real native controls against synthetic camera/lights, checks activation and
restoration, exercises the Studio flyout, manual sliders/power and settings cog,
exports synthetic client-area screenshots, and exits. Demo flyouts remain visible
when deactivated to support background inspection; normal flyouts dismiss.
It never installs the helper, changes startup or
contacts hardware. `tools\get-runtime.ps1` only inspects the resolved runtime
package; it does not install it. CI registers that Microsoft-signed shared
framework **only on its disposable hosted runner** before the UI smoke.

Build and publish sequentially (their intermediate directories are shared).
Packaging refuses an existing version output directory rather than mixing old
and new payloads. Remove only the named generated directory before rebuilding
the same package version. Do not build from or modify a reference checkout.

Native light artwork is original SVG, not downloaded product photography.
The classic Key Light uses a wide panel, ball mount and desk clamp; the original
Key Light Air uses a square panel and weighted base. Unknown product metadata
uses neutral generic artwork. Saved product names survive custom display names.
The checked-in `.ico` files provide multiple sizes for caption, taskbar and
light/dark system-tray themes. `node tools\generate-light-art.mjs` regenerates
the on/off/unknown variants from the original SVG source shapes.

## Camera source and honest uncertainty

The active UI source is
[`MFCreateSensorActivityMonitor`](https://learn.microsoft.com/windows/win32/api/mfidl/nf-mfidl-mfcreatesensoractivitymonitor)
and `IMFSensorActivitiesReportCallback`. Windows supplies current sensor reports,
device symbolic links, streaming flags, client PIDs and report times. Each
successful per-camera report replaces that camera's previous state; a partial
notification does not clear other cameras. State is not reconstructed
from guessed start/stop events or stale registry values. Process identities
are scoped to the current Windows user/session and checked against creation
time to reject PID reuse. Already-streaming clients can be identified at
startup. Native callbacks update state immediately; periodic process/device
refreshes and UI snapshots do not require continuous camera-frame events.

Local verification covered a Logitech Brio with Windows Camera and the app's
shared preview, and two classic Elgato lights on a routed LAN with source-bound
HTTP. It does not certify every driver, virtual camera, direct-driver capture
path or app combination. Only capture activity represented by Windows' sensor
monitor is supported; test the actual combination before relying on automation.

The **optional legacy helper**, not the active UI source, enables only provider
`Microsoft-Windows-MediaFoundation-MFCaptureEngine`
(`B8197C10-845F-40CA-82AB-9341E98CFC2B`), with an ETW event-ID filter for
completed preview start/stop **207/209** and record-mode start/stop **211/213**.
It decodes `CaptureEnginePointer` and `hrStatus` through TDH and accepts only
zero-success HRESULTs. State is keyed by process ID **and creation time**,
engine pointer and mode. An event called StartRecord indicates capture API
usage, **not proof that video was saved**. Initialization completion is not
camera stopping. FrameServer per-frame events are not enabled.

The registry webcam ConsentStore is **not a live sensor** and is not read as
one. Stale registry values never become an inactive state.

Legacy ETW limitations:

- Cameras already active when a legacy client connects may not be reconstructed from
  these events. A process predating the observation epoch stays unknown even
  after a matched pair; restart that app to establish a supported-path
  baseline. A process created after the observation boundary has a known
  baseline **only for this supported capture-engine source**. Each IPC
  connection has its own tracker; reconnecting creates a new observation
  boundary. Available bounded lifecycle history is replayed, but is not a
  complete startup inventory. Restart monitored apps after reconnecting.
- Unsupported camera APIs, proxy/broker attribution and inaccessible process
  identities are not solved. Inaccessible identities make that client's
  snapshot unhealthy and discard existing baselines rather than infer absence.
  Unrelated inaccessible system/other-user processes do not poison this user's
  catalog health. Successful capture events from unresolved process owners
  in this client's session do block automated release until ownership is
  resolved or the process exits; they are never silently discarded as inactive.
  Protected capture processes may therefore prevent legacy ETW automation.
  The helper must be evaluated under LocalService on each target Windows
  build; running the UI smoke does not validate provider access.
- Duplicate starts, unmatched stops, schema errors, event loss, helper
  disconnection, stale heartbeats and resume are not translated into camera-off.
  After event loss/resume, restart/repair the helper and restart monitored apps
  to re-establish baselines. This conservative spike can leave lights on until
  explicit pause/manual release or app/helper restart.
- Process death is confirmed by refresh of the process catalog; PID reuse does
  not inherit an older process's engines. Legacy tracking cannot replace a
  complete sensor inventory.

Shared safeguards:

- The normal-user sensor begins unknown until a valid report arrives. Errors,
  unavailable selected cameras, unattributable streaming clients, PID/time
  mismatches, resume and stale/missing snapshots hold demand rather than
  treating uncertainty as camera-off. Positively identified other-user/session
  clients are excluded. Inaccessible unrelated idle processes do not poison
  the source; an unresolved streaming client relevant to this session does.
- The app holds its last automation demand on unhealthy/missing sensor data.
- A fresh healthy snapshot with no matching running process can establish
  absence. Any selected active app requests light-on; any selected unknown app
  blocks automatic release. Changing selections is a normal-user preference.
- Pause, disabling automation, explicit manual-off/release, and exit are user
  overrides that attempt to restore **only owned** previous states even when
  sensor state is unknown.
- Light ownership is best-effort, not a device-side transaction: the API has no
  compare-and-swap. We re-read before restoring and relinquish ownership after
  any differing on/brightness/temperature value. An external edit to precisely
  the same values cannot be detected. Recovery intent is saved before writes;
  failed restoration remains available for a later attempt.
- No VPN settings/routes are changed. Requests disable proxies and redirects
  and source-bind to a dynamically re-resolved address on the selected physical
  LAN interface. Routed LAN subnets (including lights advertised through a
  local mDNS reflector) are supported without requiring the light to share
  the PC's subnet. No request silently switches to the VPN/default interface.
  Interface disappearance is an explicit error. IPv6 and TLS Elgato transports
  are not implemented.

## Security boundary and local data

The helper has no light-networking code, preferences, generic command execution,
client-selected provider GUIDs, or client-supplied paths. Its fixed local
named pipe rejects network logons, identifies the client by Windows
impersonation, and obtains the client's real session ID. Snapshots are filtered
to that SID and interactive session. Process-owner SIDs come from Windows WTS
enumeration; image paths and creation times are queried under the authenticated
client's impersonation, not by opening user-process tokens as LocalService.
No client supplies process paths or owner SIDs. Only a bounded queue of
lifecycle metadata crosses from the ETW callback into per-client trackers;
overflow becomes unknown state. The legacy pipe client verifies the LocalService
owner before accepting data; ordinary users cannot assign that owner to a
spoofed pipe. Same-user processes are the same trust boundary as this user's
preferences; administrators can replace the installed helper.

Per-user preferences and the light recovery journal live under
`%LOCALAPPDATA%\CameraLightTray` (or `--data-dir`). Runtime app identities, light
addresses and preferences are local data: do not upload them, raw traces,
camera usage history or hardware-specific diagnostics to the public repository.
Tests use only synthetic identities and documentation-only IP ranges.

## Repository and automation

`src\CameraLightTray.Core` contains testable sensor state, automation, storage,
source-bound HTTP and mDNS. `src\CameraLightTray.Monitor` contains the Windows
service, ETW and process/pipe boundary. `src\CameraLightTray.App` is the Reactor
tray UI, normal-user orchestration and explicit setup entry point.

PR/main verification builds the solution, runs the executable managed and
Native AOT synthetic test harness, publishes both Native AOT executables,
checks the uninstalled helper entry point and runs the isolated native UI
smoke. The synthetic harness does not install a service or contact hardware.
A separate integration check **only on disposable hosted Windows runners**
uses the published setup script to install/start the LocalService helper,
requires successful ETW startup and two fresh authenticated healthy snapshots
containing a temporary standard user's executable identity, checks installed
helper write protection, and removes the service/executable/account in
guaranteed cleanup. It does not activate a camera, write to lights, change
routes or widen provider ACLs. The draft-release workflow requires a successful
Verify result for its exact main commit, rejects existing tags, and never
clobbers published release assets. Production signing, target-machine
compatibility and a monitored real-camera/light acceptance run are remaining
release gates, not claims made by synthetic tests.
