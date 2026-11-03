# Camera Light Tray

An initial Windows tray-app spike: turn selected Elgato lights on when **any
selected app uses its camera**, then restore the states this app changed.
Written in C# targeting **.NET 11**, with **Microsoft UI Reactor / WinUI 3**,
published as **Windows x64 Native AOT**. The Windows App SDK is a **shared
dependency**, not bundled with the app. No .NET runtime installation is needed
for the published executables.

This is not yet a production camera-activity detector. Read the limitations
below before using it unattended. Automation, login startup, new app selections
and newly discovered lights are all opt-in. The app captures no camera frames,
audio, or ETL files and sends no telemetry.

## What works in the spike

- Normal-user tray UI with friendly app names, per-app selections and camera
  status; saved choices survive process restarts. Packaged apps use package
  family plus executable identity, desktop apps use full executable paths.
- Aggregate capture-engine preview/record activity across engines, processes
  and selected apps. Brief activations are debounced (500ms default); leave
  grace defaults to 2500ms.
- Explicit LAN-interface selection, mDNS `_elg._tcp.local` discovery, read-only
  connection diagnostics and manual IPv4 configuration for port 9123.
- Classic HTTP Elgato Key Light / Key Light Air control. Brightness and color
  temperature are preserved. TLS-advertising models are visibly unsupported.
- Previous-state restoration, persistent crash-recovery intent, external-change
  ownership relinquishment, pause, manual-on and manual-off/release.
- A fixed-function LocalService helper and local authenticated IPC. App
  selections, light networking and preferences remain in the normal-user UI.

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
   as yourself, **not as administrator**. Before service setup the camera state
   is unknown; the UI and preferences work without elevation.
3. Use **Set up / repair monitor (administrator)** if you choose to install
   the helper. This is the only in-app elevation action. Inspect the unsigned
   spike and its setup script before approving elevation; no installer or
   permission warning is bypassed.
4. Restart camera apps that were running before the UI connected to the helper. Choose
   apps and lights, test light connections (read only), and explicitly enable
   automation. The helper's reported status must be healthy.

Closing the window hides it; right-click the tray icon to pause/resume or exit.
**Start at login** writes only the current user's Run registration. Normal
login launches the UI hidden and never invokes setup or UAC. Leave the app in
its stable location when enabling login startup. Disable that preference
before moving/removing the UI.

### Setup, repair, update and removal

The ZIP contains `setup-monitor.ps1` and `monitor\CameraLightTray.Monitor.exe`.
Setup copies **only that fixed helper executable** into
`%ProgramFiles%\CameraLightTray\Monitor`, protects executable code against
ordinary-user writes, and registers `CameraLightTrayMonitor` as an automatic
**LocalService** Windows service. It never uses LocalSystem or adds the user
to Performance Log Users. The UI is not elevated and never runs from the
service. Setup does not enable physical light automation.

For an update, extract the new ZIP, exit the UI, run its explicit setup/repair
action to stop/replace/start the helper, then reopen the UI. Restart monitored
apps to establish a new observation baseline. There is no automatic updater.
To remove the helper, explicitly run the following in an elevated PowerShell
from the extracted ZIP:

```powershell
.\setup-monitor.ps1 -Remove
```

This removes the named service and its fixed executable, not your preferences.
The directories can then be removed manually. Cancelled or failed setup is
reported rather than silently treated as success. Provider-specific ETW
permissions are checked by the actual `StartTrace`/`EnableTraceEx2` calls: if
LocalService is denied, the UI stays unknown and shows the error. This spike
does **not** broaden provider ACLs or silently fall back to an elevated UI.

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
claiming a zero-admin camera-monitor installation. A future signed MSIX can
declare the service and a shared framework dependency; no arbitrary elevated
custom action is needed or implied. No Store submission workflow is provided.

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
.\tools\package.ps1 -Version 0.1.0
.\tools\smoke.ps1 -Executable .\artifacts\package-0.1.0\CameraLightTray.exe
```

`--isolated` requires a separate data directory, disables setup and login
registration, and starts automation off even if that directory previously
enabled it. It may read an already-installed shared monitor; use `--demo` for
fully synthetic camera/light behavior with **no** ETW, service connection,
network discovery or physical light writes. Demo and smoke modes also require
an explicit data directory. Use a different directory per worktree; a file
lock prevents concurrent instances from sharing one directory.

The unattended smoke run opens a non-activating native Reactor window, invokes
real native controls against synthetic camera/lights, checks activation and
restoration, and exits. It never installs the helper, changes startup or
contacts hardware. `tools\get-runtime.ps1` only inspects the resolved runtime
package; it does not install it. CI registers that Microsoft-signed shared
framework **only on its disposable hosted runner** before the UI smoke.

Build and publish sequentially (their intermediate directories are shared).
Packaging refuses an existing version output directory rather than mixing old
and new payloads. Remove only the named generated directory before rebuilding
the same package version. Do not build from or modify a reference checkout.

## Camera source and honest uncertainty

The helper enables only provider
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

Important limitations and safeguards:

- Cameras already active when the UI connects to the helper may not be reconstructed from
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
  Protected capture processes may therefore prevent unattended automation.
  The helper must be evaluated under LocalService on each target Windows
  build; running the UI smoke does not validate provider access.
- Duplicate starts, unmatched stops, schema errors, event loss, helper
  disconnection, stale heartbeats and resume are not translated into camera-off.
  After event loss/resume, restart/repair the helper and restart monitored apps
  to re-establish baselines. This conservative spike can leave lights on until
  explicit pause/manual release or app/helper restart.
- Process death is confirmed by refresh of the process catalog; PID reuse does
  not inherit an older process's engines. The app holds its last automation
  demand on unhealthy/missing sensor data.
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
  LAN interface. Interface disappearance/subnet mismatch is an explicit error.
  IPv6 and TLS Elgato transports are not implemented.

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
overflow becomes unknown state. The UI verifies the pipe's LocalService
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
