---
name: better-layout
description: Sets grouping, alignment, reading order, responsive structure and room for long or translated text in OutSystems screens and blocks, so a layout holds up when it is resized, translated or mirrored.
---

# Layout

This skill sets grouping, alignment, spacing and responsive structure in OutSystems screens and blocks. It then tests that structure at every supported width, with long text and, when the app needs it, mirrored for right-to-left.

Use the theme's spacing tokens: `--space-xs`, `--space-s`, `--space-base`, `--space-m`, `--space-l`, `--space-xl`, `--space-xxl` (4, 8, 16, 24, 32, 40, 48 px in OutSystems UI). The px values below are the steps to pick from that scale. A finding needs a failure you can show: a clip, an overlap, a broken mirror or a misread group. A value that differs from these starting points is not one.

Hit areas and focus belong to `better-accessibility`. Radius and shadows belong to `better-ui`, motion to `better-motion`. Line length and truncation belong to `better-typography`, the words to `better-writing`.

## Group with space, not lines

Space groups first, a background shape (an OutSystems UI `Card` or `Section`) second, separator lines last. The gap between groups is at least twice the gap inside one: `--space-s` inside a group means `--space-base` or more between groups. Keep separators for dense data such as tables and long settings lists, as a low-contrast hairline.

## Keep controls distinct from content

Every control has a fill, a border, an underline or a fixed place such as a toolbar. A Link styled like the text next to it does not read as a control. The reverse holds too: a `Tag` shaped like the buttons beside it collects clicks.

## Align to shared edges

Pick a few edges and put everything on them. One spacing step per level of nesting, `--space-base` by default. Numbers in table columns align to the end, text to the start.

## Order by importance

The most important content sits near the top and the start edge. In a row, the name leads; metadata and actions trail. Visual order matches widget order in the screen: never reorder with `order`, `row-reverse` or grid placement, because keyboard and screen-reader order follow the widgets.

## One primary action per view

Each view has one primary action. Screen-level buttons go in the layout's `Actions` placeholder. With more than 3 secondary actions, put the rest in a menu. Prefer a short screen that links to detail over one long screen. How color marks the primary action is `better-colors`.

## Hint at hidden content

Content off screen or collapsed has a visible cue. The next card peeks 16 to 32 px past the scroll edge, or a control names what is hidden: "Show 12 more orders", not "More". A truncated value the user needs has a way to the full text: a detail screen, an expand control or an OutSystems UI `Tooltip` that also opens on keyboard focus. A `title` attribute alone does not count.

## Room between controls

Without a density rule in the app, start with these gaps. Bordered or filled controls: 8 to 16 px (`--space-s` to `--space-base`). Borderless text or icon buttons: 24 px (`--space-m`) between their visible glyphs, counting their padding. Hit areas never overlap.

## Inset buttons from the edges

Full-width buttons stay inside the layout margins, about 16 px from the edge on a phone. OutSystems UI makes buttons full width on phones; the class `no-responsive` stops that for one button.

## Content bleeds, controls float

Backgrounds and images may reach the screen edges. Text and controls stay inside the layout margins. Content scrolls under a fixed header (`HasFixedHeader`): set `scroll-padding-block-start` to the header height so a focused element or anchor target never lands under it.

## Responsive with OutSystems UI

- Screen structure: OutSystems UI `Columns2` to `Columns6`, `ColumnsSmallLeft` and the like, with `TabletBehavior` and `PhoneBehavior` (`Entities.BreakColumns.All`, `.First`, `.Middle`, `.Last`, `.None`). Card grids: `Gallery` with `RowItemsDesktop`, `RowItemsTablet`, `RowItemsPhone`.
- Per device CSS: OutSystems UI sets `.desktop`, `.tablet` or `.phone` on `body` (phone below 768 px, tablet 768 to 1024 px). Write `.phone .my-card { ... }`. Different content per device: `DisplayOnDevice`; in an expression, `IsPhone()`, `IsTablet()`, `IsDesktop()`.
- No breakpoints of your own for the screen. A block that sits both in a narrow side panel and in a wide area may use a container query on its own root (`container-type: inline-size` on a box with a definite width).
- Check every width the profile supports with `oml browser sweep <url>`; read `overflowBy`, `hiddenHere` and `onlyHere`.

## Plan for growth

Text gets longer: user data, long names, translations (`ui.translations` in the profile). A one-word button label grows most. Size text boxes with `max-width` and `min-height`, never a fixed width or height, and let rows wrap (`flex-wrap: wrap`). A flex child or grid track with long text gets `min-width: 0` or `minmax(0, 1fr)`, or it pushes the page wider than the screen.

Never put a critical action where zoom, a small screen or scrolling cuts it off. A Popup taller than the screen gets `max-height: 100dvh`, a scrolling body and its buttons outside the scroll area. A canvas, a background effect or an absolute child needs a parent with an explicit `height`, `min-height` or `aspect-ratio`.

## Right to left

Only when the profile has `ui.rtl` on. Write start and end, not left and right: `margin-inline-start`, `padding-inline-end`, `inset-inline-start`, `text-align: start`, `border-inline-end`, `float: inline-start`. Flex rows and grid columns mirror on their own; `translateX`, `background-position` and anything placed with `left` or `right` do not and need an override for the RTL page. Progress (stars, steps, progress bars) fills from the start edge, which is the right in RTL. Find how the app marks RTL (`dir` on the page or a body class) with `oml browser look` before you write the override.

## Before you finish

| Pattern | Fix |
| --- | --- |
| A grid track `1fr` or a flex child holding long text, wider than the screen | `minmax(0, 1fr)` on the track, `min-width: 0` on the child |
| `width: 100vw` | `width: 100%`; `100vw` includes the scrollbar |
| `height: 100vh` on a full-height phone panel | `100dvh` |
| A fixed `height` on a box with text | `min-height`, or `max-height` with `overflow-y: auto` |
| `@media (max-width: ...)` in a screen or block sheet | OutSystems UI Columns, `.phone` and `.tablet` classes |
| A fixed header and no `scroll-padding-block-start` | Set it to the header height |
| Three spacing values on one column edge | One theme spacing step |
| `margin-left` on a mirrored page | `margin-inline-start` |

## Reporting

**Verification.** Without a published page: Columns behaviors, device classes and fixed sizes in the CSS, widget order against visual order. With one: `oml browser sweep <url>` at every supported width, 320 px and 200% zoom, and the RTL page when `ui.rtl` is on. Report each check you could not run as `Not verified`.

**Standalone severity.** `HIGH` hides content or an action at a supported width: a clip or overlap at 320 px or 200% zoom, truncated text with no way to the full value, or hidden content with no cue. `MEDIUM` harms hierarchy, reading order or adaptability. `LOW` is isolated alignment or spacing. One row per root cause with every place: Severity, Location, Before, After, Why. End with `Block` when a `HIGH` remains, else `Approve`. With nothing to report: "No actionable layout findings", then the verification.
