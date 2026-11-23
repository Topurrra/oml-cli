---
name: interface-review
description: Reviews a change to an OutSystems module (a staged copy against its source, two exports or two server versions) for the UI problems it introduced or made worse. User-invoked.
disable-model-invocation: true
---

# Interface review of a change

This skill reviews a change rather than a screen. It resolves the two module versions, expands the changed elements to the screens they affect, reads both sides and gives every finding a status. Run it only when the user asks for it.

It owns the scope, the statuses and the change report. Domain rules belong to the `better-*` skills. Severity, the cap, the coverage table and the verdict belong to `better-interface`, which this skill hands the review to. Logic, data and security belong to `oml review` and `oml check`: name such a concern once and move on.

## The change, not the module

The author asks "did I make this worse?". Report what the change caused. Three old findings are a courtesy; thirty are a different review nobody asked for. Read the whole change before you judge it.

## 1. Resolve the two versions

The request names the target. Accepted targets:

- **A staged copy:** `<source.oml>` and `<staged.oml>` from an `oml apply`. The usual case.
- **Two exports** from Service Studio, as two `.oml` files of the same module.
- **Two server versions:** `oml review --server <Module> <v1> <v2>` (version numbers from `oml server versions <Module>`, or `published` and `latest`). It signs in to the server, so ask the user first.

With no target, look for the newest staged file next to its source and offer it. Never guess a pair. With nothing to compare, say so and offer: the newest staged copy, two versions on the server, or a whole-screen review by `better-interface` (not a change review). Never report a review of nothing as `Approve`.

## 2. List what changed

Run `oml review <before.oml> <after.oml> --md <new path>.md` once. Read `summary.byArea.ui` and the `ui` entries in `changes`: screens, blocks, widgets (added, removed, changed, moved), themes, style sheets and scripts. The `.md` shows the changed lines of style sheets, scripts and long expressions. For the widget detail of one screen, describe both sides and compare the two texts:

```text
oml describe <before.oml> <Screen> > before.txt
oml describe <after.oml> <Screen> > after.txt
git diff --no-index before.txt after.txt
```

For style sheets, do the same with `--css`.

## 3. A changed element is not a screen

A changed element is evidence; its reach is what you review. Expand one step by default: a changed block to the screens that use it. Find them by describing the screens (`oml describe <after.oml> <Screen1> <Screen2> ...`, names from `oml inspect --own --only screens,blocks`) and searching the text for the block's name. Expand two steps only for the theme, a theme token and the layout blocks, where one line reaches every screen. Review at most five screens in total; say how many you did not expand.

## 4. Read what was removed

Regressions are invisible in the new version. Check the removed side against this list and send each hit to its owner:

| Removed | Owner | What to check |
| --- | --- | --- |
| `aria-label`, `role`, `aria-describedby`, `aria-live` attributes | `better-accessibility` | The control or region lost its name or announcement |
| `alt`, a Label's Input Widget | `better-accessibility` | An image or input lost its text |
| A Button or Link replaced by a Container or Icon with On Click | `better-accessibility` | Keyboard and screen reader lost the control |
| `:focus-visible`, `outline`, `tabindex` in CSS | `better-accessibility` | Keyboard users lost the focus ring or the stop |
| `prefers-reduced-motion` | `better-accessibility` | Motion now ignores the setting |
| `inline-start`, `inline-end` swapped for `left`, `right` | `better-layout` | Right to left breaks (when `ui.rtl` is on) |
| `text-wrap`, `line-clamp`, `overflow-wrap`, `tabular-nums` | `better-typography` | Wrapping or number alignment changed |
| A theme token swapped for a literal or a lighter token | `better-colors` | The contrast pair may now fail: measure it |
| A text, validation message or empty state shortened or deleted | `better-writing` | The words lost what they told the user |

A removal is not a regression when the change replaces it. Examples: `aria-label` replaced by a visible label; an Icon with On Click turned into a Button; a literal replaced by a token of the same value.

## 5. Give every finding a status

- `Introduced`: the change created it.
- `Regression`: the change weakened something that was correct.
- `Pre-existing`: there before the change and not made worse.

Status by cause, not by place: a screen nobody touched that breaks because the theme changed is `Introduced` or `Regression`.

## 6. Hold the change to its stated purpose

Read what the change was for: the user's request, the spec, the `--notes` of `oml review`. Look for what is missing:

- A new state of a widget styled for some of hover, focus, active, disabled, loading and selected, not all.
- A new text with no translation when the module is multilingual (`ui.translations`).
- A new list or screen with no empty, loading or error state.
- A control added to one screen but not to the sibling screens that have its peers.

Do not report that a change does too much; that is a process question.

## 7. Hand it to `better-interface`

Give `better-interface` the scope and the affected screens; it runs the domain skills, ranks and caps. Give every finding a status. If `better-interface` is unavailable, report the scope and the changed elements, name the missing skill and stop.

## Never change anything

A change review only reads. Never `oml apply`, `install` or `publish` during it. Rendered checks are optional: a staged copy cannot render until the user installs and publishes it. Mark visual claims `Not verified`, or, when the user asks for a rendered review and has published, compare with `oml browser page <url> --snapshot before.json` taken before the publish and `oml browser diff before.json <url>` after.

## Report

Start with the scope block:

| Field | Value |
| --- | --- |
| Target | staged copy, two exports, or server versions |
| Before | `Orders.oml`, or `Orders` version 41 |
| After | `Orders.staged-3.oml`, or version 42 |
| UI changes | from `oml review`: 4 screens, 2 blocks, theme sheet |
| Screens expanded | `OrdersList`, `OrderDetail`; 3 more `OrderCard` users not expanded |

The coverage table from `better-interface` follows; a domain with no evidence in the change is `Not reviewed: no evidence in the change`. Then the findings with a `Status` column:

| Severity | Domain | Status | Location | Before | After | Why |
| --- | --- | --- | --- | --- | --- | --- |
| HIGH | Accessibility | Regression | `OrderCard > CloseButton` | `aria-label` removed | Restore `aria-label` = `"Close"` | The icon-only button had a name and lost it |

With no `Introduced` or `Regression` finding: "No actionable interface findings in this change." Then at most three `Pre-existing` findings, marked as not this change's, with how many more exist. Verification and the verdict follow `better-interface`'s format; the verdict covers `Introduced` and `Regression` only.

## Before you finish

| Mistake | Fix |
| --- | --- |
| A review of a pair the user never chose | Offer the targets and wait |
| A changed block with no screen under "Screens expanded" | Expand to its screens and name the ones you skipped |
| A removed `aria-label` or `prefers-reduced-motion` with no note | Send it to its owner with the table above |
| A broken untouched screen marked `Pre-existing` after a theme change | The change caused it: `Introduced` or `Regression` |
| `oml apply` or `install` in the command log | A change review only reads |
