---
name: "Camera Light Tray — Native Studio"
description: "Implemented, user-approved equipment-first Windows tray controls and native settings."
colors:
  studio-surface: "#24282E"
  studio-raised: "#30353C"
  studio-line: "#434A53"
  studio-text: "#F0F3F5"
  studio-muted: "#B2BCC6"
  studio-accent: "#A9CADF"
  studio-accent-ink: "#183345"
  studio-warning: "#EDC381"
  settings-surface: "#FBFCFD"
  settings-navigation: "#F0F3F6"
  settings-selected: "#DFE9F1"
  settings-selection-summary: "#EDF5FA"
  settings-line: "#D8E0E6"
typography:
  headline: { fontSize: "26", fontWeight: 600 }
  title: { fontSize: "15", fontWeight: 600 }
  device-name: { fontSize: "14", fontWeight: 600 }
  body: { fontSize: "13" }
  label: { fontSize: "11" }
rounded:
  power: "7"
  selection-summary: "6"
spacing:
  inline: "8"
  content-stack: "12"
components:
  power-on: { backgroundColor: "{colors.studio-accent}", textColor: "{colors.studio-accent-ink}", rounded: "{rounded.power}", padding: "6", width: "36", height: "36" }
  power-off: { backgroundColor: "{colors.studio-raised}", textColor: "{colors.studio-muted}", rounded: "{rounded.power}", padding: "6", width: "36", height: "36" }
---

# Design System: Camera Light Tray — Native Studio

## Overview

**Creative North Star: "Studio — equipment-first quick controls"**

Studio is the implemented native Windows design, approved November 6, 2026:
> love the first one, 'Studio'. The icons for the lights need refinement to better match the real products, but they're a good start. Go ahead and implement the Studio design, commit and push all changes when complete.

The flyout puts equipment, physical power and visible adjustments first; the cog opens native settings. Both windows use one saved System/Light/Dark preference under General. System follows Windows app colors live. The platform is Windows 11 x64, .NET 11, Microsoft UI Reactor / WinUI 3, Native AOT and the shared Windows App SDK. Normal monitoring needs no elevation or helper.
**Key Characteristics:** recognizable original equipment SVGs; separate power and automation; honest uncertainty; progressively disclosed setup and diagnostics.
Authority: `src\CameraLightTray.App\UI\Studio.cs`, `UI\Dashboard.cs`, `Program.cs` WindowSpecs and `Native\FlyoutPlacement.cs`; context: `README.md` and `designs\PRODUCT.md`.
`artifacts\studio-smoke-data\studio-on.png` and `studio-settings.png` are synthetic native client-area renders, not hardware readings or window-chrome captures. Apps selection targets now follow the corrected 44-DIP source; screenshot recapture follows.
Root `DESIGN.md` and `design-system.json` describe native Studio. The three comparison directions under `designs` are archived exploration, not competing native identities.

## Colors

- **Primary:** muted Studio blue marks known-on power buttons, enabled flyout automation switches and text actions; accent ink contrasts the on button.
- **Neutral:** charcoal surface, raised power controls, fine dividers, light primary text and muted labels establish the flyout hierarchy.
- **Settings:** shared light/dark semantic surfaces, a tinted selected page and app-selection summary; ordinary controls inherit the selected native theme. Light uses the existing pale surfaces with dark text; Dark uses the Studio charcoal palette.
- **States:** warning text accompanies unknown camera/light state and errors. Lit product panels use illustrative warm-to-cool white points and intensity derived only from confirmed physical temperature/brightness readbacks. Off panels remain unlit; stale/TLS observations remain unknown. This is not a calibrated photometric simulation.
- Slider track gradients and thumb brushes are recorded in the JSON extension. High contrast substitutes system foreground/background and removes custom toggle/slider brushes.

## Typography

Text inherits native WinUI fonts; there is no custom web font stack. Glyphs explicitly use **Segoe Fluent Icons** (18-DIP default).
Frontmatter dimension strings are native **DIP**, including font sizes, spacing and radii; they are not browser CSS units.
Settings headings are semibold 26; flyout title 15; device names 14; body 13; secondary states, slider labels/values and footer 11. The empty-light heading is semibold 18.
Native TextBlocks wrap and receive automation names; values retain `%` and `K`, with last-known or unavailable qualifiers when needed.

## Layout

- Flyout: 400 DIP wide, borderless, non-resizable, absent from taskbar/switcher. Measured native chrome plus the equipment content's desired height determines height, independent of the initial window size; the equipment scroller is bounded at 500 DIP and the overall window is capped to the monitor work area. No fixed blank band precedes the footer.
- Header, automation summary, scrolling equipment region and footer are separate grid rows. The approved two-product composition keeps both brightness and temperature sliders visible.
- Header padding is 18 × 12; equipment rows 16 × 12 with a 6-DIP stack gap. Row headings use 68 / flexible / auto columns; art is 58 × 65.25 DIP.
- Settings request 940 × 780 DIP, minimum 620 × 500; a fixed 180-DIP navigation rail accompanies scrolling content padded 32 × 28. Rail padding is 12 × 24.
- All row content and actions align on their vertical centers. Horizontal scrolling is disabled and the vertical gutter is reserved, so long technical content cannot widen unrelated rows.
- Settings switches use content-sized widths and right-aligned state labels, sharing the trailing edge of the other row controls in both On and Off states. Their native template height is unchanged.
- The navigation element tree is memoized by page/theme only. Native hover state must survive background updates; repeated monitoring or readback notifications must not rebuild its button content.
- A page change creates a fresh page/scroller identity and starts at vertical offset zero, including when returning to a page. Live updates and theme changes preserve the current page's scroll position.
- Apps and trigger-camera selections have empty visual labels, descriptive automation names and 44 × 44-DIP targets, including minimum size; Apps uses a trailing action column.
- Placement anchors to the tray and clamps position and size to the monitor work area, accounting for taskbar edges. This is native window placement, not breakpoint layout.

## Elevation & Depth

Client content uses flat tonal fields and 1-DIP dividers, not authored CSS shadows. The flyout requests native rounded window corners; settings request Mica at the window level beneath an explicit light client background.
Ordinary control hover, focus and pressed treatments remain native unless source explicitly overrides a brush. No custom motion timing or shadow scale is established here.

## Shapes

Known-on/off power buttons are 36-DIP rounded squares with 7-DIP corners; the settings cog is 34 × 34. These are distinct from the 44-DIP empty-label selection targets.
Original Key Light artwork retains a wide panel, ball mount and desk clamp; Air retains a square panel, telescopic post and flat weighted base.
Each model has on/off/unknown art; unknown has a question badge. Unrecognized products use generic UnknownLight art, never an inferred model. High contrast uses a native glyph instead of equipment SVGs.

## Components

- **Power and Auto:** physical power text/art/button state is independent of automation selection. The per-light Auto switch uses its intrinsic WinUI size and built-in content label; TLS models cannot be selected for control. Do not reduce the template's natural height to compact the layout.
- **Interaction stability:** memoized settings/power button subtrees preserve native hover across background renders. The power symbol is an original SVG with a padded view box and round strokes, not a font glyph with a cropped baseline; high contrast uses a smaller native system-colored glyph.
- **Button templates:** power buttons reserve the native 1-DIP border plus 6-DIP padding, leaving a 22-DIP content slot for the 20-DIP icon. Normal/PointerOver/Pressed/Disabled use per-control native theme brushes; on-state hover retains accent and icon contrast. Keep owned brush instances stable when their colors change, and never mutate shared system brushes. Verification checks actual template-slot containment and painted hover colors, not just outer bounds or visual-state names.
- **Adjustments:** brightness 0–100, step 1; temperature 2,900–7,000 K, step 100. Native sliders keep their intrinsic template height, thumb tooltips disabled, labels/values always visible. Hover, thumbs and tracks must fit their controls; complete normal light rows must fit above the footer. Stale/TLS controls are disabled; readings say last known, Not read or Unavailable.
- **Pending edits:** slider intent coalesces for 250ms and applies on a background task without an applying label, control replacement or layout shift. Superseded request cancellation is not a connectivity failure. Keyboard focus stays on the current slider; native SmallChange is 1% / 100 K.
- **Manual ownership:** power/slider edits serialize with automation and put that light in manual override without changing untouched fields. Automation does not fight the edit; **Resume auto** returns control, and activation completion ends the override. Manual edits are not rolled back at call end.
- **Readback:** physical readbacks are authoritative; optimistic artwork never asserts confirmed power. Unknown camera activity holds owned lights rather than becoming idle/off. Unreachable lights disclose reconnection while other lights remain usable.
- **Triggers:** no selected apps means no automatic activation. Suggestions never grant permission. **All cameras** is explicit; preview camera selection is independent. Newly discovered lights remain opted out.
- **Settings:** Automation, Apps, Cameras, Lights, General and Diagnostics share the native navigation surface. Search, counts and selections lead. Camera preview and manual entry forms are ordinary visible sections; longer technical details are on Diagnostics.
- **Sections and feedback:** no expanding regions change the page's measure or surrounding control sizes. Errors use text or native InfoBars.
- **Keyboard and lifecycle:** native buttons, switches, checkboxes, sliders and fields retain keyboard and accessibility paths. Escape dismisses the flyout; deactivation dismisses normal runs. Closing settings hides it and stops preview; the cog hides the flyout before showing settings.
- **Synthetic testing:** Demo remains visibly labelled; native settings show a non-closable Synthetic demo InfoBar with no hardware access. Demo/smoke intentionally suppress deactivation dismissal for testing.

## Do's and Don'ts

- **Do** preserve the approved equipment-first Studio composition and native control/keyboard/high-contrast paths.
- **Do** keep known power, unknown power, automation eligibility and manual override separate; trust physical readbacks.
- **Do** retain visible slider labels/units, explicit selections, stable content constraints and contextual diagnostics.
- **Don't** infer off from an unreachable light, idle from camera uncertainty, a product from unknown identity, or every app from an empty selection.
- **Don't** treat synthetic captures as physical-device evidence or archived browser mockup dimensions as native specifications.
- **Don't** introduce another UI framework, speculative token scales or copied product branding/photography; retain the original refined equipment SVGs.
