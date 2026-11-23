---
name: break
description: Stress tests one OutSystems block or widget by rendering it under every scenario that can reach it, injected into the running DEV page, and reports what broke. User-invoked.
disable-model-invocation: true
---

# Break

This skill takes one block or widget and renders it under every scenario that can actually reach it, side by side on the running DEV page. That page is the report: every scenario in one column, with the breaks marked. Run it only when the user asks for it.

It observes; it does not judge. A finding is something that visibly broke, named by the domain skill that owns the fix. Reviewing against rules is `interface-review` and `better-interface`; exploring designs is `variant`; working through product states one at a time is `state-machine`. Where `variant` insists on the real context, this skill isolates on purpose: it checks whether the block defends itself when the content is worst-case.

The run is build, look once, report: minutes, not a session.

## 1. Scope one block

One block or widget per run: the order card, not "the orders screen". When the request spans several, list them and ask. Restate it in one sentence: what it shows, where it lives and where its data comes from (`oml describe <module.oml> <Block>`).

## 2. Pick the scenarios from the block

Stress only what varies in production. Read the block first: its inputs, its texts, its lists and its states. Keep an axis only when its cue matches:

| Axis | Cue | Scenarios | Owner of a break |
| --- | --- | --- | --- |
| Length | It shows text it does not write (user data, entity data, translations) | Empty; one word; typical; several sentences; one 60-character unbreakable string (a URL, `Donaudampfschiffahrtsgesellschaft`) | `better-typography`, `better-layout`, `better-writing` |
| Shape | Text from users or other languages | Emoji; right-to-left text; mixed direction; diacritics and tall scripts; numbers in columns | `better-typography`; mirroring `better-layout` |
| Quantity | It repeats over a list | 0 items; 1 item; the real count; 10 times the real count | `better-writing` (empty state), `better-layout` |
| Container | Always | A 320 px wide box; squeezed by a sibling in a flex row; a very wide box | `better-layout` |
| State | It has the state (loading, error, disabled) | Each state it has | `better-accessibility`, `better-ui` |
| Environment | The profile lists it | Dark theme, 200% zoom, reduced motion: named for the user to switch, never faked on the page | `better-colors`, `better-accessibility` |

Write the kept scenarios down before you build, one line each, and say in one line which axes you dropped and why.

## 3. Build the harness on the DEV page

Nothing is saved: the harness is injected into the published page with `oml browser try`, and a reload removes it.

Write `break.js`. It finds the block's rendered root by its widget name (the platform id ends in `-<Name>`). It clones the root once per scenario into a column added to the page, with a short label above each clone. Each clone changes only what its scenario changes:

- Length and shape: replace the text of the elements that show data.
- Quantity: remove all list items, keep one, or clone items to 10 times the count.
- Container: set the clone's wrapper width to 320 px, put it in a flex row next to a wide sibling, or make it very wide.

The harness adds labels, widths and texts only: no fonts, styles or theme changes of its own, which would test a different block. Run it:

`oml browser try <url> --js break.js --screenshot break.png`

In a session started with `oml browser start --headed`, the user sees the harness in that window and can scroll it.

A clone is the block's real markup and CSS without its logic. A state that needs logic (loading, a server error) cannot be cloned. List it as not tested. To see such states for real, the user can run `state-machine`.

## 4. Look once

Look at the screenshot (and `layout` in the output) once, top to bottom, and note what visibly broke: "text runs out of the card's right edge", never "spacing feels tight". A predicted failure is not a finding. With no browser available, say so and give the user `break.js` and the command to run.

Then add the notes to the page: one line under each broken scenario's label, in `break.js`, and run it once more, so the page reads as the report on its own.

## 5. Report what broke and stop

Broken scenarios first:

| Scenario | Observed | Owner |
| --- | --- | --- |
| One unbreakable 60-character string | Runs out of the card; no wrap, no truncation | `better-typography` |
| 0 items | Empty white area, no message | `better-writing` |

This skill owns no domain rules and gives no verdict. "Everything survived" is a full report: say which scenarios ran and how to run the harness again. Fix nothing unless asked; on a request to fix, follow the owner skill, `oml apply` to a new staged file, and run the harness again after the user publishes.

## 6. Keep the script, drop it on request

The page is half the report. Keep `break.js` and the screenshot until the user is done; nothing is in the module to remove.

## Before you finish

| Mistake | Fix |
| --- | --- |
| A scenario without the text it was fed | The harness found the wrong element; fix the selector and run once more |
| The window resized per scenario | Widths are fixed boxes in the harness; one load shows them all |
| A break in the table but not marked on the page | Note it under the label |
| A fake dark theme or zoom in the harness | Name the mode for the user to switch |
| A test screen added to the module for this | Inject instead; a module change needs `oml apply` and an install |
