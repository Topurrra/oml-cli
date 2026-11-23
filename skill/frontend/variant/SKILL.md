---
name: variant
description: Previews three different variants of one piece of OutSystems UI in the running DEV page, with a picker, and builds the one the user picks. User-invoked.
disable-model-invocation: true
---

# Variants

This skill takes one described piece of UI and builds three versions that differ on purpose. They are previews injected into the real DEV page with `oml browser try`, behind a picker, so the user flips between them and chooses. Nothing is saved until the user picks one; that one is built with `oml apply`. Run it only when the user asks for it.

It produces candidates and never ranks them. Reviewing UI is `interface-review` and `better-interface`, stress testing one block is `break` and working through a block's states is `state-machine`. This is golden rule 1 (preview first) with three options instead of one.

## Different answers, not different tints

Each variant answers the same brief differently, on one axis:

| Axis | Owner | What varies |
| --- | --- | --- |
| Structure | `better-layout` | Grouping, order, column count, what collapses |
| Density | `better-layout` | Spacing steps, how much fits |
| Emphasis | `better-colors` | Where filled color goes, what recedes |
| Type | `better-typography` | Scale steps, weight contrast, measure |
| Voice | `better-writing` | Labels, tone, how much copy |

Pick **one primary axis** and give each variant a different position on it. Other choices follow from it: a dense variant may need a smaller type step, and that is coherence, not a second axis.

## The floor every variant clears

Before a variant enters the picker, it clears `better-interface`'s escalation triggers:

- Every control has a name and a visible focus, and the keyboard reaches everything the mouse does.
- Motion respects reduced motion; no state change rides on motion alone.
- Nothing clips or overlaps at 320 px or 200% zoom.
- Text passes its contrast ratio; no meaning rides on color alone; no semantic color is used against its meaning.
- A destructive action has a confirmation, undo or distinct look.
- Truncated text has a way to the full value, and hidden content has a cue.
- Every error says how to recover.

A direction that only works by breaking the floor is dropped, and you say so.

Each variant also follows golden rule 1: the app's theme tokens, OutSystems UI patterns and the app's blocks, no new palette or generic decoration.

## 1. Scope one piece

One piece per run: the KPI card, not "the dashboard". When the request spans several, list them and ask. Restate the brief in one sentence: what it is, where it renders, what it must do.

## 2. Learn the ground

Variants must look like they could ship tomorrow. Read the theme's tokens (`oml describe <module.oml> <Theme> --css`) and the OutSystems UI patterns and blocks the app uses. Read the screen around the piece (`oml describe <module.oml> <Screen>`): its neighbors, its background, and the widths in the profile (`ui.targets`). A dense business tool limits how far the boldest variant may go.

## 3. Name the set before writing code

Three variants by default; five only when asked. Write down a name and an axis position for each. Names say the direction: `Quiet`, `Editorial`, `Dense`, never `Option A`. Done when no two share a position.

## 4. Build the previews into the real page

Ask for the DEV URL of the screen that will hold the piece (or the closest existing screen). Each variant is a CSS file and, when the structure changes, an HTML file for the widget that holds the piece. Use the real widget names and real content: app-shaped text, plausible names and the number of items the screen really carries. Lorem ipsum and three rows make every structure look good.

For each variant, one screenshot:

`oml browser try <url> --css quiet.css --html <Widget>=quiet.html --screenshot quiet.png`

`try` uses the session's window size. When the profile supports phones, take the phone screenshots in a session started with `oml browser start --width 390`.

Then the picker, so the user can flip live: ask the user to run `oml browser start --headed` (and log in), then inject all variants at once with the script in [picker.md](picker.md) (`oml doc variant/picker`): `oml browser try <url> --js picker.js`. A reload or a screen re-render removes it; nothing is saved.

## 5. Present the tradeoffs and stop

Look at every screenshot: each variant renders, nothing clips, and the output has no JavaScript `errors`. Then hand over the decision:

| Variant | Axis position | Right when | Costs |
| --- | --- | --- | --- |
| Quiet | Least visual weight | The screen is used all day | Least memorable |
| Editorial | Largest type, most space | The moment deserves weight | Uses more height |

Say where the picker runs, which keys flip it and which widths you judged at. Never mark a favorite. Asked directly, answer from how often the piece is seen and from the app's character, not from which one you liked building.

## 6. Build the chosen one

On a choice, build that variant properly: theme or block CSS and widgets in one spec, `oml apply` to a new staged file, `oml review`, then ask the user to install and publish. After the publish, compare the screen with the variant's screenshot (`oml browser page <url> --screenshot built.png`); the built screen must match the preview. Delete the other variants' files and the picker script.

Asked for another round instead, run **Name the set before writing code** again with new positions around the direction the user leaned toward.

## Before you finish

| Mistake | Fix |
| --- | --- |
| Variants differ only in accent color or a few words | Move one to another position on the axis, or cut it |
| Lorem ipsum, three rows, "Jane Doe" | Real text and the real item count |
| A new palette or decoration in a variant | The app's theme tokens and patterns |
| The picker styled in the app's theme | Keep it outside the design system |
| A variant built into the module before the user chose | Previews only until a choice |
