---
name: better-colors
description: Builds and checks the colors of an OutSystems theme by mapping colors to OutSystems UI tokens, adding semantic tokens, measuring contrast and building dark themes.
---

# Colors

This skill builds and audits the colors of an OutSystems theme. It maps colors to roles and tokens, builds ramps when a new color is needed and measures the contrast of rendered pairs.

Never report a contrast value you did not measure, and never estimate a color you could compute. The notation (hex or `oklch()`) and a tinted neutral are project choices, not findings. A broken role mapping, a failing pair and a color outside the screen's gamut are findings. "Lightness" here means OKLCH `L`, from 0 to 1.

Whether a pair must pass belongs to `better-accessibility`. Surfaces, shadows and icon color belong to `better-ui`. Golden rule 1 applies: no new palette; use the app's theme.

## Use the theme's tokens

OutSystems UI themes define `--color-primary`, `--color-secondary`, `--color-neutral-0` to `--color-neutral-10`, `--color-error`, `--color-warning`, `--color-success` and `--color-info`. Screens and blocks use these, never a hex literal. Read the theme first with `oml describe <module.oml> <Theme> --css`. An older Traditional Web theme may have no CSS variables: then reuse the classes and values it has, and add variables only when the user asks for a theming change.

Keep the theme's notation. A hex theme stays hex.

## Add a role token, never borrow one

Never use a token for a job it was not made for because its value fits today. A border color used as text goes wrong when borders get lighter. When a role has no token, add a semantic token in the theme, named by its role and pointing at an existing value: `--color-text-secondary: var(--color-neutral-7);`. Components then use `var(--color-text-secondary)`. The role list and the naming grammar are in [palette.md](palette.md) (`oml doc better-colors/palette`). Keep the OutSystems UI names as they are: `--color-primary` is the brand color.

## A system is ramps, not colors

One neutral ramp, one brand ramp and only the status colors the app shows. A second brand hue earns its place only when two things must be told apart at a glance. A new ramp is built with a color library, never by eye: [palette.md](palette.md#building-a-ramp).

## One color, one meaning

A color has one purpose across the app; hues within 15 degrees of OKLCH hue count as the same color. Never let color carry meaning alone: add an icon or a word (`better-accessibility`). OutSystems UI `Tag` and `Badge` colors (`Entities.Color.Red` and so on) follow the same rule: red means danger everywhere. There is no `Entities.Color.Success` or `Error`; use `Green` and `Red`.

## Fill one action per view

When filled color marks the primary action, one action gets it and its peers stay neutral. Color goes on the background, not the label: a filled Button reads as primary; colored text on a neutral Button reads as a link. An existing hierarchy that marks emphasis another way stays.

## Measure the rendered pair, then report

Measure a foreground against the background it actually renders on (the nearest painted ancestor, often a `Card`, not the page). Get both colors from the published page with `oml browser look <url> <Widget>` (`computed` and `rules`), in each theme the profile lists (`--theme dark`). Compute the ratio with a script, never by eye.

When a pair fails, report the pair, its value and the threshold, and leave the colors alone: they are a design decision. When asked to fix it, change lightness and keep the hue, then measure again. Thresholds and the fix rules are in [palette.md](palette.md#contrast).

Some OutSystems UI palette colors fail as text on white: orange, yellow, lime, green and the light neutrals. Override the token or pick another color.

## Dark theme

Only when the profile lists `dark` in `ui.themes`. A dark theme is not the light one reversed: lower the brand color's chroma a step, separate the dark surfaces more, and measure every pair again. Use one switch for the whole theme: the app's class or attribute (`[data-theme="dark"]` scoped to your class) or `prefers-color-scheme`, never both. Recipe: [palette.md](palette.md#dark-theme).

## Gradients

Golden rule 1 forbids decorative gradients. When the design has one, write `linear-gradient(in oklab, ...)`, and keep text off it or measure the worst region.

## Before you finish

| Pattern | Fix |
| --- | --- |
| A hex or `rgb(` literal in a screen or block sheet where a token exists | The theme token |
| `var(--color-neutral-7)` as text in ten places | A role token such as `--color-text-secondary` |
| A token named by hue or place: `--color-blue-button`, `--color-sidebar-gray` | A role name: `--color-accent-solid`, `--color-bg-surface` |
| A border token used in `color:`, or a text token in `background:` | Add a token for that role |
| A status color within 15 degrees of `--color-primary` | Move it 15 degrees or more, or give the delete action a distinct look |
| Dark tokens equal to the light ramp in reverse | Lower chroma, widen the dark end, measure again |
| Colors switched by both `prefers-color-scheme` and a class | One switch |
| A contrast fix that changes hue and keeps lightness | Change lightness |
| Text on a color with alpha (`rgba(`, `color-mix(` with `transparent`) | Measure the rendered result, or use a solid token |
| White text on an orange, yellow or green fill | Measure it; it often fails 4.5:1 |

## Reporting

**Verification.** Without a published page: token values, both theme blocks present, and contrast computed from the declared pair. With one: the background actually rendered behind the text, with opacity and any image under it, in each theme. Report each check you could not run as `Not verified`.

**Standalone severity.** `HIGH` makes content unreadable or uses a semantic color against its meaning; text that fails its ratio, meaning carried by color alone and a misused semantic color are `HIGH` on sight. `MEDIUM` is a theme, token or gamut failure. `LOW` is isolated. One row per root cause with every place: Severity, Location, Before, After, Why. End with `Block` when a `HIGH` remains, else `Approve`. With nothing to report: "No actionable color findings", then the verification.
