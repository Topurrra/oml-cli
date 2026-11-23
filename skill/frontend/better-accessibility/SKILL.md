---
name: better-accessibility
description: Reviews and fixes keyboard and focus behavior, accessible names, labels, forms, announcements, motion and zoom in OutSystems screens and blocks against WCAG 2.2.
---

# Accessibility

This skill reviews and fixes semantics, keyboard and focus, accessible names, forms, announcements, motion and zoom in OutSystems 11 Reactive and Traditional Web UI. It reports each failure against the WCAG 2.2 criterion it breaks and writes the fix as a widget property, a widget attribute or theme CSS.

A review is two walks on the published page. Keyboard only: every flow completes without a mouse. Then names: every control has a name, a role and its state (`oml browser page` lists `a11y` problems). When unsure, keep the platform widget and remove attributes rather than add them.

Contrast measurement and color fixes belong to `better-colors`. Text size and language belong to `better-typography`. Spacing between targets belongs to `better-layout`. Label, error and alt text wording belong to `better-writing`. Animation recipes belong to `better-motion`.

## Criteria, not conventions

A finding cites a WCAG 2.2 A or AA criterion, a task an assistive-technology user cannot complete, or a `better-interface` escalation trigger. Everything else is a recommendation and never `HIGH`: AAA criteria, one `h1` per page, no skipped heading levels, and the 44 px and 40 px targets. The thresholds below are exact.

## How attributes get on a widget

In Reactive, HTML attributes (`aria-label`, `role`, `alt`, `autocomplete`, `aria-hidden`) go in the widget's `attributes` in the spec, each value an expression: `"attributes": { "aria-label": "\"Close\"" }` (the Widget attributes row of `oml spec "Reactive UI and Mobile UI"`). `oml describe` shows them as `attr aria-label="Close"`. In Traditional Web, apply refuses `attributes`: list each attribute for the user to add as an Extended Property in Service Studio. The platform sets widget ids: never set `id`.

## Platform widgets first

Use a Button for an action and a Link for navigation. Never put On Click on a Container, Icon or Image: it has no role, no focus and no keyboard. An external URL is an HTML Element `a` with `href` (`oml spec "Reactive UI and Mobile UI"`). OutSystems UI patterns (Tabs, Accordion, DropdownSearch, Sidebar, DatePicker) bring their own keyboard behavior: test it, never rebuild it.

## Disabled means unavailable

Set `Enabled` to False when a control is unavailable. Put the reason in visible text next to it, not in a tooltip: a disabled control gets no focus or hover, so keyboard and touch users never see a tooltip.

## Visible focus

Style `:focus-visible`, not `:focus`, in the theme. Prefer the browser ring; never `outline: none` without a replacement you checked. A custom ring uses a theme color and reaches 3:1 against every color it crosses (1.4.11). In Windows high contrast (`forced-colors: active`) keep system colors. A focused element must never hide fully under a sticky header (2.4.11). Recipes: [recipes.md](recipes.md) (`oml doc better-accessibility/recipes`). Check each state with `oml browser look <url> <Widget> --state focus-visible`.

## Full keyboard support

Every mouse action has a keyboard path. Escape closes the last opened overlay; arrow keys move inside a composite widget; Tab moves between widgets; Enter and Space press buttons. Use only `tabindex="0"` (to join the tab order) and `tabindex="-1"` (for focus from code). A positive value breaks the order: fix the widget order instead.

## Trap and restore focus

A Popup is bound to a Boolean local; with the WCAG 2.2 setting on, also close it on Escape (`oml doc ui-reactive#popups`). When it opens, Tab must land inside it; when it closes, focus returns to the button that opened it. If the published page does not do this, move focus in a JavaScript node of the open and close actions, scoped to the block's root. For a destructive confirmation, focus the safe button first.

## Minimum target size

WCAG 2.5.8 AA: a target is at least 24 by 24 CSS px, or has 24 px of free space around it. `oml browser page` reports a smaller button or field as `small` in `layout`. Aim for 44 px on touch and 40 px on desktop where the density allows. Every drag has a one-pointer alternative, such as move up and move down buttons (2.5.7).

## Label every input

Every Input, Dropdown, Checkbox and Switch has a Label widget whose Input Widget points to it (spec `targetWidget`). A prompt (placeholder) is never a label. Give fields about the user an `autocomplete` attribute (1.3.5) and the Input Type that opens the right keyboard. Never block paste. Tables: [recipes.md](recipes.md#forms).

## Errors that announce

Keep the submit button enabled and Built-in Validations on; validate on submit. For a custom check set `<Input>.Valid = False` and `<Input>.ValidationMessage`, then test `<Form>.Valid`; repeat every check in the server action (`oml doc ui-reactive#forms-and-validation`). On the published page, check that the failing input is marked invalid and its message is read with it. While the request runs, show a spinner next to the button's own label (OutSystems UI `ButtonLoading`, input `IsLoading`).

## Accessible names everywhere

An icon-only Button or Link has an `aria-label` that names the action. A name starts with the visible label text (2.5.3). A decorative Icon or Image gets `aria-hidden="true"`, never on or above a focusable widget.

## Never color alone

Status needs a second cue: an icon, a word or an underline (1.4.1). This skill decides which contrast rule applies; `better-colors` measures the pair:

| Criterion | Applies to | Minimum |
| --- | --- | --- |
| 1.4.3 AA | Text | 4.5:1 |
| 1.4.3 AA | Large text: 24 px, or 18.67 px bold | 3:1 |
| 1.4.11 AA | Input borders, focus rings, states, meaningful icons | 3:1 |

Disabled controls and logos are exempt. Report a failing pair and its criterion; change colors only when asked.

## Honor reduced motion

Movement, scale and blur run only under `@media (prefers-reduced-motion: no-preference)`. Opacity fades, spinners and a short press feedback may stay. Under reduced motion, replace slides with an opacity fade and stop parallax and autoplay. The recipes are in `better-motion`.

## Nothing important runs on a timer

Moving or auto-updating content that lasts over 5 seconds has a pause control (2.2.2): a Carousel with `AutoPlay` needs one. A message with an action or an error stays until the user closes it: use an `Alert` or a `Notification` the user closes, not a message that fades.

## Announce changes

- Focus moves there anyway (an opened Popup, the first invalid input): nothing more.
- Tied to one input: its validation message.
- Not urgent, not tied to an input (saved, result count, loading): a Container with attribute `role` = `"status"`, rendered from the start, whose text changes. Do not wrap it in an If: an If removes it, and a new region is often not read.
- Urgent, not tied to an input (session expired, the form failed): `role` = `"alert"`.

## Alt text by purpose

Decorative: `alt=""`. Informative: the meaning. Functional (the image is the button): the action. An Image with no `alt` makes a screen reader read the file name.

## Structure is navigation

Every screen has a title. Headings are HTML Elements with tag `h1` to `h6` that describe their sections; `oml browser page` reports a missing `h1` and skipped levels. Turning on `EnableAccessibilityFeatures` on the layout helps every screen; it is a layout change, so ask the user first.

## Survive zoom and reflow

Text survives 200% zoom (1.4.4). At 320 px width the page reflows with no horizontal scroll (1.4.10): run `oml browser sweep <url> --widths 320`. When the profile has no phone target, oml refuses that width; ask the user, then add `--no-profile`. Text boxes take `min-height`, not `height`. Never add `maximum-scale=1` or `user-scalable=no`.

## Before you finish

| Pattern | Fix |
| --- | --- |
| `outline: none` with no `:focus-visible` rule next to it | Keep the browser ring or add a checked one |
| On Click on a Container, Icon or Image | A Button, or a Link when it navigates |
| Save disabled until the form is valid | Keep it enabled and validate on submit |
| Status region inside an If | An always-rendered Container with `role` = `"status"` |
| `role` = `"alert"` on a success message | `"status"` |
| Image with no `alt` | `alt=""` when decorative, else its purpose |
| `aria-label` that leaves out the visible text | Start the name with the visible text |
| `aria-hidden` on a container that holds a Button, Link or Input | Remove it |
| A tooltip on a disabled Button | Visible text next to it |
| Input with a prompt and no Label | Add a Label with Input Widget set |

## Reporting

**Verification.** Without a published page: names on every Button, Link and Icon, Labels bound to inputs, `:focus-visible` and `prefers-reduced-motion` rules in the CSS. With one: `oml browser page` (`a11y`, `layout`), Tab through the flow in a headed session (`oml browser start --headed`), and `oml browser look --state focus-visible` on each stop. Report each check you could not run as `Not verified`.

**Standalone severity.** `HIGH` stops a task, hides content from assistive technology or repeats across the app; this domain's escalation triggers are `HIGH` on sight. `MEDIUM` makes a task much harder. `LOW` is isolated. Group findings under the rule they break, one row per root cause with every place: Severity, Location, Before, After, Why. End with `Block` when a `HIGH` remains, else `Approve`. With nothing to report: "No actionable accessibility findings", then the verification.
