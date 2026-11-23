---
name: better-ui
description: Polishes surfaces and icons in OutSystems screens with exact values for border radius, optical alignment, shadows, image outlines and icon states.
---

# UI polish

This skill holds the visual polish for surfaces and icons in OutSystems 11 UI, with the exact value of each detail. It applies once the interaction works; a polish finding never outranks a broken interaction.

## Exact values, optional polish

The values below are exact: an outline at `0.1` alpha is not one at `0.15`. The optical nudges and the 24 px padding cutoff are starting points, judged by eye. Keep the app's theme, blocks and density. A deliberate, consistent choice (no shadows anywhere) is a preference, not a finding. The same detail done two ways in the app is a finding.

Golden rule 1 applies: no gradients, glow, glass effects, cards inside cards, an icon on every label, or a shadow and large radius on everything.

Text rendering belongs to `better-typography`. Hit areas, keyboard and reduced motion belong to `better-accessibility`. Grouping and spacing belong to `better-layout`. Color tokens and contrast belong to `better-colors`. Every animation belongs to `better-motion`.

## Use the theme's surface tokens

Radius: `--border-radius-soft`, `--border-radius-rounded`, `--border-radius-circle`. Shadow: `--shadow-xs` to `--shadow-xl`. A surface uses these, never a new value. Read them with `oml describe <module.oml> <Theme> --css`.

## Outer radius equals inner radius plus padding

A surface inside another with an even inset (a `Card` holding an image) takes this outer radius: inner radius plus padding plus border width. An 8 px inner radius in 8 px of padding needs a 16 px outer radius. Past 24 px of padding, or with uneven padding, treat them as separate surfaces and keep each one's token.

## Align by eye where geometry looks off

- A Button with text and a trailing Icon: 2 px less padding on the icon side (`padding-inline-start: 16px; padding-inline-end: 14px;`).
- A play triangle: shift it 2 px toward its point (`transform: translateX(2px)`; play icons never mirror).
- A star, arrow or caret that looks off center: fix the SVG's `viewBox`; when you cannot, nudge its wrapper by 1 px.

## Shadows for depth, borders for structure

Where a border exists only to lift a surface, use a shadow token instead. Keep borders on separators, table cells, selected states and inputs (an input border needs 3:1, `better-accessibility`). High-contrast mode removes every `box-shadow`, so keep `border: 1px solid transparent` under a shadow ring; the transparent border also keeps the box size.

When the design asks for a hairline ring instead of a border, the light values are exact:

```css
.my-card {
  border: 1px solid transparent;
  box-shadow:
    0px 0px 0px 1px rgba(0, 0, 0, 0.06),
    0px 1px 2px -1px rgba(0, 0, 0, 0.06),
    0px 2px 4px 0px rgba(0, 0, 0, 0.04);
}
```

In the dark theme a single ring replaces it: `0 0 0 1px rgba(255, 255, 255, 0.08)` (hover `0.13`).

## Outline content images

Give photos and screenshots a 1 px outline inset by 1 px, in pure black or white at 10%: `outline: 1px solid rgba(0, 0, 0, 0.1); outline-offset: -1px;`, and `rgba(255, 255, 255, 0.1)` in the dark theme. Never a tinted gray or the brand color: it shows as a colored fringe. Skip logos and transparent artwork. Use `outline`, not `border`, so the image keeps its size.

## Hover only where a mouse hovers

Put hover-only styles inside `@media (hover: hover)`. On a touch screen `:hover` sticks after a tap and looks selected. Where a control draws its own pressed state, add `-webkit-tap-highlight-color: transparent` so the gray tap flash does not double it.

## Keep scroll inside overlays

A scrolling Popup, `Sidebar`, `BottomSheet` or `ScrollableArea` gets `overscroll-behavior: contain`, so scrolling past its end never scrolls the screen behind.

## One icon set, one look per state

Use the Icon widget's set (Font Awesome in OutSystems UI; check the names your module offers) and only that set on a screen. Size an icon next to text at `1em` to `1.25em` of the text size, so the pair scales together. Icons take color from `color` (`currentColor`), so hover, selected and disabled states come from CSS color and opacity, never from separate images. Some icons have an outline twin (Font Awesome 4 names end in `-o`, such as `heart-o`). Then the outline is the default and the filled icon marks the active state.

A custom icon is an SVG image (spec key `images`; optimize it with `oml image <icon.svg> --to svg`). An HTML Element cannot be `svg`; to color a custom icon with `currentColor`, use it as a CSS `mask-image` on an element with `background-color: currentColor`. For line icons, the rendered stroke follows the text weight next to it: 1.5 px at 400, 2 px at 500 to 600, 2.5 px at 700.

Right to left (only with `ui.rtl` on): mirror icons whose meaning follows reading direction (back and forward arrows, chevrons, send) with `scale: -1 1`. Never mirror logos, check marks, clocks or media playback.

## Before you finish

| Pattern | Fix |
| --- | --- |
| A padded box and the box inside it with the same radius | Outer radius = inner + padding |
| A radius or shadow value that is not a theme token | The theme token |
| `box-shadow: 0 0 0 1px` ring with no border | `border: 1px solid transparent` |
| A gray or brand-colored outline on photos | Black or white at 10% |
| A fixed `color` or `fill` inside a custom SVG icon | `currentColor` through `mask-image` |
| `:hover` rules outside `@media (hover: hover)` | Wrap them |
| A scrolling Popup or Sidebar with no `overscroll-behavior` | `overscroll-behavior: contain` |
| Two icon sets on one toolbar | One set |
| Shadow and large radius on every box, a card in a card | Remove them (golden rule 1) |

## Reporting

**Verification.** Without a published page: read every state the widget defines (hover, pressed, selected, disabled, loading, empty) from the CSS and the screen. With one: `oml browser look <url> <Widget> --state hover` (and the other states), in each theme. Report each check you could not run as `Not verified`.

**Standalone severity.** `HIGH` breaks an interaction, such as a hover state stuck on touch. `MEDIUM` is a behavior defect short of that (scroll chaining) or a visible inconsistency in surfaces or icons. `LOW` is isolated polish. One row per root cause with every place: Severity, Location, Before, After, Why. End with `Block` when a `HIGH` remains, else `Approve`. With nothing to report: "No actionable UI-polish findings", then the verification.
