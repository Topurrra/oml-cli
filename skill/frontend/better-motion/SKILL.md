---
name: better-motion
description: Decides what should animate in OutSystems screens and builds it in CSS, with exact durations, easing, scale and distance, and recipes for enters, exits, icon swaps and reduced motion.
---

# Motion

This skill decides whether an element in an OutSystems screen should move and gives the values and CSS recipes when it should. Motion lives in the theme or the block's style sheet as CSS transitions and keyframes; a state change switches a class through the widget's Style Classes expression.

## Less motion, exact values

The first question is whether to animate at all, and the answer is often no. Golden rule 1: motion only to explain a change, short, and off with reduced motion. Removing an animation is a valid fix.

Exact values: the press scale, the icon cross-fade, the stagger and the curves. So `0.96` is not `0.95`. The duration, scale and distance tables are starting points for desktop: adjust by eye for size and distance. Keep the app's existing durations and curves where no rule here gives a value. A deliberate, consistent project value (a press at `0.95` everywhere) stays. The same detail done two ways is a finding.

The reduced-motion requirement and pause controls belong to `better-accessibility`. Surfaces and icon looks belong to `better-ui`. The layout an animation moves between belongs to `better-layout`.

## What the platform already animates

OutSystems plays its own transition between Reactive screens (the module's default is in `oml inspect <module.oml>` under `module.settings.defaultTransition`, such as `SlideFromRight`), and OutSystems UI patterns (`Sidebar`, `Notification`, `Accordion`, `BottomSheet`, `Tooltip`) animate themselves. Keep those; add no page-enter animation on top of a screen transition and no second animation on a pattern. Check on the published page whether each pattern respects `prefers-reduced-motion` before you call it compliant. In Traditional Web, an Ajax Refresh node has `animationEffect` (`None`, `Highlight`, `Fade`, `VerticalSlide`): use `None` for a refresh that runs often. A refresh replaces the widget's HTML, so a keyframe animation on it plays again on every refresh.

## How often it is seen decides whether it moves

Typing, row hovers and content behind a tab change at once, or with 150 ms or less on `opacity` or `background-color`. A menu opened many times a day opens at once and animates only as it closes. Keep expressive motion for rare moments: a first load, a success state, an empty state. Every animated change also leaves a static cue (a color, an icon, a word); motion is never the only feedback.

## Transitions for state, keyframes for one-off sequences

Interactive state (hover, open, selected) uses CSS `transition`, which reverses smoothly when the user changes their mind. Keyframes run a fixed timeline and cannot reverse: keep them for sequences that run once, such as an entrance or a loader. A transition does not run on first render and a keyframe animation does, so a state never animates in on load when it is a transition.

## Duration follows size

| Element | Enter |
| --- | --- |
| Tooltips and small elements | 150 ms |
| Dropdowns and menus | 250 ms |
| Popups, sidebars and panels | 350 to 450 ms |

An exit is shorter than its enter: a dropdown that opens in 250 ms closes in 150 ms.

## Easing

Enters, exits and state changes: `ease-out`. Loaders and other loops: `linear`. A sheet opening, a shape morphing or a drag settling imitates a physical object. CSS has no spring, so use `cubic-bezier(0.2, 0, 0, 1)`: 300 ms for small swaps, 400 ms for a tab indicator, 450 ms for a sidebar. Color and opacity changes never take that curve; they take `ease-out`.

## Scale shrinks as the element grows

A tooltip or dropdown enters from `0.96` or `0.97`. A Popup or panel enters from `0.98` or `0.99` and exits to `0.99`. Set `transform-origin` toward the widget that opened it (`top left` for a menu under the left edge of its button); a Popup in the center of the screen keeps the center.

## Move only far enough to show direction

| Movement | Distance |
| --- | --- |
| A text swap | 4 px |
| A screen-level change | 8 px |
| Dropdowns, tooltips, most elements | 8 to 32 px |
| Sidebars and sheets | Up to 70% of their own size |

A dropdown moves away from its trigger; a panel enters from the side it opens on.

## Press scales to 0.96

A pressed Button scales to `0.96` over 150 ms `ease-out`; a disabled one never scales. Scope it to your class. Recipe: [recipes.md](recipes.md#press) (`oml doc better-motion/recipes`).

## Stagger rare entrances by 100 ms

Where order shows hierarchy in a rare entrance, split it into title, description and actions, 100 ms apart. Each enters with opacity, 4 px of blur and 12 px of `translateY` over 300 ms `ease-out`. Never stagger list rows or routine actions. The OutSystems UI `Animate` block exists; prefer the CSS recipe, which has the reduced-motion fallback built in: [recipes.md](recipes.md#staged-entrance).

## Exits are short or instant

An If removes content at once and a Visible change hides it at once: that is the right default. Where an exit must animate, use opacity, 4 px of blur and a fixed `-12px` `translateY` over 150 ms, never the full height. Slide fully out only when the destination means something, such as a sidebar closing.

## Cross-fade icon swaps

An icon that swaps on a rare change (copy to copied, play to pause) cross-fades: scale `0.25` to `1`, opacity `0` to `1`, blur `4px` to `0px`, 300 ms `cubic-bezier(0.2, 0, 0, 1)`. Keep both Icons rendered and switch a class with the Style Classes expression (`If(IsCopied, "is-copied", "")`): [recipes.md](recipes.md#icon-cross-fade). Tab icons and actions revealed on row hover swap at once.

## Animated height

`OutSystems UI` `Accordion` already animates its height. For your own block, animate to a measured height: a ResizeObserver on an inner wrapper, removed in `OnDestroy` (`oml doc ui-checklist`): [recipes.md](recipes.md#measured-height). Skip it where content changes on every keystroke.

## Drag

There is no shared drag widget in OutSystems UI. When the app uses a JavaScript drag library in a block, a row that snaps lands on a snap point, never where the fling throws it. Settings: no momentum, elastic about `0.05`, commit past half the distance to the next point, settle with `cubic-bezier(0.2, 0, 0, 1)` over 300 ms. Set `touch-action: none` on the drag surface only. Every drag needs a one-pointer alternative (`better-accessibility`).

## Gate motion behind reduced motion

Movement, blur and enter or exit scale run only under `@media (prefers-reduced-motion: no-preference)`. Under reduced motion, replace them with an opacity fade instead of removing the element at once. The press scale may stay: it is feedback, not travel. Recipe: [recipes.md](recipes.md#reduced-motion).

## Transition only what changes

Name the properties (`transition-property: transform, opacity`), never `all`. Animate `transform`, `opacity`, `filter` and `clip-path`, which skip layout; measured height is the one layout property worth animating. Add `will-change` only after you saw a stutter or a 1 to 2 px jump at the start or end, and name the property (`will-change: transform`), never `all`.

## Theme switch without a smear

When the app has a theme toggle and colors have transitions, turn transitions off for the switch: [recipes.md](recipes.md#theme-switch).

## Slow it down before you call it done

Speed hides flaws. On the published page, in a headed session (`oml browser start --headed`), trigger the change and slow every running animation to a tenth with `oml browser raw Runtime.evaluate slow.json`, where `slow.json` holds `{ "expression": "document.getAnimations().forEach(a => a.playbackRate = 0.1)" }`. It reaches only the animations running at that moment. Look for content that pops while its container still moves, a popup growing from its center, and a 1 to 2 px jump. Fix, then check again at full speed.

## Before you finish

| Detection | Fix |
| --- | --- |
| `transition: all` | Name the changing properties |
| `animation:` on `:hover` or a toggled class | A transition on the same properties |
| A press scale below `0.96` with no single app value | `0.96` |
| `:active` scale with no disabled exclusion | `:not(:disabled):active` |
| A Popup or panel entering from a scale below `0.98` | `0.98` |
| A menu with no `transform-origin` | Toward its trigger |
| An exit as long and far as its enter | 150 ms and a fixed 12 px |
| `opacity: 0` in a base rule that only an animation reveals | The hidden state in the `from` keyframe, `animation-fill-mode: both` |
| `transform`, `translate`, `filter` or entrance scale with no reduced-motion gate | Gate it; fade opacity under reduced motion |
| A page-enter animation on top of the screen transition | Remove it |
| An Ajax Refresh on every keystroke with an effect | `animationEffect` `None` |
| `will-change: all`, or on an element that never animates | Name the property, or delete it |

## Reporting

**Verification.** Without a published page: read every animated state in the CSS with its properties, durations, curves and origin. With one: trigger each state, reverse it mid-way, and replay it slowed as above. Report each check you could not run as `Not verified`.

**Standalone severity.** `HIGH` breaks an interaction (a keyframe toggle that cannot reverse); motion that ignores reduced motion and a state change shown by motion alone are `HIGH` on sight. `MEDIUM` is motion that gets in the way or glitches: an animated frequent action, a snapping height, content popping mid-transition. `LOW` is an isolated value off its rule. One row per root cause with every place: Severity, Location, Before, After, Why. End with `Block` when a `HIGH` remains, else `Approve`. With nothing to report: "No actionable motion findings", then the verification.
