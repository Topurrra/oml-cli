# UI checklist

Use this list for every block and screen you build. You cannot see the result, so look at a preview.
Never report UI work as done without at least one `oml preview` that you looked at.

- **Labels:** No default widget texts. A new Link has the text "link" and a new Button has "Button". Replace them; do not add text after them.
- **Content:** Use real demo content. No "Demo content", "Lorem ipsum" or "Text".
- **Theme:** Work in light and dark, including the layout header and footer. Use theme variables (`var(--color-...)`) and your own variables for new colours.
- **Theme CSS:** Scope theme rules to your class (`[data-theme="dark"] .my-block`). Never style `body`, `html`, `*` or a bare `button`.
- **Size:** A canvas, a background effect or an absolute child needs a parent with an explicit `height`, `min-height` or `aspect-ratio`.
- **Preview size:** The preview must be readable at 1200 px and at 390 px: nothing cut off, empty or one pixel high.
- **Layout:** Responsive units (`%`, `rem`, `minmax`, `flex`). No horizontal scroll at 390 px (`horizontalOverflow` must be `false`).
- **Focus:** Focus is visible on every link, button and input.
- **Names:** Each Input has a Label or prompt. Each icon-only Button has an aria label. Decorative elements have `aria-hidden="true"`.
- **Forms:** Put inputs in a Form. For a custom check, set `<Input>.Valid = False` and `<Input>.ValidationMessage`, then test `<Form>.Valid`. Repeat the checks in the server action.
- **Contrast:** Text contrast is at least 4.5 to 1 in both themes.
- **Motion:** `@media (prefers-reduced-motion: reduce)` stops animation.
- **JavaScript scope:** Query inside your root (`document.getElementById($parameters.RootId)`), never the whole page. No globals.
- **JavaScript cleanup:** Every `window` or `document` listener, timer, observer and animation frame is removed in `OnDestroy`.

## The loop

1. `oml apply` the spec to a new staged file.
2. `oml preview <staged.oml> <Element> --theme light --screenshot light.png`
3. `oml preview <staged.oml> <Element> --theme dark --width 390 --screenshot dark-390.png`
4. Look at both screenshots. Read `checklist`, `scriptErrors`, `skipped`, `blocked`, `horizontalOverflow` and `unsupported` in each report. With `--click`, `--type`, `--eval` or `--wait`, read `steps[].ok` (and `error`) first: a failed step means the screenshot does not show that interaction.
5. Fix every failed check and every problem you see. Use new output paths. Repeat from step 1.

The preview does not load OutSystems UI. Classes such as `card` or `btn` look plain there. Judge your own CSS, texts and layout in the preview. Test OutSystems UI styling in the published app.

JavaScript: the preview loads the required scripts of the block or screen and runs its OnInitialize, OnReady and OnRender JavaScript with stubs. Each `scriptErrors` entry names the node or script. Script elements that it does not require are not loaded (see `notes`). It has no network, so data comes from `--arg` values. To check an interaction, add steps: `--click "#ToggleButton" --wait 300 --eval "document.getElementById('Status').textContent"`, then read `steps` and look at the screenshot.
