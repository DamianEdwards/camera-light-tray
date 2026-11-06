---
name: "Camera Light Tray — Design exploration"
description: "Existing Control Center-inspired mockups; three provisional alternatives, no approved native identity."
colors:
  review-accent: "#245784"
  review-background: "#edf0f3"
  review-text: "#252c34"
  dark-surface: "#24282e"
  dark-raised: "#30353c"
  dark-hover: "#3b424b"
  dark-line: "#434a53"
  dark-text: "#f0f3f5"
  dark-muted: "#b2bcc6"
  dark-subtle: "#a0acb7"
  dark-accent: "#a9cadf"
  dark-accent-ink: "#183345"
  dark-warning: "#edc381"
  dark-good: "#c0d6bd"
  light-surface: "#fafbfc"
  light-raised: "#eef1f4"
  light-hover: "#e7edf2"
  light-line: "#d5dde4"
  light-text: "#28343e"
  light-muted: "#566570"
  light-subtle: "#667886"
  light-accent: "#29618b"
  light-accent-ink: "#fff"
  light-warning: "#8b5c15"
  light-good: "#376751"
  panel-off: "#5a646d"
  panel-off-edge: "#79848f"
  panel-unknown: "#7b838b"
  panel-unknown-edge: "#aab4bc"
  panel-lit-fallback: "#fff1cf"
  panel-lit-edge: "#fffdf3"
  settings-light-text: "#25313c"
  settings-light-muted: "#566570"
  settings-light-line: "#d8e0e6"
  settings-light-background: "#fbfcfd"
  settings-light-navigation: "#f0f3f6"
  settings-light-accent: "#255f8c"
  settings-dark-text: "#e8eef2"
  settings-dark-muted: "#b2bec7"
  settings-dark-line: "#424c56"
  settings-dark-background: "#282e35"
  settings-dark-navigation: "#22282e"
  settings-dark-accent: "#a5cae3"
typography:
  display:
    fontFamily: '"Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: "32px"
    fontWeight: 650
    lineHeight: 1.2
    letterSpacing: "-.6px"
  headline:
    fontFamily: '"Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: "26px"
    fontWeight: 650
    lineHeight: 1.4
    letterSpacing: "-.3px"
  title:
    fontFamily: '"Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: "15px"
    fontWeight: 600
    lineHeight: 1.5
  device-name:
    fontFamily: '"Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: "14px"
    fontWeight: 600
    lineHeight: 1.35
  body:
    fontFamily: '"Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: "13px"
    fontWeight: 400
    lineHeight: 1.6
  label:
    fontFamily: '"Segoe UI Variable", "Segoe UI", system-ui, sans-serif'
    fontSize: "11px"
    fontWeight: 400
rounded:
  settings-field: "4px"
  field: "5px"
  icon-button: "6px"
  power-button: "7px"
  gallery-device: "8px"
  settings-window: "10px"
  flyout: "12px"
spacing:
  inline: "8px"
  control-gap: "9px"
  gallery-gap: "12px"
  slider-gap: "14px"
  slider-top: "16px"
  settings-section: "28px"
components:
  power-dark-on:
    backgroundColor: "{colors.dark-accent}"
    textColor: "{colors.dark-accent-ink}"
    rounded: "{rounded.power-button}"
    padding: "8px"
    width: "36px"
    height: "36px"
  power-light-on:
    backgroundColor: "{colors.light-accent}"
    textColor: "{colors.light-accent-ink}"
    rounded: "{rounded.power-button}"
    padding: "8px"
    width: "30px"
    height: "30px"
  settings-search:
    backgroundColor: "transparent"
    rounded: "{rounded.field}"
    padding: "10px 12px"
    width: "100%"
  pause-dark:
    backgroundColor: "transparent"
    textColor: "{colors.dark-text}"
    rounded: "{rounded.field}"
    padding: "7px 9px"
---

# Design System: Camera Light Tray — Design exploration

> **ARCHIVED comparison record — status updated November 6, 2026.**
> The user approved Studio for native implementation, including refined original
> product illustrations; native Studio is implemented. Root `..\DESIGN.md` and
> `..\design-system.json` are authoritative for that implementation.
> Studio, Compact and Gallery below are archived comparison compositions.
> Historical “provisional”, “no direction selected” and “unapproved” statements
> describe the earlier review, not the current approval or native status.

## Overview

**Creative North Star: "Control Center's compact equipment grammar"**

This file records the built visual exploration in this standalone `designs`
prototype. It is **not authoritative for native implementation until the user
approves a direction**. Studio, Compact and Gallery are provisional alternatives
for the same request, not three identities to ship. No direction is selected.
The Studio “Suggested starting point” label is a prototype suggestion, not
approval; the default example layout is not a product decision.

The shared grammar is Windows UI typography, restrained charcoal or soft-grey
surfaces, recognizable original equipment SVGs and a clear separation between
actual power and automation configuration. Compact daily controls lead to a
paired settings window through a cog. The descriptive Camera Light Tray name
and camera-inside-light-panel icons remain exploratory.

Evidence is `PRODUCT.md`, `README.md`, the contract comment in `index.html`,
`review.css`, `review.js`, and the supplied `compare-desktop.png` and
`settings-apps.png` captures under `artifacts\design-review`. All screenshots,
devices, apps and activity are synthetic. This browser artifact performs no
camera access, light writes or preference requests. The native Windows app is
unchanged; its framework and release identity are outside this document's scope.

**Key Characteristics:**
- Three comparable compositions, with the final choice open.
- Equipment-led power depiction, separate from Auto/Manual state.
- Short operational labels; setup and diagnostics disclosed where needed.
- Shared Windows typography and restrained light/dark treatments.
- Proposed manual controls, not claims of shipped capability.

## Colors

The existing palette uses blue for actions, neutral surfaces for hierarchy,
warm-to-cool panels for illumination, and restrained status colors.
Frontmatter values describe this prototype only.

### Primary
- **Review accent:** links, view actions and “Try” controls outside the mockups.
- **Dark accent / dark accent ink:** pale blue pressed power buttons and enabled
  automation switches, with dark icon contrast.
- **Light accent / light accent ink:** blue pressed power buttons and enabled
  switches in Gallery, with white icon contrast.
- **Settings accents:** independent light/dark action colors for the settings
  navigation, line icons and text buttons; they are not substitutions for the
  flyout accents.

### Neutral
- **Review background / text:** the surrounding comparison workbench.
- **Dark surface, raised, hover and line:** the Studio/Compact vocabulary.
  Surface, hover and dividers carry most of the visible layering.
- **Light surface, raised, hover and line:** the Gallery vocabulary. Its
  automation strip uses a separate existing soft field (`#eef2f5`).
- **Text / muted / subtle:** primary names, secondary descriptions and quiet
  status dots, respectively, with separate light and dark values.
- **Settings background / navigation / line / text / muted:** the paired window
  has its own CSS variables in light and dark variants.

### Semantic states
- **Good:** the automation strip's active camera-use dot, not a power indicator.
- **Warning:** unknown camera state, missing trigger-app setup and device
  problems; pair the color with explicit text.
- **Panel off / edge:** neutral unlit equipment, regardless of Auto selection.
- **Panel unknown / edge:** neutral artwork with a question badge, not off art.
- **Panel lit / edge:** illuminated equipment. The CSS fallback is recorded
  above; `review.js` normally supplies a temperature-dependent fill by blending
  warm RGB `(255, 230, 182)` at 2,900 K to cool RGB `(226, 242, 255)` at 7,000 K,
  clamping the blend and rounding each channel. Brightness does not change the
  illustration in this mockup.

The brightness track runs from `#404a53` to `#e8edf0`; the temperature track
runs through `#ecc798`, `#f0e7d3` and `#a1cada`. These are adjustment cues, not
additional brand accents. Theme mappings and state details live in the sidecar.

## Typography

**Display and body font:** Segoe UI Variable, Segoe UI, system-ui, sans-serif.
The entire review inherits this Windows-oriented stack; controls inherit the
font and synthesis is disabled. There is no downloaded display face or separate
monospaced identity.

### Hierarchy
- **Display:** review introduction, using the frontmatter display role; becomes
  smaller (27px) below the narrow breakpoint.
- **Headline:** full-settings page titles; becomes smaller (24px) on narrow
  screens. Comparison direction headings are separately sized (23px).
- **Title:** flyout name; general section headings use 20px at weight 650.
- **Device name:** Studio names use the device-name role; Compact uses 13px.
- **Body:** operational settings and flyout text use 13px; paragraph line-height
  is 1.6. Settings descriptions are capped at 64ch.
- **Label:** secondary device state, adjustment labels and footer text use
  11px. Slider outputs use tabular numerals; units remain visible as `%` and `K`.

Font weight, spacing and case support scanning rather than decorative display.
Sidecar typography metadata records these roles without creating a second scale.

## Layout

The three compositions share a header, automation strip, equipment, adjustments
and a trigger-management footer, but arrange the equipment differently.

| Provisional alternative | Existing composition | Focus-view width | Equipment art |
|---|---|---|---|
| Studio · dark | Stacked product-led rows; both sets of adjustments always visible | 390px | 55 × 66px |
| Compact · dark | Smaller stacked rows; at most one adjustment set expanded via Adjust | 350px | 35 × 43px |
| Gallery · light | Two side-by-side portraits; selected light receives the shared adjustment area | 420px | 98 × 110px |

Studio rows use padding (18px 17px) and a three-part heading grid
(55px / flexible / 37px) with an 11px gap. Compact reduces row padding
(13px 15px), uses a 36px heading-art column and indents automation/adjustments
by 46px. Gallery uses two equal columns, a 12px gap, and a lightly tinted,
bordered selected equipment region. These dimensions record the implementation,
not approved native tray sizing.

The outer review has a maximum width (1480px), three comparison columns and a
26px gap. At 1180px, gaps and some type/art sizes contract. At 940px, comparison
and workbench layouts stack. At 600px, review padding becomes 16px and all
focus flyouts use `min(390px, 100%)`; Gallery art becomes 74 × 90px.

The paired settings window has a maximum width (1040px), a 46px titlebar, a
208px navigation rail, and body padding (32px 38px). The rail becomes 164px at
940px; at 600px it becomes a horizontally scrollable row above the content.
App selection uses separated rows, icons, secondary descriptions and trailing
checkboxes. Search and the selected count precede the list.

The taskbar, desktop stage and flyout pointer are review illustrations. Native
tray anchoring, dismissal, per-monitor DPI and platform keyboard behavior still
require implementation and acceptance; responsive browser behavior is not proof
of native readiness.

## Elevation & Depth

Depth is structural and restrained: outer flyouts/windows cast ambient shadows;
internal rows are mostly flat, divided by fine lines or tonal fields. Gallery's
selected region is tinted rather than lifted. No blur or material effect is
established by this CSS.

### Shadow Vocabulary
- **Flyout:** `0 10px 26px #23334426, 0 2px 4px #2333441a`.
- **Settings window:** `0 14px 30px #27405a15`.
- **Slider thumb:** `0 1px 3px #0004` in the WebKit thumb rule.
- **Toast:** `0 6px 18px #0002`.

The sidecar preserves exact shadow strings, focus-ring treatment, motion and
reduced-motion behavior.

## Shapes

Flyouts use gently rounded corners (12px), a one-pixel outline and a rotated
square pointer (12px) near the lower-right edge. The settings window has
rounded corners (10px) and clips its titlebar/navigation within the window.

Power buttons are small rounded squares (7px radius); settings/search fields
use 4–5px corners, icon buttons 6px and selected Gallery regions 8px.
The Auto control is a short pill (26 × 15px, 9px radius) with a circular knob
(11px). Status dots are circles (6px).

Equipment art preserves the wider Key Light panel and clamp, the Key Light Air's
squarer panel and circular base, and an existing generic round-light symbol.
Original line icons use rounded caps/joins. The app/tray icon shares a camera
lens inside a lighting panel; shown tray sizes are 16, 20 and 32px. This is
exploratory artwork, not approved native asset integration or a product logo.

## Components

### Power and equipment

An illuminated panel and pressed power button represent known on; an unlit
panel and unpressed button represent known off. Both also have text labels.
Automation selection does not determine the art's power appearance.

Unreachable and unsupported TLS lights use unknown-power artwork and a question
badge. Their power buttons are disabled and omit `aria-pressed`, rather than
asserting on or off. An unreachable light says “Unreachable · power unknown”;
TLS is explicitly unsupported. Other lights remain independently usable.

### Automation strip and per-light control

The strip summarizes camera/automation state, with a short secondary line and
Pause/Resume or a contextual setup/details action. Active uses Good; waiting
states use Warning; idle, ignored, off and paused use Subtle.

Per-light switches and labels distinguish “Auto with camera”, “Manual only”
and “Manual this session”. Power, brightness and temperature edits are
**proposed manual functionality**: in the simulation they place that light in
Manual for the session, with explicit “Resume auto”. Production semantics remain
unapproved and need physical-control acceptance; the mockup is not the native
ownership/restoration implementation.

Trigger eligibility remains opt-in: the active app and its camera must match
the selected triggers. **No selected apps means no automatic activation.**
Unknown camera activity is shown honestly and holds owned light states rather
than being relabeled idle. Auto configuration and camera uncertainty stay
separate from actual light power.

### Adjustments

Brightness is a range (0–100, step 1); temperature is a range
(2,900–7,000 K, step 100). End icons, visible labels, units and tabular outputs
support quick reading. Compact's Adjust button exposes at most one set;
Gallery's shared controls name the selected light.

Unreachable adjustments are disabled, their range rows dimmed (opacity .4),
and their readings marked “last known”. Unsupported TLS readings say
“Unavailable”. Disabled buttons use opacity .5. Stale values are not presented
as current measurements.

### Settings, navigation and disclosure

The cog opens the full settings window. The existing rail contains Automation,
Apps, Cameras, Lights and General; the current page has a tinted background and
semibold text. Hover uses a quieter translucent field.

App search, selected counts and checkboxes make participation explicit.
Suggestions, executable-path entry and troubleshooting use disclosure sections
instead of repeating caveats around every light. The camera page exposes an
explicit All cameras choice; preview is a separate action. Discovery and
empty-light states have contextual actions without implying automatic opt-in.

### Feedback, focus and motion

Keyboard focus uses a solid ring (3px, `#428cc7`) with a 3px offset on buttons,
links, inputs, selects and summaries. Feedback uses existing live status outputs
and a toast, not additional modal controls.

Button background/border and toast opacity transitions use 160ms ease-out;
Auto knob movement uses 150ms ease-out. Discovery uses a 1.2s linear spinner.
Reduced motion disables all animations and transitions, including pseudo-elements.
The prototype does not establish native high-contrast or DPI acceptance.

## Do's and Don'ts

### Do:
- **Do** retain all three compositions as provisional review alternatives.
- **Do** use Windows typography and the recorded variant values when describing
  this built prototype.
- **Do** keep known power, unknown power and automation state visibly distinct.
- **Do** label unreachable values last known and keep disabled controls visibly
  unavailable.
- **Do** preserve explicit app/camera/light eligibility and honest uncertainty.
- **Do** describe direct manual controls and native asset integration as open work.

### Don't:
- **Don't** treat Studio's suggestion or the default scenario as user approval.
- **Don't** apply this scoped document as the native app's authoritative identity
  before the user chooses and approves a direction.
- **Don't** infer off from unreachable power, idle from camera uncertainty, or
  “every app” from an empty trigger-app selection.
- **Don't** present synthetic screenshots as live device readings or shipped UI.
- **Don't** copy Elgato branding or redistribute reference product photography;
  this exploration uses original SVGs.
- **Don't** reopen style review from this record: the separate finishing
  corrections are recorded as resolved, and the supplied handoff reports all
  13 interaction/overflow/browser checks passed. No new review was performed here.
