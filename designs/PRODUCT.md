# Camera Light Tray

<!-- impeccable:product-schema 1 -->

## Platform

web

This value scopes this standalone review artifact, not the shipped application.
The product is a native Windows 11 x64 C# app using Microsoft UI Reactor,
.NET 11 Native AOT, and the shared Windows App SDK. The browser prototype is
not a proposed replacement framework.

## Users and purpose

Windows users with Elgato lights want lighting to follow camera use without
manually opening a control application for every meeting. Daily interaction
belongs in a small tray flyout; app, camera, network and timing configuration
belongs in the full settings window.

## Confirmed product truth

- Automation is opt-in. Only selected apps, selected trigger cameras and
  selected lights participate.
- No selected apps means no automatic activation, not every app.
- No specific trigger-camera selection currently means all cameras.
- Selected app activity is a union: the lights stay on while any selected
  app uses a monitored camera.
- Camera uncertainty must not be interpreted as camera-off.
- Restore only changes owned by this application. Preserve pre-existing
  light states and respect external edits.
- Monitoring runs as the normal user. The legacy helper is not a prerequisite.
- Preview is explicitly started, stays local, and does not record.
- Networking binds to the selected LAN interface; do not change VPN routes.

## Design brief

Confirmed by the user's November 5 request: take inspiration from Elgato
Control Center's compact tray controls; use recognizable light art with
distinct on/off states; show both actual power and automation configuration;
offer easy controls and a settings cog; reduce repeated caveat copy; clarify
setup and app selection; explore alternatives before committing a design.
Original SVG illustrations are acceptable. Do not copy Elgato's logo or
redistribute product photos without permission.

## Open decisions

- Flyout layout and density; the prototype provides three alternatives.
- Direct power, brightness and temperature controls are proposed UI behavior,
  not shipped functionality.
- An explicit "Any app" mode is a possible future feature, not today's implicit
  meaning of an empty selection. It is deliberately absent from the prototype.
- Proposed manual edits suspend automatic control of that light until the
  current activation ends or the user explicitly resumes it. This requires
  implementation and acceptance tests before release.
- App branding remains the descriptive placeholder Camera Light Tray.

## Evidence and boundaries

The supplied Control Center screenshot is a design reference, not a asset
license. Native automation has been exercised with a Brio and two classic
lights. Prototype devices, apps and states are synthetic; no machine addresses,
IDs, usage history, camera images or personal preferences are copied into it.
The native app remains running and unchanged during design review.
