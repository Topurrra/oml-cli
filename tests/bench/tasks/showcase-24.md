Expect: Fx*:24, screens:Showcase:1
## Task (identical for every agent)
In the OutSystems 11 module, build a **component showcase**: 24 original UI components as Reactive blocks in a UI flow named `ShowcaseUI`, and one screen `Showcase` that shows them all.

Write every component yourself in plain CSS and vanilla JavaScript (no React, no libraries, no CDN, no downloads). Each component is a block named `Fx` + the PascalCase name (e.g. `FxShineText`) with typed inputs and sensible defaults. Scope CSS under one root class; JavaScript runs in the block's OnRender (a JavaScript node), finds its root container by Id, never touches anything outside it, and cleans up listeners/animation when re-rendered or removed. Respect `prefers-reduced-motion`. Use these theme colours with fallbacks: `var(--fx-bg,#fff)`, `var(--fx-surface,#f4f5f7)`, `var(--fx-text,#111827)`, `var(--fx-muted,#6b7280)`, `var(--fx-border,#e5e7eb)`, `var(--fx-accent,#6366f1)`.

Components ("Has Content" means the block has a placeholder named Content):
1. Split Reveal — letters rise and fade in one after another when visible. Inputs: Text, delay per letter.
2. Blur In — words go from blurred to sharp one by one when visible. Inputs: Text, direction (top/bottom).
3. Shine Text — a light band sweeps across muted text in a loop. Inputs: Text, speed, disabled.
4. Gradient Flow — text with a slowly moving multi-colour gradient, optional animated gradient border. Inputs: Text, three colours, speed, show border.
5. Scramble Text — random characters resolve into the text letter by letter, on view or hover. Inputs: Text, trigger, speed.
6. Word Rotator — shows words one at a time with a slide transition. Inputs: Words (comma-separated), interval.
7. Typewriter — types phrases with a blinking caret, deletes, types the next. Inputs: Phrases (separated by |), typing speed, pause.
8. Number Count — counts from a start to an end number with easing when visible, with thousands separators. Inputs: From, To, duration, separator.
9. Fade In View — content fades and slides in when visible. Inputs: direction, distance, duration. Has Content.
10. Click Burst — every click draws short lines bursting from the click point (canvas). Inputs: colour, count, size. Has Content.
11. Magnetic — content moves towards the pointer when near and springs back. Inputs: strength, radius. Has Content.
12. Glow Border — rounded border where two light spots travel along the edge. Inputs: colour, speed. Has Content.
13. Hover Glare — a diagonal glare sweeps across the content on hover. Inputs: colour, angle, duration. Has Content.
14. Glyph Rain — canvas background of changing letters and symbols with colour changes and a vignette. Inputs: colours, speed. Has Content.
15. Aurora Glow — background of large soft colour shapes drifting and blending. Inputs: three colours, speed. Has Content.
16. Spotlight Card — dark card where a soft radial light follows the pointer. Inputs: Title, Description, light colour. Has Content.
17. Tilt Card — image card tilting in 3D towards the pointer with a moving shine and caption. Inputs: ImageUrl, Caption, max tilt.
18. Stagger List — items animate in one after another; arrow keys move a highlighted selection. Inputs: Items (separated by |).
19. Step Wizard — stepper with numbered circles, progress line, Back/Next and a Complete state. Inputs: Steps (titles separated by |).
20. Odometer — each digit rolls vertically to a new value. Inputs: Value, digit height.
21. Stretch Slider — range slider that stretches like elastic past its ends, with value label. Inputs: Min, Max, Value, Step.
22. Grid Trail — canvas of slowly scrolling square lines; the square under the pointer lights up. Inputs: square size, direction, speed, colours. Has Content.
23. Particle Field — canvas of floating dots connected by thin lines; dots move away from the pointer. Inputs: count, colour, speed. Has Content.
24. Wave Lines — canvas of thin lines moving like waves and bending around the pointer. Inputs: colour, amplitude, speed. Has Content.

Screen `Showcase` (flow MainFlow, the module's existing layout, access: registered users; do not change the default screen): title "oml showcase"; one card per component in a responsive grid with name, one-sentence description, a live preview (block instance with demo values), and three buttons that copy text to the clipboard via a client action `FxCopyText(Text)` (clipboard API with a textarea fallback and a short "Copied" message): "Copy spec" (how to recreate the block), "Copy agent prompt" (a one-sentence request for an agent), "Copy CSS + JS" (the component's CSS and JavaScript). In the layout header: a sun/moon icon button that switches `data-theme` dark/light on `<html>` (remembered in localStorage, default from prefers-color-scheme, the theme variables defined for both themes) and a Sans/Serif/Mono font picker (`data-font` on `<html>`, system font stacks, remembered).

Done when: the module has zero validation errors, every block and the screen exist, and you report: every tool call that failed and why, and anything you could not build.
