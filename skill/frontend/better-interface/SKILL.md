---
name: better-interface
description: Reviews an OutSystems screen, flow or whole app across accessibility, layout, writing, typography, color, UI polish and motion, and returns one ranked report.
---

# Interface review

This skill runs a review across every UI domain for OutSystems 11 Reactive and Traditional Web screens. It sends the screen to each domain skill, collects their evidence and writes one ranked report with a verdict.

It owns only the orchestration. Accessibility rules belong to `better-accessibility`, structure to `better-layout`, copy to `better-writing`, type to `better-typography`, color to `better-colors`, surfaces and icons to `better-ui` and motion to `better-motion`. A review of a change (a staged module against its source) belongs to `interface-review`.

## Evidence, not taste

Press hard on the escalation triggers. Leave deliberate project choices alone: a density, radius or voice you dislike is not a finding. The bar for a finding is evidence. The bar for `Approve` is that you inspected what you say you inspected.

## 1. Resolve the scope

Name the screen, flow or module from the request. Cover all of it in every domain, with the empty, loading, error and phone-width states where they exist. When the scope is too large, narrow it to one complete flow (the one the request is about, else the entry path every user takes). State what you left out.

## 2. Send a change to `interface-review`

A request about a staged module, an `oml apply` result or two module versions is a change review. Ask the user to run `interface-review`. When it hands a review back, apply everything below to it. The cap and the verdict then cover `Introduced` and `Regression` findings only.

## 3. Recon before judgment

Read in one call: `oml inspect <module.oml> --own --only screens,blocks`, `oml describe <module.oml> <Screen> --css`, `oml config` and `oml team-rules`. Learn:

- Reactive or Traditional Web, and OutSystems UI or OutSystems UI Web (or an older theme).
- The theme's tokens (`--color-*`, `--space-*`, `--border-radius-*`, `--font-size-*`). An older Traditional Web theme may have no CSS variables.
- The app's own blocks and the OutSystems UI patterns it uses (`oml doc osui-patterns`).
- The profile: `ui.targets`, `ui.themes`, `ui.rtl`, `ui.accessibility`, `ui.translations`. Review only the sizes and themes it lists.

Write every fix in the app's idiom: a theme or block style sheet, a Style Classes value, a widget property, an OutSystems UI pattern. Never a fix in React, Tailwind or a library the app does not use. A team rule settles taste but never excuses a trigger. When a theme rule or a shared block causes a finding, report it once against that source.

## 4. Run each domain skill

Read each skill (`oml doc <name>`) and finish its review before you consolidate, in this order:

1. `better-accessibility`
2. `better-layout`
3. `better-writing`
4. `better-typography`
5. `better-colors`
6. `better-ui`
7. `better-motion`

Take each skill's rules and checks. Its own severity and format are for standalone use; this file replaces them. A skill you cannot read is `Not reviewed`; never rebuild its rules from memory. When two skills cover one issue, give it to the owner of the rule and note the side effect in **Why**.

## 5. Verify on the running app

Before a publish there is nothing to render: mark visual claims `Not verified`. After a publish to DEV:

- `oml browser page <url> --screenshot <Screen>.png`: read `a11y`, `layout` and `errors`, and look at the picture.
- `oml browser sweep <url>`: every width the profile supports.
- `oml browser look <url> <Widget> --state focus-visible`: which rule wins, per state. `--theme dark` for the dark theme.

Report each command and what it showed. A check you could not run is `Not verified`, never a finding.

## Require evidence

Every finding names its place: `Screen > Block > Widget` (the `path` from `oml browser look`) or `Theme.css: selector`. Show the current value. A visual finding needs the rendered page, unless it follows from declared values (a `384px` fixed panel at a `390px` phone width is a source finding).

## Rank by user impact

- `HIGH`: blocks a task, misleads, hides content or controls, risks data loss, or repeats across the app.
- `MEDIUM`: harms understanding, speed, adaptability or consistency.
- `LOW`: isolated polish.

Within a severity, rank by reach: a theme or shared block fix outranks the same symptom on one screen.

**Escalation triggers.** Once the owning skill confirms one, it is `HIGH`, exempt from the cap, and listed every time:

- A control with no accessible name.
- A keyboard-reachable control with no visible focus.
- A control reachable by mouse but not by keyboard.
- Motion or autoplay that ignores `prefers-reduced-motion`.
- Content or a control clipped, overlapped or unreachable at 320 px width or 200% zoom.
- Body or control text whose rendered contrast fails its ratio.
- State or meaning carried by color alone.
- A destructive action with no confirmation, undo or distinct look.
- Truncated content with no way to the full value.
- Content hidden past a scroll edge or in a collapsed part with no visible cue.
- An error that names no way to recover.
- A semantic color used against its meaning (the danger color on a safe action).
- A state change carried by motion alone.

## Prefer the cheaper fix

Take the first fix that works:

1. **Delete.** A separator that space would carry, an animation on a frequent action, an attribute a platform widget makes redundant.
2. **Use the platform.** A Button or Link widget, an OutSystems UI pattern, the browser's focus ring.
3. **Reuse.** A theme token, the app's block, an existing class.
4. **Correct the value.** The exact value the owning skill gives.
5. **Add.** A new class, token, attribute or media query.

When the work under review adds something that **Delete** would fix, that is a finding.

## Consolidate

One root cause is one finding, with every place in the same row. At most 15 findings; never pad. No findings is a valid result.

## Read only by default

A review changes nothing. When the user also asks for the fixes, the report is the change scope: write one spec, `oml apply` it to a new staged file, and verify again after the user publishes.

## Report

State the scope, module kind, theme and profile settings. Then the coverage table:

| Domain | Evidence inspected | Result |
| --- | --- | --- |
| Accessibility | Screens, widgets, commands | Finding count, or `Clear`, or `Not reviewed` with the reason |

Then one findings table, by severity, then reach:

| Severity | Domain | Location | Before | After | Why |
| --- | --- | --- | --- | --- | --- |
| HIGH | Accessibility | `Orders > OrderCard > DeleteButton` | Icon-only Button, no name | Add attribute `aria-label` = `"Delete order"` | The control has no accessible name |

Say how many findings the cap left out. With none: "No actionable interface findings." Then **Verification**: each command and its result, with `Not verified` checks apart. End with one verdict:

- `Block`: one `HIGH` finding or more remains.
- `Approve`: no `HIGH` finding remains. `MEDIUM` and `LOW` stay in the table as work to do.

Name every `Not reviewed` domain in the verdict line.

## Before you finish

| Mistake | Fix |
| --- | --- |
| One findings section per domain | One table, by severity, then reach |
| A rendering claim from the spec or CSS alone | Look at the published page, or mark it `Not verified` |
| A domain marked `Clear` that you never read | `Not reviewed`, with the skill name |
| The same symptom on five screens, five rows | One row against the theme rule or block |
| A fix in Tailwind, React or a new library | Rewrite it as theme CSS, a widget property or a pattern |
