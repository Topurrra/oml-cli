# Reactive UI rules

Lifecycle, block events, forms, tables, CSS, responsive and accessibility rules for Reactive Web and Mobile. Spec shape: `oml spec screens`.

## Screens and blocks

- A screen has a title, roles (`access`), inputs, locals, aggregates, data actions, actions and lifecycle events. A block has no title and no roles, but has OnParametersChanged and can define events.
- A detail screen takes only the record identifier and fetches the record itself.
- Each block instance must set every mandatory block input.
- The Common and Layouts flows serve all screens. Change them only when asked.

## Lifecycle

First load: OnInitialize; all aggregates and data actions start in parallel; OnReady; OnRender. Each finished fetch runs its OnAfterFetch, then OnRender again.

| Event | Use it for | Never |
|---|---|---|
| OnInitialize | defaults, values from inputs, redirect an unwanted user | read fetched data, call the server, read storage |
| OnReady (once) | start JS widgets, listeners, focus | read fetched data |
| OnRender (each data change) | DOM work | change screen data (endless loop), call the server |
| OnAfterFetch (`onAfterFetch`) | copy a value; start a dependent local storage fetch | refresh its own source (loop), chain server fetches (use one data action) |
| OnParametersChanged (block) | refresh aggregates that use inputs | expect it when the block changes its own input |
| OnDestroy | remove listeners, timers, widgets | slow work (it delays the next screen) |

- Child blocks run OnReady and OnRender before the parent; OnDestroy runs from the screen down.
- Assign variables used by aggregate filters in OnInitialize.
- A changed local re-renders its widgets. Aggregates do not run again by themselves: use `refreshData` (it waits for the fetch).
- `<Agg>.IsDataFetched` gives the loading state; `<Agg>.HasFetchError` the failed state.
- An on-demand source (`fetch: "onDemand"`, `oml spec fetch`) needs a `refreshData`, or it never runs. An at-start source cannot depend on an on-demand one; a source depends on one other source at most.
- In a screen transition both screens are in the DOM. Scope JavaScript to your own element, never `document`.
- Several server calls in one client action, or one inside a loop, are slow: make one server action do the work.
- Client variables live in the browser: basic types, literal default, cleared at sign-out, readable by the user. No secrets.

## Block communication

- Child to parent: the block defines an event (with optional inputs) and fires it with a Trigger Event node. The parent sets a handler action on the block instance; it receives the event's inputs. Spec: block `events`, node `triggerEvent`, instance `events` (`oml spec triggerEvent`).
- Events are mandatory by default: every instance needs a handler.
- Parent to child: change the values bound to block inputs. The block reacts in OnParametersChanged.
- A block aggregate that uses a block input must be refreshed in `onParametersChanged`.
- A public block must not navigate. Raise an event; the parent navigates.

## Forms and validation

- Put inputs in a Form. Inputs, Checkbox, Switch and Dropdown need a variable; its type decides what is valid.
- Keep Built-in Validations at Yes on the submit button (oml does). Empty mandatory inputs and wrong types are then marked before the action runs.
- Custom checks in the client action: `<Input>.Valid = False` and `<Input>.ValidationMessage = "Text"`, then branch on `<Form>.Valid` (read-only).
- Repeat every check in the server action. A user can skip client checks.
- Input Max Length is 50 by default. Match Max Length and Mandatory to the bound attribute.
- Bind a variable to one input widget only.
- Recipe: `form-with-validation.jsonc`.

## Tables, lists, sorting, paging

- Set `maxRecords` on every screen aggregate; for a Dropdown lookup, above the expected row count.
- Empty test: `<Agg>.List.Empty`, not `<Agg>.Count = 0`. Empty state: `<Agg>.IsDataFetched and <Agg>.List.Empty`.
- Paging: Integer locals `StartIndex` (0) and `MaxRecords` feed the aggregate's Start Index and Max Records; the total is `<Agg>.Count`. The OutSystems UI Pagination handler gets `NewStartIndex`: assign it to `StartIndex`, then `refreshData`. Reset `StartIndex` to 0 when a filter changes.
- Sorting: give sortable header cells a sort attribute. The table's On Sort handler gets the clicked column; keep it in a Text local, append `" DESC"` on a second click, use the local as the aggregate's dynamic sort (`"dynamic": true`), then `refreshData`. Sort attributes and On Sort are Service Studio only: tell the user to add them; the dynamic sort and its local work in the spec.
- Inputs inside a table bind through `<Agg>.List.Current`.
- The List widget renders only visible rows; the attribute `disable-virtualization` turns that off for items with blocks or JavaScript widgets.
- Recipe: `list-search-paging.jsonc`.

## Popups

Bind Show Popup to a Boolean local; client actions set it to open or close. With the WCAG 2.2 setting on, also close it on Escape.

## Layouts, themes and CSS

- Web layout placeholders: Title, MainContent (required), Breadcrumbs, Actions, Header, Menu, Footer. Mobile: HeaderLeft, Title, HeaderRight, HeaderContent, Content, Bottom.
- App layouts (LayoutTopMenu, LayoutSideMenu, LayoutBase, LayoutBlank) are copies in the app. Style them with the `ExtendedClass` input and theme CSS.
- Put CSS in the theme. Avoid screen and block style sheets, inline styles and `id` selectors.
- Style Classes is an expression: `"card"` or `If(IsLate, "tag-red", "tag-green")`.
- z-index: use the `--layer-*` tokens, such as `var(--layer-global-elevated)`; the top layer is 20.
- No `!important` except fixed states; no class names such as `.Red`; animate with CSS; `@import` first.

## Responsive

- `body` has `.desktop`, `.tablet` or `.phone`, and `.is--touch`. Write `.phone .my-card {}`.
- Phone is below 768 px; tablet is 768 to 1024 px.
- Use OutSystems UI Columns and DisplayOnDevice, not your own breakpoints. Class `no-responsive` stops full-width buttons on phones.
- `IsPhone()`, `IsTablet()`, `IsDesktop()` (OutSystemsUI) work in expressions.

## Accessibility musts

- Every screen has a title. Every Input has a Label whose Input Widget points to it.
- Images have `alt` (empty for decoration). Decorative icons: `aria-hidden="true"`. Icon-only buttons: `aria-label`. Widget HTML attributes (`alt`, `aria-*`, `disable-virtualization`) are Service Studio only: list them for the user to add.
- Headings: an `AdvancedHtml` widget (HTML Element) with tag `h1` to `h6`.
- `EnableAccessibilityFeatures` = True on the layout helps every screen; it is a layout change, so ask the user first.
- Contrast: 4.5 to 1 below 24 px text, 3 to 1 from 24 px.
- Use Button or Link for actions, not a clickable Container or Icon.

More: `oml doc ui-checklist`, `oml doc osui-patterns`.
