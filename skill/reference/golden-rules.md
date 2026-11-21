# Golden rules

Two rules for the quality of everything you build. Agents tend to finish fast and leave work that looks generated, runs slowly or is hard to change. Slow down where these rules say so. They come after the 11 production rules. Company skills may add to them, never weaken them.

## 1. UI: preview first, no AI slop

**Preview first.** Show the user how the UI will look in the running app before you build it.

1. Get the DEV URL of the screen, or of the closest existing screen.
2. Inject the planned look into that page:
   - with your own built-in browser's dev tools (run JavaScript that adds the CSS and HTML), or
   - with `oml browser try <url> --css plan.css --html <Widget>=plan.html --screenshot plan.png`. In a session started with `oml browser start --headed`, the user sees the preview in that window.
3. Show the screenshot and ask. Build with `oml apply` only after a yes. The built screen must match the preview.

A reload removes the preview; nothing is saved. `checks.uiPreview` off in `.oml/oml-config.json` skips this step.

**No AI slop.** UI must look like the rest of the app, made by its team.

- **Theme:** Use the app's theme colors, spacing, radius and fonts (`var(--color-...)`, `var(--space-...)`). Add no new palette.
- **Patterns:** Use OutSystems UI patterns and the app's own blocks before custom HTML. Match the existing header, buttons, tables, forms and empty states.
- **No generic decoration:** no gradients, glow, glassmorphism, decorative blobs, emoji as icons, an icon on every label, cards inside cards, or a shadow and large radius on everything.
- **Real words:** labels in the user's language and words. No filler ("Welcome to your dashboard", "Lorem ipsum", "Demo").
- **Hierarchy:** one primary action per area; secondary actions stay quiet. Align to the grid; keep spacing on the theme scale.
- **States:** design empty, loading, error, long text, many rows and phone width, not only the happy path.
- **Motion:** only to explain a change, short, and off with `prefers-reduced-motion`.

## 2. Optimized, maintainable, built to scale

**Data and speed**

- Fetch only the attributes and rows the screen shows: Max Records, pagination, filters and sorting in the aggregate, not in loops.
- No aggregate, SQL or server call inside a For Each. Join, or fetch once and look up.
- One server call per screen action: put consecutive calls in one server action. Data not needed at start: Fetch Only On Demand.
- Add a non-unique index for attributes you filter, join or sort on in large entities (rule 5 allows new indexes).
- Keep IDs, not large records or lists, in client and session variables.
- Slow or bulk work (imports, mass emails, many external calls) goes to a timer or process, in batches with a checkpoint, not a screen request.
- External calls: a timeout, handled errors, never one call per row.

**Maintainable**

- Small server actions with clear names and descriptions; screen actions orchestrate. No copied flows: reuse one action.
- Put each element on the right layer of the Architecture Canvas: no upward or side references between end-user modules; core modules own entities and their writes (`oml doc architecture-rules`).
- Names say what things are; no `Action1`, `Aggregate2`, `Container3`.
- Errors: user exceptions with a clear message; log unexpected ones and keep the user informed.
- CSS in the theme or the block's style sheet, scoped to your class. No inline styles, no `!important`.

**Built to scale**

- Assume many users and many rows: what works for 10 rows must work for 100,000.
- No shared row, site property or file that every request writes.
- Cache only data that is read often and changes rarely, with an expiry.

**Before you report**

- Run `oml check --diff <source.oml> <staged.oml>` and fix the findings your change added.
- After a publish, run `oml browser page <url>` and read `slowCalls`, `layout` and `a11y`.
- Say what you did not optimize and why.
