---
name: better-typography
description: Sets and reviews how text renders in OutSystems themes and screens, from the type scale and line height to fonts, wrapping, truncation and punctuation.
---

# Typography

This skill sets and reviews how text renders: the type scale, spacing, font loading, wrapping and punctuation. It writes every fix as theme or block CSS, using the theme's font tokens.

The words belong to `better-writing`, heading structure (which tag) to `better-accessibility` and spatial right-to-left layout to `better-layout`. `better-colors` measures contrast; `better-accessibility` decides whether it passes.

## Measured, not preferred

Some values are exact, and a miss is a finding:

- Unitless line height; weight 400 or heavier below 18 px.
- A measure of 60 to 75 characters.
- 16 px inputs on phones; tabular figures on changing numbers.

Letter spacing, pairing and scale ratios are heuristics: report them only where they break the app's own scale. Never propose a new typeface unless the task asks for a type change.

## Use the theme's scale

OutSystems UI themes define `--font-size-h1` to `--font-size-h6`, `--font-size-base`, `--font-size-s` and `--font-size-xs`. Use them; do not add sizes. Pair each size with its line height and weight once, in the theme, so a role is one decision. Emphasis inside a role is one weight step up (400 to 600), not a new size. Read the theme first with `oml describe <module.oml> <Theme> --css`; an older Traditional Web theme may have fixed sizes and no variables: then reuse its sizes.

## Fewer fonts, sizes and weights

Rarely more than two font families in an app. Weight and size carry hierarchy; each extra one dilutes it. Below 18 px use weight 400 or heavier. Weights 100 to 300 only at 28 px and up.

## Load the faces you use

A browser fakes a bold or italic the font does not ship, and it looks wrong. Load every weight and style the design uses. Serve `.woff2`: convert a `.ttf` or `.otf` with `oml font woff2 <font.ttf>`. Serve it the way the app's theme already serves fonts; check the `@font-face` URL in the theme CSS.

## Headings descend with level

`h1` to `h6` (HTML Elements with that tag) map to descending steps, so a lower heading never outweighs its parent. A heading is never smaller than body text, except a deliberate overline. Style the level with CSS; never pick the tag by its size.

## Line height by role

Display text `1.1`, headings `1.2` to `1.3`, body `1.5` to `1.6`. Always unitless, so it scales with the size; `24px` does not. Text that wraps to three lines or more needs at least `1.4`, even in a tight card.

## Letter spacing by size

Headings at 24 px and up: `-0.01em` to `-0.02em`. Uppercase labels at 14 px or smaller: `0.05em`. Body: `0`. Always in `em`. Never `font-kerning: none`.

## Cap the measure

Long text stays at 60 to 75 characters per line: about `max-width: 60ch` at 16 px. Short form labels and tables are exempt.

## Wrap deliberately

- `text-wrap: balance` on headings and short descriptions, never on paragraphs.
- `text-wrap: pretty` on paragraphs, to keep one word off the last line.
- `overflow-wrap: break-word` where a long word, URL or id could escape its box.
- `white-space: nowrap` on short labels and `Tag` texts where a break looks broken.
- Interface text is `text-align: start`; never `justify`.

## Tabular figures on changing numbers

`font-variant-numeric: tabular-nums` on timers, counters, prices and numeric table columns, so digits keep one width. Figures in running text stay proportional.

## Truncate with a way back

One line: `overflow: hidden; text-overflow: ellipsis; white-space: nowrap;`. Several lines: `display: -webkit-box; -webkit-box-orient: vertical; -webkit-line-clamp: 3; overflow: hidden;`. A truncated value needs a way to the full text, which `better-layout` owns.

## Natural case, real punctuation

Store text in natural case and set case with `text-transform`, so a redesign does not mean rewriting texts. Rendered text uses real characters. The ellipsis character (U+2026) replaces three dots, and an en dash (U+2013) replaces the hyphen in a range such as `2010-2020`. Use curly quotes, and a no-break space (U+00A0) in `10 km` so it never splits. In an OutSystems expression these characters go inside the text literal. Code and ids keep straight quotes.

## Underlines

`text-underline-position: from-font` and `text-decoration-thickness: from-font`, or set `text-underline-offset` and a thickness by hand. A dotted underline marks a word with more information (an abbreviation). Only the underline's color animates reliably.

## Inputs at 16 px on phones

Safari on iPhone zooms the page when an input's text is under 16 px. Size inputs at 16 px on phones: `.phone .my-form input { font-size: 1rem; }`. A design that must show smaller text there needs a transform workaround: ask before you build it.

## Size floors

Long body text starts at 16 px (`1rem`). Interface text may be smaller: inputs and menus 14 px, captions 13 px, rarely under 12 px. Set sizes in `rem` so the reader's browser size still applies.

## Font smoothing once

`-webkit-font-smoothing: antialiased` and `-moz-osx-font-smoothing: grayscale` belong once on the root rule of the theme, never in a block.

## Language and direction

In a multilingual app, check that the page's `lang` matches the language shown; a wrong `lang` makes screen readers mispronounce and breaks hyphenation. Wrap a mixed-direction value (a name or number inside right-to-left text) in an HTML Element `bdi`. Never reorder digits by hand.

## Keep text selectable

Never `user-select: none` on a container or across the app; only on a drag surface.

## Before you finish

| Pattern | Fix |
| --- | --- |
| A `font-weight` the loaded font files do not have | Load that face, or use a weight it ships |
| `line-height` in `px` or `rem` | A unitless value |
| A tight line height on a description that wraps to 3 lines | `1.4` or more |
| `letter-spacing` in `px` | The same value in `em` |
| Body `font-size` in `px`, or a size outside the theme tokens | The theme's `--font-size-*` |
| Weight 100 to 300 on text under 18 px | 400 or heavier |
| `text-wrap: balance` on a paragraph | `text-wrap: pretty` |
| `text-align: justify` in app UI | `text-align: start` |
| `-webkit-line-clamp` without `display: -webkit-box` | Add it and `-webkit-box-orient: vertical` |
| A timer, price or number column without `tabular-nums` | `font-variant-numeric: tabular-nums` |
| `...` or a hyphen range in a shown text | The ellipsis character or an en dash |
| An input under 16 px on phones | `.phone` rule at `1rem` |
| `user-select: none` on a container | Remove it |

## Reporting

**Verification.** Without a published page: declared size and weight per heading level, line height units, measure, and truncation against realistic text lengths. With one: `oml browser look <url> <Widget>` gives the computed font values and the rule that wins; `oml browser sweep <url>` shows wrapping and truncation at each width. Report each check you could not run as `Not verified`.

**Standalone severity.** `HIGH` makes text unreadable, truncates it with no way back, or clips it at 320 px or 200% zoom. `MEDIUM` misses an exact value under **Measured, not preferred**, or breaks the type scale or heading hierarchy. `LOW` is isolated. One row per root cause with every place: Severity, Location, Before, After, Why. End with `Block` when a `HIGH` remains, else `Approve`. With nothing to report: "No actionable typography findings", then the verification.
