---
name: build-design
description: Builds OutSystems UI from a Figma file or a design image so it matches the design, using the app's theme tokens, OutSystems UI patterns and blocks.
---

# Build design

This skill turns a design into an OutSystems screen or block that matches it. It reads the design at its source, maps it onto what the app already has, previews it in the running page, builds it with `oml apply` and compares the result with the design before calling it done.

The design decides. Values come from the design file, never from taste. A change the design does not show is a deviation, and a deviation is the user's call.

It owns no domain rules. Where the design breaks one, build it as designed and report the conflict to its owner. Contrast is `better-accessibility` and `better-colors`; focus and semantics `better-accessibility`; type `better-typography`; spacing and structure `better-layout`. Copy is `better-writing`, surfaces and icons `better-ui`, motion `better-motion`.

## 1. Read the design at its source

A Figma link means the Figma MCP server. Search the available and deferred tools for `figma` before you say it is missing; a server that lists only an authenticate tool is connected but signed out, so ask the user to sign in. With no Figma tool at all, ask the user to connect one; do not build from guesses meanwhile.

From Figma: the file key follows `/design/` or `/file/` in the URL, the node is `node-id` (`1-2` in the URL is `1:2` for the tools). With no `node-id`, list the frames (`get_metadata`) and ask which ones to build. For each frame, one `get_design_context` call with its screenshot; `get_variable_defs` for the variables it uses. Its code is a reference (React and Tailwind by default): translate it, never paste it. Trust variable names over raw values, auto layout gaps and padding over absolute positions, and Code Connect mappings over name matches. Download assets; never leave a Figma URL in the module.

From a screenshot: there are no values to read. Sizes are ratios to the capture: state the scale you assumed and treat every number as an estimate. Ask for the Figma link when one exists.

Frames of the same thing at different widths are one piece at several widths. Done when you have a screenshot of every frame in scope and its spacing, sizes, radii, colors, type and components.

## 2. Map the design onto the app

Read the theme (`oml describe <module.oml> <Theme> --css`), the app's blocks and the OutSystems UI patterns in use (`oml inspect <module.oml> --own --only blocks`, `oml doc osui-patterns`). Then map every value:

| In the design | In the module |
| --- | --- |
| A variable or style | The theme token with the same name or value (`--space-*`, `--color-*`, `--border-radius-*`, `--font-size-*`, `--shadow-*`) |
| A raw value equal to a token | That token |
| A raw value with no token | The nearest token, listed as rounded |
| A component instance | The app's block of that name, else the OutSystems UI pattern that matches (`Card`, `Tag`, `Columns3`, `BlankSlate`) |
| A component with no equivalent | Containers and CSS in place, listed as a deviation; ask whether a block should exist |
| A one-off group of layers | Containers and CSS in place, not a new block |
| An icon | The Icon widget's set; a custom SVG as an image |

Never add a token, a block or an arbitrary value just to hit a number. Ask when rounding would show: more than 2 px on spacing or size, or a color landing on another token.

Wire the data the design needs to what exists: aggregates, data actions, block inputs. A part with no backend yet renders from locals with sample values, named in the report as unwired.

## 3. Preview, then build only what the design shows

Golden rule 1: before the build, inject the planned CSS and HTML into the DEV page of the screen (or the closest one) with `oml browser try <url> --css plan.css --html <Widget>=plan.html --screenshot plan.png`. Show the screenshot next to the design and build after a yes.

Build the frames, the widths they define and the states the file draws (hover, empty, error). A state the file does not draw is not yours to design: keep the pattern's default and list it as missing. Leave everything around the piece as it was: no copy edits, icon swaps or clean-ups next to it (rule 8). One spec, `oml apply <source.oml> <spec.json> <new-staged.oml>`, `oml review`, then ask the user to install and publish to DEV.

## 4. Compare with the design

After the publish, screenshot each frame width (`oml browser page <url> --screenshot <frame>.png` in a session started with that `--width`, or `oml browser sweep <url> --out <folder>`) and walk it element by element against the design screenshot. "The gap is 12 px, the design is 16 px" is a finding; "feels tight" is not. `oml browser look <url> <Widget>` gives the exact box and computed values. Before the publish, say the comparison is pending.

Fix every mismatch you caused in a new staged file and compare again. Done when every remaining difference is a rounding or a question.

## 5. Report and stop

| Element | Design | Built | Status |
| --- | --- | --- | --- |
| Card padding | 24 px | `var(--space-m)` | Matches |
| Title size | 15 px | `var(--font-size-base)`, 16 px | Rounded to token |
| Status badge | Filled pill | OutSystems UI `Tag` | Matches |
| Avatar group | Overlapping circles | Containers and CSS | Deviates: no block exists, asked |

Then, one line each and only when not empty: **Unwired** (what shows sample data until the backend exists), **Missing states** (what the design does not draw), **Domain conflicts** (where the design breaks a rule, with the owner). Close with where the build runs and the widths you compared.

## Before you finish

| You notice | Fix |
| --- | --- |
| "Figma isn't connected" before any tool search | Search the tools for `figma` first |
| A value in the spec that no Figma answer holds | Read it from the design context |
| A hex color or px value in the CSS where a token exists | The token, or ask when rounding shows |
| A new block for a frame that is not a component | Containers in place |
| A loading or error state the design never drew | Keep the default; list it as missing |
| Changes to widgets no frame touches | Undo them |
| A contrast or focus change the design does not show | Build the design; report the conflict |
| A match claimed with no screenshot of the build | Compare after the publish, or say it is pending |
| Sample data not in the report | List it under **Unwired** |
