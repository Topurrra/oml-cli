# UI checklist

Use this list for every block and screen you build. You cannot see the result, so look at a screenshot of the real app.
Never report UI work as done without at least one screenshot that you looked at: `oml preview --live` (style sheets), `oml qa page --screenshot` (after a publish), or `oml preview` (no running app yet). If the user has not published and no command above can run, report the work as "pending visual check" instead of done.

- **Labels:** No default widget texts. A new Link has the text "link" and a new Button has "Button". Replace them; do not add text after them.
- **Content:** Use real demo content. No "Demo content", "Lorem ipsum" or "Text".
- **Theme:** Use theme variables (`var(--color-...)`) and your own variables for new colours, also in the header and footer.
- **Theme CSS:** Scope theme rules to your class (`[data-theme="dark"] .my-block`). Never style `body`, `html`, `*` or a bare `button`.
- **Size:** A canvas, a background effect or an absolute child needs a parent with an explicit `height`, `min-height` or `aspect-ratio`.
- **Preview size:** The preview must be readable at 1366 and 1920 px (390 px for mobile work): nothing cut off, empty or one pixel high.
- **Layout:** Responsive units (`%`, `rem`, `minmax`, `flex`). No horizontal scroll (`horizontalOverflow` must be `false`).
- **Focus:** Focus is visible on every link, button and input.
- **Names:** Each Input has a Label or prompt. Each icon-only Button has an aria label. Decorative elements have `aria-hidden="true"`.
- **Forms:** Put inputs in a Form. For a custom check, set `<Input>.Valid = False` and `<Input>.ValidationMessage`, then test `<Form>.Valid`. Repeat the checks in the server action.
- **Contrast:** Text contrast is at least 4.5 to 1 in each theme you preview.
- **Motion:** `@media (prefers-reduced-motion: reduce)` stops animation.
- **JavaScript scope:** Query inside your root (`document.getElementById($parameters.RootId)`), never the whole page. No globals.
- **JavaScript cleanup:** Every `window` or `document` listener, timer, observer and animation frame is removed in `OnDestroy`.
- **Lists:** List items are direct children of the list, after a `<script>`: style `.my-list > *`; in a grid hide `[data-list] > script`.
- **CSS variables:** Reactive drops `--name` from a style attribute: set them from a Script (`style.setProperty`).
- **Platform widgets:** Restyle Switch, Checkbox, Dropdown and Input with OutSystems UI's selector plus your class (`input[data-switch].my-switch`); check with `oml qa page --style-conflicts`.

## The loop

1. `oml apply` the spec to a new staged file.
2. Only style sheets changed: `oml preview --live <screen url> <source.oml> <staged.oml>`. Look at `before` and `after`. Changes in `needsPublish` need step 3.
3. Anything else: the user installs and publishes to DEV. Then `oml qa page <screen url> --screenshot <Screen>.png`; read `layout` and `errors` too.
4. Fix every problem you see in a new staged file. Repeat from step 1.

No running app yet (a new module): `oml preview <staged.oml> <Element> --width 1366 --screenshot 1366.png`. It shows only your own CSS and texts, without OutSystems UI. Read its `checklist` and `scriptErrors`. Details: `oml doc commands#preview`.
