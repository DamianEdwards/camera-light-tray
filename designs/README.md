# Tray-first design exploration

**Studio was selected for native implementation on November 6.** Compact and
Gallery remain archived alternatives. The native application now implements
Studio's tray-first interaction and paired settings flow, with refined
product art and real manual controls; this browser artifact remains a simulation.

Open `index.html` in a browser, or use the Design review canvas in this session.
The prototype works from a local file with no server, build, fonts download or
dependency installation. It makes **no camera, device or preference requests**.
All content is synthetic.

## Compare the directions

| Direction | Daily interaction | Tradeoff |
|---|---|---|
| Studio | Product-led rows, both lights' controls always visible | Tallest flyout; closest to Control Center's direct adjustments |
| Compact | Small rows with one light's adjustments expanded | Smallest footprint, but adjusting another light takes a click |
| Gallery | Larger portraits, side-by-side lights, a shared adjustment area | Clearest equipment recognition; requires explicit selected-light focus |

The cog opens a paired full settings window. Try Apps, Cameras, Lights,
Automation and General; search, clear selections, or switch the example state
in **Try a design**. Examples include idle, active, paused, no apps, unknown
camera state, an unreachable light, discovery, no lights and an unsupported
TLS light.

The user supplied a Control Center screenshot as inspiration. The review does
not redistribute it. All equipment illustrations and app/tray icons are
original SVGs in `assets`. The same light geometry is embedded in the page to
avoid browsers blocking external SVG symbol references on local-file URLs.

## Behavior shown, versus behavior shipped

No selected apps means **no automatic activation**, not every app. Both the
active app and its camera must match the selected triggers. This is preserved,
not a proposed expansion to every app. An explicit Any app mode remains an
open product decision and is not included.

Direct power, brightness and temperature controls are proposed functionality.
In the example they make that light manual for the camera session, with an
explicit **Resume auto** action. The actual app still uses its existing
ownership/restoration behavior; these controls have not been wired to it.

Unknown power is not depicted as off or as a pressed power button. An offline
light shows a question badge and last-known adjustment values. Unknown camera
activity holds automatic states. Suggestions, helper diagnostics and privacy
details are progressively disclosed rather than repeated around the controls.

The compact prototype is not a native tray implementation. Production work
still needs tray anchoring and dismissal, keyboard navigation at native scale,
per-monitor DPI, high contrast, model metadata/art mapping, native assets,
localized text, manual-control semantics and physical-control acceptance.
It must retain C#, Reactor, Native AOT and the shared Windows App SDK.

## Review outcome

| Material correction | Result |
|---|---|
| Active fixture respects both app and camera selection | Resolved; unrelated selections no longer activate it |
| Unreachable power does not assert on or off | Resolved; neutral art, question badge, no pressed-state assertion |
| Unreachable adjustments appear disabled and last known | Resolved; disabled tracks, explicit last-known labels |

The initial finishing review used a separate design reviewer. Its three
corrections were then checked against regenerated screenshots and targeted
interaction assertions in the main session because this runtime does not
support continuation of a synchronous reviewer.

This folder remains a review artifact, not the application. The production
implementation lives in `src\CameraLightTray.App`; its working behavior and
limitations are documented in the repository README.
