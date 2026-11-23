# Palette, tokens and contrast

Reference for `better-colors`: role tokens, building a ramp, contrast thresholds and the dark theme. Everything goes in the theme style sheet.

## Role tokens

OutSystems UI gives the primitives (`--color-primary`, `--color-neutral-0` to `--color-neutral-10`, the status colors). Add a semantic tier only for roles the app uses, each pointing at a primitive:

```css
:root {
  --color-text-secondary: var(--color-neutral-7);
  --color-border-subtle: var(--color-neutral-3);
  --color-bg-surface: var(--color-neutral-0);
}
```

A complete set covers these roles:

| Group | Roles |
| --- | --- |
| Surfaces | page background, surface, raised (menus), sunken (inputs), overlay |
| Text | primary, secondary, disabled, inverse, on-accent |
| Borders | subtle, default, strong, focus ring, separator |
| Accent | subtle background, border, solid, solid hover, text |
| Status | per status shown: subtle background, border, solid, text |

Separator and border are two roles even when they share a value today.

Grammar, one shape always: neutral roles lead with the property, `--color-{bg|text|border}-{variant}-{state}`. Accent and status roles lead with the role, `--color-{accent|danger|...}-{subtle|border|solid|text}-{state}`. One word per concept: `text` (never `fg`), `bg` (never `background`), `border` (never `stroke`). In OutSystems UI `--color-primary` is the brand color, so new brand roles use `accent` and `primary` keeps that one meaning.

| Bad name | Problem | Better |
| --- | --- | --- |
| `--color-blue-button` | Names a look; wrong when the brand changes | `--color-accent-solid` |
| `--color-sidebar-gray` | Named after its first use | `--color-bg-surface` |
| `--color-light-gray` | Wrong in the dark theme | `--color-neutral-3` |
| `--color-text-2` | A number says nothing | `--color-text-secondary` |

## Building a ramp

Only when the app needs a color the theme lacks. A correct ramp:

- Lightness (OKLCH `L`) changes steadily; never use HSL lightness.
- Hue stays the same end to end.
- Chroma peaks mid-ramp and falls off at both ends.
- Light steps sit closer: about 0.04 to 0.05 apart at the light end, 0.07 to 0.10 mid-ramp.
- No two neighbors closer than 0.03; both ends stop short of pure black and white.

Compute it with a color library (`culori`, `colorjs.io` or `chroma.js`) in a script outside the module, interpolating in `oklab`, then write the values in the theme's notation. A brand color for buttons sits on the solid-fill step; when white text on it fails 4.5:1, it is still the brand color, but buttons use a darker step. Never darken the brand quietly.

Step roles (11-step ramp, light theme):

| Role | Step |
| --- | --- |
| Page and subtle background | 50 |
| Component background | 100 |
| Hover, selected, subtle border | 200 |
| Border | 300 |
| Solid fill | 600 |
| Solid fill hover, low-contrast text | 700 |
| High-contrast text | 900 |

A border at step 300 on white does not reach 3:1; an input border or focus ring needs a darker step. Status ramps agree with the brand ramp step for step: same lightness per step, chroma at the same share of each hue's own maximum. Yellow and cyan peak much lower than red and blue.

## Contrast

Thresholds a measured pair is reported against (WCAG 2):

| Content | AA | AAA |
| --- | --- | --- |
| Text | 4.5:1 | 7:1 |
| Large text: 24 px, or 18.67 px bold | 3:1 | 4.5:1 |
| UI components and meaningful graphics | 3:1 | none |

Disabled controls and logos are exempt. Where the project names no other standard, also give the APCA value: body text Lc 75 (preferred Lc 90), other text Lc 60, large text Lc 45, solid icons and components Lc 30. Lc is signed: compare the absolute value.

Fix a failing pair only when asked:

- Change lightness, keep hue. Move the foreground away from the background, then measure again.
- A mid-lightness background (OKLCH `L` near 0.75) caps contrast: even black text reaches only about Lc 60. Change the background instead.
- Pushing lightness can leave the screen's gamut: lower chroma to stay renderable.
- Black or white text: measure both. At OKLCH `L` 0.65 neutral, black gives about 6.49:1 and white 3.23:1.

Check every pair in each theme, and text over images, gradients or translucent headers at their worst region.

## Dark theme

Swap the roles first, then tune:

```css
[data-theme="dark"] .my-block {
  --color-bg-surface: var(--color-neutral-10);
  --color-text-primary: var(--color-neutral-0);
}
```

Use the app's own switch (`[data-theme="dark"]`, another class, or `prefers-color-scheme`) and only that one. Then:

- Lower the brand color's chroma a step: it glows on near-black.
- Give dark surfaces more separation than light ones.
- Measure every pair again; a pair that passes in light can fail in dark.

When users can raise contrast (`prefers-contrast: more`), give each theme its own stronger value, at least 0.15 of OKLCH `L` further from its background.
